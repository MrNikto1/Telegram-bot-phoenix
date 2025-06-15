using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Data.Sqlite;
using DotNetEnv;
using System.Text;

namespace ShopBonusBot
{
    class Program
    {
        private static readonly QrCodeService _qrService = new();
        internal static readonly int BonusPercentage = 12; // 12% от суммы покупки
        public static string ApiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL")!;
        private static Timer? _cleanupTimer;
        private static readonly Dictionary<long, string> _userStates = new();
        static async Task Main()
        {
            Env.Load();
            string _token = Environment.GetEnvironmentVariable("TOKEN_TELEGRAM")!;

            var botClient = new TelegramBotClient(_token);

            using var cts = new CancellationTokenSource();
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = []
            };

            botClient.StartReceiving(
                HandleUpdateAsync,
                HandleErrorAsync,
                receiverOptions,
                cts.Token
            );

            InitializeDatabase();

            _cleanupTimer = new Timer(_ =>
            {
                _qrService.CleanupExpiredTokens();
                Console.WriteLine("Выполнена очистка просроченных QR-токенов");
            }, null, TimeSpan.Zero, TimeSpan.FromHours(1));

            if (string.IsNullOrEmpty(ApiBaseUrl))
            {
                Console.WriteLine("API_BASE_URL is not configured");
                Environment.Exit(1);
            }

            Console.WriteLine("Bot started. Press Enter to stop");
            await Task.Run(Console.ReadLine, cts.Token);
            _cleanupTimer.Dispose();
            cts.Cancel();
            Console.WriteLine("Bot stopped");
        }

        private static void InitializeDatabase()
        {
            using var connection = new SqliteConnection("Data Source=database/shopbonus.db");
            connection.Open();

            var createUsersTable = @"
            CREATE TABLE IF NOT EXISTS Users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChatId INTEGER UNIQUE NOT NULL,
                Phone TEXT NOT NULL,
                BonusBalance INTEGER NOT NULL DEFAULT 1000,
                RegistrationDate TEXT NOT NULL
            );";

