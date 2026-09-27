using CodexBarWin.Models;
using CodexBarWin.Services;
using System.Text.Json;

namespace CodexBarWin.WinUI;

/// <summary>Records only usage reported by completed direct requests; never estimates missing tokens.</summary>
public sealed record AppUsageEvent(string Id, string Category, string Model, string AccountName,
    DateTimeOffset At, long InputTokens, long CachedInputTokens, long OutputTokens, long TotalTokens,
    long ImageInputTokens, long ImageOutputTokens, bool HasReportedUsage)
{
    public TokenCostBreakdown CostTokens => new(InputTokens, CachedInputTokens, OutputTokens, TotalTokens);
}

public static class AppUsageService
{
    public static string RecordsPath => Path.Combine(CodexPaths.CodexBarRoot, "app-usage");

    public static IReadOnlyList<AppUsageEvent> Load()
    {
        if (!Directory.Exists(RecordsPath)) return [];
        return Directory.EnumerateFiles(RecordsPath, "*.json")
            .Select(path =>
            {
                try { return JsonSerializer.Deserialize<AppUsageEvent>(File.ReadAllText(path)); }
                catch { return null; }
            })
            .OfType<AppUsageEvent>()
            .OrderBy(item => item.At)
            .ToArray();
    }

    public static AppUsageEvent FromCompletedResponse(JsonElement response, string category, string model, string accountName)
    {
        JsonElement source = default;
        var hasUsage = response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("usage", out source) && source.ValueKind == JsonValueKind.Object;
        var input = Read(source, "input_tokens");
        var output = Read(source, "output_tokens");
        var total = Read(source, "total_tokens");
        if (total == 0 && hasUsage) total = input + output;
        var details = GetObject(source, "input_tokens_details");
        var cached = Read(details, "cached_tokens");
        var imageInput = Read(source, "image_input_tokens");
        var imageOutput = Read(source, "image_output_tokens");
        return new AppUsageEvent(Guid.NewGuid().ToString("N"), category, model, accountName,
            DateTimeOffset.Now, input, cached, output, total, imageInput, imageOutput, hasUsage);
    }

    public static async Task SaveAsync(AppUsageEvent usage, CancellationToken token = default)
    {
        Directory.CreateDirectory(RecordsPath);
        await File.WriteAllTextAsync(Path.Combine(RecordsPath, usage.Id + ".json"), JsonSerializer.Serialize(usage), token);
    }

    private static JsonElement GetObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;

    private static long Read(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) ? Math.Max(0, number) : 0;
}
