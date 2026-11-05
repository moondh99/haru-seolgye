using System.Text;
using System.Text.Json;

namespace Haru.Api.Tests.Support;

/// <summary>Small helpers for calling the in-memory app.</summary>
public static class ApiHttp
{
    public static StringContent Json(object body) => Raw(JsonSerializer.Serialize(body));

    public static StringContent Raw(string body, string mediaType = "application/json") => new(body, Encoding.UTF8, mediaType);

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).GetProperty("error").GetProperty("code").GetString()!;

    public static async Task<IReadOnlyList<SseEvent>> ReadSseAsync(this HttpResponseMessage response) =>
        SseCapture.Parse(await response.Content.ReadAsStringAsync());

    public static string Header(this HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : "";

    /// <summary>A KST calendar day counted from the real today, in the format the API expects.</summary>
    public static string DayFromToday(int offset) =>
        DateOnly.ParseExact(Haru.Api.Infrastructure.Clock.TodayKst(), "yyyy-MM-dd").AddDays(offset).ToString("yyyy-MM-dd");
}