            var createTransactionsTable = @"
            CREATE TABLE IF NOT EXISTS Transactions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Type TEXT NOT NULL CHECK(Type IN ('Accrual', 'WriteOff')),
                Amount INTEGER NOT NULL,
                Date TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Users(id)
            );";

            // Обновляем таблицу Users
            var alterTableCommand = @"
            ALTER TABLE Users 
            ADD COLUMN LastQrToken TEXT;
            ";

            try
            {
                using var command = new SqliteCommand(alterTableCommand, connection);
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Игнорируем ошибку "column already exists"
                Console.WriteLine("Columns already exist, skipping alter");
            }


            using (var command = new SqliteCommand(createUsersTable, connection))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SqliteCommand(createTransactionsTable, connection))
            {
                command.ExecuteNonQuery();
            }
        }

        private static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Error: {exception.Message}");
            return Task.CompletedTask;
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Type != UpdateType.Message || update.Message == null)
                return;
            if (update.Message.Type != MessageType.Text)
                return;

            var chatId = update.Message.Chat.Id;
            var messageText = update.Message.Text;

            try
            {
                using var conn = new SqliteConnection("Data Source=database/shopbonus.db");
                await conn.OpenAsync(cancellationToken);

                // Обработка состояний пользователя
                if (_userStates.TryGetValue(chatId, out var state))
                {
                    if (state == "awaiting_phone")
                    {
                        _userStates.Remove(chatId);
                        await RegisterUser(botClient, chatId, messageText.Trim(), conn, cancellationToken);
                        await ShowMainMenu(botClient, chatId, cancellationToken);
                        return;
                    }
                    else if (state == "awaiting_purchase_amount")
                    {
                        _userStates.Remove(chatId);
                        if (int.TryParse(messageText, out int amount))
                        {
                            await ProcessBonusPayment(botClient, chatId, amount, conn, cancellationToken);
                        }
                        else
                        {
                            await botClient.SendMessage(
                                chatId,
                                "❌ Некорректная сумма. Введите целое число",
                                cancellationToken: cancellationToken);
                        }
                        await ShowMainMenu(botClient, chatId, cancellationToken);
                        return;
                    }
                }

                // Обработка команд
                if (messageText == "/start")
                {
                    await HandleStartCommand(botClient, chatId, cancellationToken);
                }
                else if (messageText == "📱 Регистрация")
                {
                    await RequestPhoneNumber(botClient, chatId, cancellationToken);
                    _userStates[chatId] = "awaiting_phone";
                }
                else if (messageText == "💰 Мой баланс")
                {
                    await ShowBalance(botClient, chatId, conn, cancellationToken);
                }
                else if (messageText == "🛒 Оплатить бонусами")
                {
                    await GenerateAndSendPaymentQrCode(botClient, chatId, cancellationToken);
                }
                else if (messageText == "💳 Виртуальная карта")
                {
                    await GenerateAndSendQrCode(botClient, chatId, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await botClient.SendMessage(chatId, $"Ошибка: {ex.Message}", cancellationToken: cancellationToken);
            }
        }
        
        private static async Task GenerateAndSendPaymentQrCode(
            ITelegramBotClient botClient,
            long chatId,
            CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new SqliteConnection("Data Source=database/shopbonus.db");
                await conn.OpenAsync(cancellationToken);
                
                // Проверяем регистрацию пользователя
                var user = await GetUserInfo(chatId, conn, cancellationToken);
                if (user == null)
                {
                    await botClient.SendMessage(
                        chatId,
                        "❌ Вы не зарегистрированы! Пожалуйста, пройдите регистрацию сначала.",
                        cancellationToken: cancellationToken);
                    return;
                }

                // Проверяем баланс
                if (user.Balance <= 0)
                {
                    await botClient.SendMessage(
                        chatId,
                        "❌ У вас недостаточно бонусов для оплаты!",
                        cancellationToken: cancellationToken);
                    return;
                }

                // Генерируем QR-код для оплаты
                var (qrStream, token, expiry) = _qrService.GeneratePaymentQrCode(chatId, user.Balance);
                
                await botClient.SendPhoto(
                    chatId: chatId,
                    photo: InputFile.FromStream(qrStream, "payment_qr.png"),
                    caption: $"💳 QR-код для оплаты бонусами\n" +
                            $"Доступно: {user.Balance} бонусов\n" +
                            $"Действует до: {expiry:HH:mm}\n" +
                            "Покажите этот код кассиру для применения скидки",
                    cancellationToken: cancellationToken);
                
                qrStream.Dispose();
            }
            catch (Exception ex)
            {
                await botClient.SendMessage(
                    chatId,
                    $"❌ Ошибка при генерации QR-кода: {ex.Message}",
                    cancellationToken: cancellationToken);
            }
        }

        private static async Task GenerateAndSendQrCode(
            ITelegramBotClient botClient,
            long chatId,
            CancellationToken cancellationToken)
        {
            try
            {
                // Проверяем регистрацию пользователя
                bool isRegistered = await IsUserRegistered(chatId, cancellationToken);

                if (!isRegistered)
                {
                    await botClient.SendMessage(
                        chatId,
                        "❌ Вы не зарегистрированы!\n" +
                        "Пожалуйста, пройдите регистрацию сначала с помощью команды /start",
                        cancellationToken: cancellationToken);
                    return;
                }

                // Генерируем и отправляем QR-код
                var (qrStream, token, expiry) = _qrService.GenerateQrCode(chatId);

                await botClient.SendPhoto(
                    chatId: chatId,
                    photo: InputFile.FromStream(qrStream, "qrcode.png"),
                    caption: $"Ваша виртуальная карта\nДействует до: {expiry:HH:mm}\nПокажите продавцу QR-код",
                    cancellationToken: cancellationToken);

                qrStream.Dispose();
            }
            catch (Exception ex)
            {
                await botClient.SendMessage(
                    chatId,
                    $"❌ Ошибка генерации QR-кода: {ex.Message}",
                    cancellationToken: cancellationToken);
            }
        }
        
        private static async Task<UserInfo?> GetUserInfo(long chatId, CancellationToken ct)
        {
            using var conn = new SqliteConnection("Data Source=database/shopbonus.db");
            await conn.OpenAsync(ct);
            
            const string query = "SELECT id, Phone, BonusBalance FROM Users WHERE ChatId = @chatId LIMIT 1";
            
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@chatId", chatId);
            
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new UserInfo(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(2)
                );
            }
            
            return null;
        }

        private static async Task<UserInfo?> GetUserInfo(
            long chatId, 
            SqliteConnection conn, 
            CancellationToken ct)
        {
            const string query = "SELECT id, Phone, BonusBalance FROM Users WHERE ChatId = @chatId LIMIT 1";
            
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@chatId", chatId);
            
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new UserInfo(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(2)
                );
            }
            
            return null;
        }





        // Проверка регистрации пользователя
        private static async Task<bool> IsUserRegistered(long chatId, CancellationToken ct)
        {
            using var conn = new SqliteConnection("Data Source=database/shopbonus.db");
            await conn.OpenAsync(ct);

            const string query = "SELECT 1 FROM Users WHERE ChatId = @chatId LIMIT 1";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@chatId", chatId);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null;
        }

        private static async Task HandleStartCommand(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
        {
            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "📱 Регистрация" },
            })
            {
                ResizeKeyboard = true
            };
            await botClient.SendMessage(
                chatId,
                "Добро пожаловать в магазин! Выберите действие:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private static async Task RequestPhoneNumber(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
        {
            var requestButton = new ReplyKeyboardMarkup(new[]
            {
                KeyboardButton.WithRequestContact("📱 Отправить телефон")
            })
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };

            await botClient.SendMessage(
                chatId,
                "Для регистрации нажмите кнопку или введите номер телефона:",
                replyMarkup: requestButton,
                cancellationToken: cancellationToken);
        }

        private static async Task RegisterUser(
            ITelegramBotClient botClient,
            long chatId,
            string phone,
            SqliteConnection conn,
            CancellationToken cancellationToken)
        {
            // Нормализация номера телефона
            string normalizedPhone = NormalizePhoneNumber(phone);

            // Валидация номера
            if (!IsValidRussianPhoneNumber(normalizedPhone))
            {
                await botClient.SendMessage(
                    chatId,
                    "❌ Некорректный номер телефона. Введите номер в формате: 79XXXXXXXXX, +79XXXXXXXXX или 89XXXXXXXXX",
                    cancellationToken: cancellationToken);
                return;
            }

            // Проверка существующего пользователя
            using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM Users WHERE ChatId = @chatId", conn);
            checkCmd.Parameters.AddWithValue("@chatId", chatId);
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(cancellationToken));

            if (count > 0)
            {
                await botClient.SendMessage(
                    chatId,
                    "⚠️ Вы уже зарегистрированы!",
                    cancellationToken: cancellationToken);
                return;
            }

            // Регистрация нового пользователя
            using var cmd = new SqliteCommand(
                "INSERT INTO Users (ChatId, Phone, BonusBalance, RegistrationDate) " +
                "VALUES (@chatId, @phone, 1000, date('now'))",
                conn);
            cmd.Parameters.AddWithValue("@chatId", chatId);
            cmd.Parameters.AddWithValue("@phone", normalizedPhone);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            await botClient.SendMessage(
                chatId,
                "✅ Вы успешно зарегистрировались!\n" +
                $"Ваш номер: {FormatPhoneNumber(normalizedPhone)}",
                cancellationToken: cancellationToken);
        }


        // Метод для нормализации номера телефона
        private static string NormalizePhoneNumber(string phone)
        {
            // Удаляем все нецифровые символы, кроме возможного '+' в начале
            var digits = new StringBuilder();
            foreach (char c in phone)
            {
                if (char.IsDigit(c) || (c == '+' && digits.Length == 0))
                {
                    digits.Append(c);
                }
            }
            string normalized = digits.ToString();

            // Заменяем 8 на +7 в начале номера
            if (normalized.StartsWith("8") && normalized.Length > 1)
            {
                normalized = "+7" + normalized.Substring(1);
            }
            // Добавляем +7 если номер начинается с 9 и имеет 10 цифр
            else if (normalized.StartsWith("9") && normalized.Length == 10)
            {
                normalized = "+7" + normalized;
            }
            // Добавляем + если его нет и номер начинается с 7
            else if (normalized.StartsWith("7") && normalized.Length == 11 && !normalized.StartsWith("+"))
            {
                normalized = "+" + normalized;
            }

            return normalized;
        }

        // Метод для проверки валидности российского номера
        private static bool IsValidRussianPhoneNumber(string phone)
        {
            // Номер должен быть в формате +79XXXXXXXXX (11 цифр с кодом страны)
            if (phone.Length != 12 || !phone.StartsWith("+79"))
            {
                return false;
            }

            // Проверяем что остальные символы - цифры
            return phone.Substring(1).All(char.IsDigit);
        }

        // Метод для красивого форматирования номера
        private static string FormatPhoneNumber(string phone)
        {
            if (phone.StartsWith("+7") && phone.Length == 12)
            {
                return $"+7 ({phone.Substring(2, 3)}) {phone.Substring(5, 3)}-{phone.Substring(8, 2)}-{phone.Substring(10)}";
            }
            return phone; // Возвращаем как есть, если формат не распознан
        }

        private static async Task ShowMainMenu(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
        {
            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "💰 Мой баланс", "🛒 Оплатить бонусами", "💳 Виртуальная карта" }
            })
            {
                ResizeKeyboard = true
            };

            await botClient.SendMessage(
                chatId,
                "Главное меню:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private static async Task ShowBalance(
            ITelegramBotClient botClient,
            long chatId,
            SqliteConnection conn,
            CancellationToken cancellationToken)
        {
            using var cmd = new SqliteCommand("SELECT BonusBalance FROM Users WHERE ChatId = @chatId", conn);
            cmd.Parameters.AddWithValue("@chatId", chatId);
            var balance = await cmd.ExecuteScalarAsync(cancellationToken) as long?;

            if (balance.HasValue)
            {
                await botClient.SendMessage(
                    chatId,
                    $"💰 Ваш бонусный баланс: {balance} баллов",
                    cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendMessage(
                    chatId,
                    "❌ Вы не зарегистрированы. Пройдите регистрацию сначала",
                    cancellationToken: cancellationToken);
            }
        }

        private static async Task StartBonusPayment(
            ITelegramBotClient botClient,
            long chatId,
            SqliteConnection conn,
            CancellationToken cancellationToken)
        {
            using var cmd = new SqliteCommand("SELECT BonusBalance FROM Users WHERE ChatId = @chatId", conn);
            cmd.Parameters.AddWithValue("@chatId", chatId);
            var balance = await cmd.ExecuteScalarAsync(cancellationToken) as long?;

            if (!balance.HasValue)
            {
                await botClient.SendMessage(chatId, "❌ Вы не зарегистрированы", cancellationToken: cancellationToken);
                return;
            }

            if (balance == 0)
            {
                await botClient.SendMessage(chatId, "❌ У вас нет бонусов для оплаты", cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(
                chatId,
                $"💳 Введите сумму покупки (доступно: {balance} бонусов)\n" +
                "Можно оплатить до 50% стоимости покупки",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
        }
        private static async Task ProcessBonusPayment(
            ITelegramBotClient botClient,
            long chatId,
            int purchaseAmount,
            SqliteConnection conn,
            CancellationToken cancellationToken)
        {
            // Проверка корректности суммы покупки
            if (purchaseAmount <= 0)
            {
                await SendErrorMessage(botClient, chatId,
                    "❌ Сумма покупки должна быть больше нуля",
                    cancellationToken);
                return;
            }

            await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

            try
            {
                // 1. Получаем данные пользователя с блокировкой строки
                var user = await GetUserWithLock(conn, chatId, transaction, cancellationToken);

                if (user == null)
                {
                    await SendErrorMessage(botClient, chatId,
                        "❌ Вы не зарегистрированы",
                        cancellationToken);
                    return;
                }

                // 2. Проверяем достаточность бонусов
                if (user.BonusBalance <= 0)
                {
                    await SendErrorMessage(botClient, chatId,
                        "❌ У вас нет бонусов для оплаты",
                        cancellationToken);
                    return;
                }

                // 3. Рассчитываем доступные бонусы
                var bonusCalculation = CalculateAvailableBonus(user.BonusBalance, purchaseAmount);

                if (!bonusCalculation.CanUseBonus)
                {
                    await SendErrorMessage(botClient, chatId,
                        $"❌ Недостаточно бонусов для оплаты\n" +
                        $"Минимальная сумма покупки для использования бонусов: {bonusCalculation.MinPurchaseAmount} руб.",
                        cancellationToken);
                    return;
                }

                // 4. Обновляем баланс
                var newBalance = await UpdateUserBalance(
                    conn, transaction,
                    user.Id, bonusCalculation.BonusToUse,
                    cancellationToken);

                // 5. Записываем транзакцию
                await RecordTransaction(
                    conn, transaction,
                    user.Id, bonusCalculation.BonusToUse,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                // 6. Отправляем подтверждение пользователю
                await SendSuccessMessage(botClient, chatId,
                    $"✅ Успешно списано {bonusCalculation.BonusToUse} бонусов\n" +
                    $"💳 К оплате: {purchaseAmount - bonusCalculation.BonusToUse} руб.\n" +
                    $"💰 Новый баланс: {newBalance} бонусов",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                await TryRollback(transaction);
                await SendErrorMessage(botClient, chatId,
                    $"❌ Ошибка при обработке платежа: {ex.Message}",
                    cancellationToken);

                // Логируем полную ошибку для диагностики
                Console.WriteLine($"Payment error: {ex}");
            }
        }

        // Вспомогательные методы

        private record BonusCalculationResult(
            bool CanUseBonus,
            int BonusToUse,
            int MinPurchaseAmount);

        private record UserRecord(
            long Id,
            int BonusBalance);
            
        private record UserInfo(long Id, string Phone, int Balance);

        private static BonusCalculationResult CalculateAvailableBonus(
            int currentBalance,
            int purchaseAmount)
        {
            // Можно использовать до 50% от суммы покупки
            int maxPossibleBonus = purchaseAmount / 2;
            int actualBonus = Math.Min(currentBalance, maxPossibleBonus);

            // Минимальная сумма покупки для использования бонусов
            int minPurchaseForBonus = (currentBalance * 2) + 1;

            return new BonusCalculationResult(
                CanUseBonus: actualBonus > 0,
                BonusToUse: actualBonus,
                MinPurchaseAmount: minPurchaseForBonus
            );
        }

        private static async Task<UserRecord?> GetUserWithLock(
            SqliteConnection conn,
            long chatId,
            SqliteTransaction transaction,
            CancellationToken ct)
        {
            const string query = @"
                SELECT id, BonusBalance 
                FROM Users 
                WHERE ChatId = @chatId
                LIMIT 1";
            
            await using var cmd = new SqliteCommand(query, conn, transaction);
            cmd.Parameters.AddWithValue("@chatId", chatId);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new UserRecord(
                    reader.GetInt64(0),
                    reader.GetInt32(1));
            }
            
            return null;
        }

        private static async Task<int> UpdateUserBalance(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long userId,
            int bonusToDeduct,
            CancellationToken ct)
        {
            const string query = @"
                UPDATE Users 
                SET BonusBalance = BonusBalance - @bonus 
                WHERE id = @userId
                RETURNING BonusBalance";
            
            await using var cmd = new SqliteCommand(query, conn, transaction);
            cmd.Parameters.AddWithValue("@bonus", bonusToDeduct);
            cmd.Parameters.AddWithValue("@userId", userId);
            
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        private static async Task RecordTransaction(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long userId,
            int amount,
            CancellationToken ct)
        {
            const string query = @"
                INSERT INTO Transactions 
                (UserId, Type, Amount, Date) 
                VALUES (@userId, 'WriteOff', @amount, datetime('now'))";
            
            await using var cmd = new SqliteCommand(query, conn, transaction);
            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@amount", amount);
            
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task TryRollback(SqliteTransaction? transaction)
        {
            try
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Rollback failed: {ex}");
            }
        }

        private static Task SendErrorMessage(
            ITelegramBotClient botClient,
            long chatId,
            string message,
            CancellationToken ct)
        {
            return botClient.SendMessage(
                chatId,
                message,
                cancellationToken: ct);
        }

        private static Task SendSuccessMessage(
            ITelegramBotClient botClient,
            long chatId,
            string message,
            CancellationToken ct)
        {
            return botClient.SendMessage(
                chatId,
                message,
                cancellationToken: ct);
        }
    }
}