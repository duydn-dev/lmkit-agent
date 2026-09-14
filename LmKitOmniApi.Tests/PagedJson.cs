using System.Net.Http.Json;
using System.Text.Json;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Đọc <c>items</c> từ shape getlist chuẩn <c>PagedResult&lt;T&gt;</c>
/// (<c>{ items, page, pageSize, totalCount, totalPages }</c>) mà các endpoint
/// quản lý trả về sau đợt chuẩn hóa phân trang — giữ phần thân test viết theo
/// mảng như cũ.
/// </summary>
internal static class PagedJson
{
    public static JsonElement[] Items(string rawBody)
        => JsonSerializer.Deserialize<JsonElement>(rawBody).GetProperty("items").EnumerateArray().ToArray();

    public static async Task<JsonElement[]> ItemsAsync(HttpClient client, string url)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(url);
        return page.GetProperty("items").EnumerateArray().ToArray();
    }
}
