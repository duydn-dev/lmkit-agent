using System.Text.RegularExpressions;
using System.Text.Json;

namespace LmKitOmniApi.Application.Chat;

/// <summary>
/// Trình phân tích kết quả trả về từ LLM để bóc tách các thẻ UI.
/// Biến chuỗi văn bản thành giao diện động (Server-Driven UI) cho PrimeVue.
/// </summary>
public class GenerativeUiResponseBuilder
{
    public static object ExtractGenerativeUiBlocks(string llmResponse)
    {
        var result = new
        {
            text = llmResponse,
            ui_components = new List<object>()
        };

        // Tìm kiếm các khối dữ liệu JSON được bọc trong thẻ <chart>...</chart>
        var chartMatch = Regex.Match(llmResponse, @"<chart>(.*?)</chart>", RegexOptions.Singleline);
        if (chartMatch.Success)
        {
            try
            {
                var jsonStr = chartMatch.Groups[1].Value;
                var data = JsonSerializer.Deserialize<object>(jsonStr);
                
                result.ui_components.Add(new
                {
                    type = "ui_component",
                    component = "PrimeVueChart",
                    props = new { data = data }
                });

                // Loại bỏ phần XML khỏi text hiển thị
                result = new { text = llmResponse.Replace(chartMatch.Value, "").Trim(), ui_components = result.ui_components };
            }
            catch { /* Bỏ qua nếu parse JSON lỗi */ }
        }

        return result;
    }

    public static string BuildChartComponent(string title, object data)
    {
        var jsonStr = JsonSerializer.Serialize(data);
        return $"<chart>{{\n  \"title\": \"{title}\",\n  \"data\": {jsonStr}\n}}</chart>";
    }
}
