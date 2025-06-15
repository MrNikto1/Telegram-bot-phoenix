using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public class EvotorDiscountManager
{
    private readonly string _apiToken;
    private readonly string _appToken;
    private readonly HttpClient _httpClient;

    public EvotorDiscountManager(string apiToken, string appToken)
    {
        _apiToken = apiToken;
        _appToken = appToken;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-Authorization", _apiToken);
    }

    public async Task ApplyDiscountAsync(string storeUuid, string productUuid, decimal discountPercent)
    {
        try
        {
            // 1. Получить текущие данные товара
            var product = await GetProductAsync(storeUuid, productUuid);
            
            // 2. Рассчитать новую цену
            var newPrice = product.Price * (1 - discountPercent / 100);
            product.Price = Math.Round(newPrice, 2);
            
            // 3. Обновить товар
            await UpdateProductAsync(storeUuid, product);
            
            // 4. Синхронизировать через вебхук
            await SyncViaWebhookAsync(storeUuid, product);
        }
        catch (HttpRequestException ex)
        {
            // Обработка ошибок API
            throw new ApplicationException($"API error: {ex.StatusCode}", ex);
        }
    }

    private async Task<Product> GetProductAsync(string storeUuid, string productUuid)
    {
        var url = $"https://api.evotor.ru/api/v1/inventories/stores/{storeUuid}/products";
        var response = await _httpClient.GetAsync(url);
        
        response.EnsureSuccessStatusCode();
        
        var content = await response.Content.ReadAsStringAsync();
        var products = JsonSerializer.Deserialize<List<Product>>(content);
        
        return products?.Find(p => p.Uuid == productUuid) 
            ?? throw new KeyNotFoundException("Product not found");
    }

    private async Task UpdateProductAsync(string storeUuid, Product product)
    {
        var url = $"https://api.evotor.ru/api/v1/inventories/stores/{storeUuid}/products";
        var content = new StringContent(
            JsonSerializer.Serialize(new List<Product> { product }),
            Encoding.UTF8,
            "application/json");
        
        var response = await _httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
    }

    private async Task SyncViaWebhookAsync(string storeUuid, Product product)
    {
        var url = $"https://partner.ru/api/v1/inventories/stores/{storeUuid}/products";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _appToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new List<Product> { product }),
            Encoding.UTF8,
            "application/json");
        
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private class Product
    {
        public string Uuid { get; set; }
        public string Name { get; set; }
        public bool Group { get; set; }
        public string ParentUuid { get; set; }
        public string Type { get; set; } = "NORMAL";
        public decimal Quantity { get; set; }
        public string MeasureName { get; set; } = "шт";
        public string Tax { get; set; } = "NO_VAT";
        public decimal Price { get; set; }
        public bool AllowToSell { get; set; } = true;
        public decimal CostPrice { get; set; }
        public string Description { get; set; }
        public string ArticleNumber { get; set; }
        public string Code { get; set; }
        public List<string> BarCodes { get; set; }
        public object Attributes { get; set; }
    }
}