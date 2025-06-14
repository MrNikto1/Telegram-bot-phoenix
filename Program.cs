using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Data.Sqlite;
using DotNetEnv;

namespace ShopBonusBot
{
    class Program
    {
        private static readonly QrCodeService _qrService = new();
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
                    await StartBonusPayment(botClient, chatId, conn, cancellationToken);
                    _userStates[chatId] = "awaiting_purchase_amount";
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

        private static async Task GenerateAndSendQrCode(
            ITelegramBotClient botClient, 
            long chatId, 
            CancellationToken cancellationToken)
        {
            try
            {
                var (qrStream, expiry) = _qrService.GenerateQrCode(chatId);
                
                await botClient.SendPhoto(
                    chatId: chatId,
                    photo: InputFile.FromStream(qrStream, "qrcode.png"),
                    caption: $"Ваша виртуальная карта\nДействует 10 минут.\nПокажите продавцу QR-код ",
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
            // Если пользователь отправил контакт
            if ((phone.StartsWith("8") && phone.Length == 11) || (phone.StartsWith("+7") && phone.Length == 12))
            {
                phone = phone.Trim();
            }
            else
            {
                await botClient.SendMessage(chatId, "❌ Некорректный номер телефона", cancellationToken: cancellationToken);
                return;
            }

            // Проверяем, есть ли уже такой пользователь
            using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM Users WHERE ChatId = @chatId", conn);
            checkCmd.Parameters.AddWithValue("@chatId", chatId);
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(cancellationToken));

            if (count > 0)
            {
                await botClient.SendMessage(chatId, "⚠️ Вы уже зарегистрированы!", cancellationToken: cancellationToken);
                return;
            }

            // Вставляем нового пользователя
            using var cmd = new SqliteCommand(
                "INSERT INTO Users (ChatId, Phone, BonusBalance, RegistrationDate) VALUES (@chatId, @phone, 1000, date('now'))",
                conn);
            cmd.Parameters.AddWithValue("@chatId", chatId);
            cmd.Parameters.AddWithValue("@phone", phone);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            await botClient.SendMessage(chatId, "✅ Вы успешно зарегистрировались!", cancellationToken: cancellationToken);
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
            // Проверка корректности суммы
            if (purchaseAmount <= 0)
            {
                await botClient.SendMessage(
                    chatId,
                    "❌ Сумма покупки должна быть больше нуля",
                    cancellationToken: cancellationToken);
                return;
            }

            var transaction = (SqliteTransaction?)await conn.BeginTransactionAsync(cancellationToken);
            try
            {
                // Получаем баланс с блокировкой строки
                var balanceCmd = new SqliteCommand("SELECT BonusBalance FROM Users WHERE ChatId = @chatId", conn, transaction);
                balanceCmd.Parameters.AddWithValue("@chatId", chatId);
                var balanceObj = await balanceCmd.ExecuteScalarAsync(cancellationToken);
                
                if (balanceObj == null)
                {
                    await botClient.SendMessage(chatId, "❌ Вы не зарегистрированы", cancellationToken: cancellationToken);
                    return;
                }
                
                long balance = Convert.ToInt64(balanceObj);
                
                // Рассчитываем максимальную скидку
                long maxBonus = Math.Min(balance, purchaseAmount / 2);
                
                if (maxBonus <= 0)
                {
                    await botClient.SendMessage(
                        chatId,
                        $"❌ Недостаточно бонусов для оплаты\n" +
                        $"Минимальная сумма покупки для использования бонусов: {balance * 2 + 1} руб.",
                        cancellationToken: cancellationToken);
                    return;
                }

                // Обновляем баланс
                var updateCmd = new SqliteCommand(
                    "UPDATE Users SET BonusBalance = BonusBalance - @bonus WHERE ChatId = @chatId",
                    conn, transaction);
                updateCmd.Parameters.AddWithValue("@bonus", maxBonus);
                updateCmd.Parameters.AddWithValue("@chatId", chatId);
                int updated = await updateCmd.ExecuteNonQueryAsync(cancellationToken);
                
                if (updated == 0)
                {
                    await botClient.SendMessage(chatId, "❌ Ошибка при списании бонусов", cancellationToken: cancellationToken);
                    await transaction.RollbackAsync(CancellationToken.None);
                    return;
                }

                // Фиксируем транзакцию
                var transCmd = new SqliteCommand(
                    "INSERT INTO Transactions (UserId, Type, Amount, Date) " +
                    "SELECT id, 'WriteOff', @amount, date('now') FROM Users WHERE ChatId = @chatId",
                    conn, transaction);
                transCmd.Parameters.AddWithValue("@amount", maxBonus);
                transCmd.Parameters.AddWithValue("@chatId", chatId);
                await transCmd.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync();
                
                await botClient.SendMessage(
                    chatId,
                    $"✅ Успешно списано {maxBonus} бонусов\n" +
                    $"💳 К оплате: {purchaseAmount - maxBonus} руб.",
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                try
                {
                    if (transaction != null)
                        await transaction.RollbackAsync(CancellationToken.None);
                }
                catch { }
                
                await botClient.SendMessage(
                    chatId,
                    $"❌ Ошибка при обработке платежа: {ex.Message}",
                    cancellationToken: cancellationToken);
            }
        }
    }
}