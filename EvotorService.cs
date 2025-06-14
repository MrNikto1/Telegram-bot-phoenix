using System.Text;
using System.Text.Json;

public class EvotorService
{
    private readonly string _orgUuid;
    private readonly string _accessToken;

    public EvotorService(string orgUuid, string accessToken)
    {
        _orgUuid = orgUuid;
        _accessToken = accessToken;
    }

    public async Task<string?> CreateReceiptWithDiscound(string chatId, decimal totalAmount, decimal discountAmount)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", "" + _accessToken);
        client.DefaultRequestHeaders.Add("X-version", "2.0");

        var payload = new
        {
            uuid = chatId,
            organization_uuid = _orgUuid,
            type = "SALE",
            items = new[]
            {
                new {
                    name = "Товар",
                    quanty = 1,
                    price = totalAmount,
                    sum = totalAmount,
                    payment_type = "FULL_PAYMENT",
                    payment_method = "CARD"
                }
            },

            discount = new[]
            {
                new {
                    value = discountAmount,
                    description = "Скидка_Виртуальной_карты"
                }
            }

        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var respone = await client.PostAsync($"https://api.evotor.ru/api/v1/organizations/{_orgUuid}/receipts", content);

        if (respone.IsSuccessStatusCode)
        {
            var responeBody = await respone.Content.ReadAsByteArrayAsync();
            return "receipt_created"; // В будущем здесь будет UUID чека
        }

        return null;
    }
}