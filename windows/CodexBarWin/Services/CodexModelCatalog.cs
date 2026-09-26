using System.Text.Json;
using CodexBarWin.Models;

namespace CodexBarWin.Services;

public sealed record CodexModelOption(string Id, string[] Efforts, string[] Tiers);

/// <summary>只读本机 Codex 的模型目录；离线或旧目录时采用内置选项，允许手填模型。</summary>
public static class CodexModelCatalog
{
    public static IReadOnlyList<CodexModelOption> Load()
    {
        try
        {
            var path = Path.Combine(CodexPaths.CodexRoot, "models_cache.json");
            if (!File.Exists(path)) return Array.Empty<CodexModelOption>();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return Array.Empty<CodexModelOption>();
            return models.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object)
                .Select(m => new CodexModelOption(Read(m,"slug") ?? Read(m,"id") ?? "",
                    ReadArray(m,"supported_reasoning_levels","effort"),
                    new[] { "standard" }.Concat(ReadArray(m,"service_tiers","id").Select(t => t is "priority" ? "fast" : t)).Distinct().ToArray()))
                .Where(m => !string.IsNullOrWhiteSpace(m.Id)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return Array.Empty<CodexModelOption>(); }
    }
    public static string[] Models() => Load().Select(m => m.Id).Concat(CodexBarConfig.AvailableModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string[] Efforts(string model) => Load().FirstOrDefault(m => m.Id == model)?.Efforts is {Length: >0} efforts ? efforts : CodexBarConfig.AvailableReasoningEfforts;
    public static string[] Tiers(string model) => Load().FirstOrDefault(m => m.Id == model)?.Tiers ?? CodexBarConfig.AvailableServiceTiers;
    public static string? ConfigTier(string model, string? requested)
    {
        var tier = (requested ?? "standard").Trim().ToLowerInvariant();
        if (tier is "standard" or "flex" or "" or "default")
            return Tiers(model).Contains("default") ? "default" : null;
        if (tier == "priority") tier = "fast";
        return Tiers(model).Contains(tier) ? tier : null;
    }
    private static string? Read(JsonElement element,string key) => element.TryGetProperty(key,out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string[] ReadArray(JsonElement element,string key,string value) => element.TryGetProperty(key,out var array) && array.ValueKind == JsonValueKind.Array
        ? array.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.Object).Select(x=>Read(x,value)).Where(x=>!string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray() : Array.Empty<string>();
}
