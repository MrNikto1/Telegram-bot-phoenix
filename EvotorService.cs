using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

public class EvotorService
{
    private readonly string _orgUuid;
    private readonly string _accessToken;
    private readonly ILogger<EvotorService> _logger;
    private readonly HttpClient _httpClient;

    public EvotorService(
        string orgUuid, 
        string accessToken,
        ILogger<EvotorService> logger,
        HttpClient httpClient)
    {
        _orgUuid = orgUuid;
        _accessToken = accessToken;
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<string> CreateReceiptWithDiscount(
        string userId,
        decimal totalAmount,
        decimal discountAmount,
        string userPhone)
    {
        try
        {
            // Формируем запрос
            var payload = new
            {
                uuid = Guid.NewGuid().ToString(),
                organization_uuid = _orgUuid,
                type = "SALE",
                items = new[]
                {
                    new 
                    {
                        name = "Покупка с бонусами",
                        quantity = 1,
                        price = totalAmount,
                        sum = totalAmount,
                        payment_type = "FULL_PAYMENT",
                        payment_method = "CARD"
                    }
                },
                discounts = new[]
                {
                    new 
                    {
                        value = discountAmount,
                        description = $"Бонусная скидка ({userPhone})"
                    }
                },
                client_info = new
                {
                    phone = userPhone
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            // Настраиваем заголовки
            _httpClient.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", _accessToken);
            _httpClient.DefaultRequestHeaders.Add("X-version", "2.0");

            // Отправляем запрос
            var response = await _httpClient.PostAsync(
                $"https://api.evotor.ru/api/v1/organizations/{_orgUuid}/receipts", 
                content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Evotor API error: {response.StatusCode} - {errorContent}");
                throw new Exception($"Evotor API error: {response.StatusCode}");
            }

            // Обрабатываем успешный ответ
            var responseBody = await response.Content.ReadAsStringAsync();
            var responseObj = JsonSerializer.Deserialize<EvotorReceiptResponse>(responseBody);
            
            return responseObj?.Uuid ?? "unknown_uuid";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating receipt in Evotor");
            throw;
        }
    }

    public async Task AddBonusesToReceipt(
        string receiptUuid,
        decimal purchaseAmount,
        int bonusesAdded)
    {
        try
        {
            // Формируем запрос на добавление информации о бонусах
            var payload = new
            {
                extra = new
                {
                    bonus_program = new
                    {
                        earned = bonusesAdded,
                        purchase_amount = purchaseAmount
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            // Отправляем запрос
            var response = await _httpClient.PatchAsync(
                $"https://api.evotor.ru/api/v1/organizations/{_orgUuid}/receipts/{receiptUuid}", 
                content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Evotor update error: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating receipt with bonus info");
        }
    }

    private class EvotorReceiptResponse
    {
        public string Uuid { get; set; } = string.Empty;
    }
}