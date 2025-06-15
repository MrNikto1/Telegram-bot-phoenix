using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ShopBonusBot.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DiscountController : ControllerBase
    {
        private readonly QrCodeService _qrService;

        public DiscountController(QrCodeService qrService)
        {
            _qrService = qrService;
        }

        [HttpPost("apply-discount")]
        public async Task<IActionResult> ApplyDiscount([FromBody] ApplyDiscountRequest request)
        {
            // Валидация токена
            var (valid, chatId) = _qrService.ValidateToken(request.Token);
            if (!valid)
            {
                return BadRequest(new { error = "Недействительный или просроченный QR-код" });
            }

            // Проверка суммы покупки
            if (request.PurchaseAmount <= 0)
            {
                return BadRequest(new { error = "Некорректная сумма покупки" });
            }

            using var conn = new SqliteConnection("Data Source=database/shopbonus.db");
            await conn.OpenAsync();

            // Получаем информацию о пользователе
            var user = await GetUserByChatId(conn, chatId);
            if (user == null)
            {
                return BadRequest(new { error = "Пользователь не найден" });
            }

            // Рассчитываем доступные бонусы
            int maxBonus =(int) request.PurchaseAmount / 2;
            int actualBonus = Math.Min(user.Balance, maxBonus);

            return Ok(new ApplyDiscountResponse
            {
                DiscountAmount = actualBonus,
                NewPurchaseAmount = request.PurchaseAmount - actualBonus,
                UserPhone = user.Phone
            });
        }

        [HttpPost("add-bonuses")]
        public async Task<IActionResult> AddBonuses([FromBody] AddBonusesRequest request)
        {
            // Валидация токена
            var (valid, chatId) = _qrService.ValidateToken(request.Token);
            if (!valid)
            {
                return BadRequest(new { error = "Недействительный или просроченный QR-код" });
            }

            // Рассчитываем бонусы (12% от суммы покупки)
            int bonuses = (int)(request.PurchaseAmount * ((int)(Program.BonusPercentage / 100.0)));

            using var conn = new SqliteConnection("Data Source=database/shopbonus.db");
            await conn.OpenAsync();

            // Начисляем бонусы
            await using var transaction = await conn.BeginTransactionAsync();
            try
            {
                // Обновляем баланс
                await using var updateCmd = new SqliteCommand(
                    "UPDATE Users SET BonusBalance = BonusBalance + @bonus WHERE ChatId = @chatId",
                    conn,(SqliteTransaction) transaction);
                updateCmd.Parameters.AddWithValue("@bonus", bonuses);
                updateCmd.Parameters.AddWithValue("@chatId", chatId);
                await updateCmd.ExecuteNonQueryAsync();

                // Записываем транзакцию
                await using var transCmd = new SqliteCommand(
                    "INSERT INTO Transactions (UserId, Type, Amount, Date) " +
                    "SELECT id, 'Accrual', @amount, datetime('now') FROM Users WHERE ChatId = @chatId",
                    conn, (SqliteTransaction) transaction);
                transCmd.Parameters.AddWithValue("@amount", bonuses);
                transCmd.Parameters.AddWithValue("@chatId", chatId);
                await transCmd.ExecuteNonQueryAsync();

                await transaction.CommitAsync();
                
                return Ok(new { message = $"Начислено {bonuses} бонусов" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { error = $"Ошибка начисления бонусов: {ex.Message}" });
            }
        }

        private async Task<UserInfo?> GetUserByChatId(SqliteConnection conn, long chatId)
        {
            await using var cmd = new SqliteCommand(
                "SELECT id, Phone, BonusBalance FROM Users WHERE ChatId = @chatId LIMIT 1", 
                conn);
            cmd.Parameters.AddWithValue("@chatId", chatId);
            
            await using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() 
                ? new UserInfo(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)) 
                : null;
        }
    }

    public class ApplyDiscountRequest
    {
        public string Token { get; set; } = string.Empty;
        public decimal PurchaseAmount { get; set; }
    }

    public class ApplyDiscountResponse
    {
        public decimal DiscountAmount { get; set; }
        public decimal NewPurchaseAmount { get; set; }
        public string UserPhone { get; set; } = string.Empty;
    }

    public class AddBonusesRequest
    {
        public string Token { get; set; } = string.Empty;
        public decimal PurchaseAmount { get; set; }
    }

    public record UserInfo(long Id, string Phone, int Balance);
}