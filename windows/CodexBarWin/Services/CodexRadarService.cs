using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin.Services;

public sealed record CodexRadarPrediction(
    string Level,
    string LevelLabel,
    string ExpectedWindow,
    DateTimeOffset? UpdatedAt,
    bool IsAvailable)
{
    public static CodexRadarPrediction Loading { get; } = new("loading", "获取中", string.Empty, null, false);

    public static CodexRadarPrediction Unavailable { get; } = new("unknown", "暂不可用", string.Empty, null, false);

    public string DisplayText
    {
        get
        {
            if (!IsAvailable)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(ExpectedWindow)
                ? $"预测当前重置概率 · {LevelLabel}"
                : $"预测当前重置概率 · {LevelLabel} · {ExpectedWindow}";
        }
    }
}

public sealed class CodexRadarService
{
    private static readonly Uri CurrentUri = new("https://codexradar.com/current.json");
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private readonly object _gate = new();
    private CodexRadarPrediction _lastPrediction = CodexRadarPrediction.Loading;
    private DateTimeOffset _lastFetchedAt = DateTimeOffset.MinValue;

    public CodexRadarPrediction LastPrediction
    {
        get
        {
            lock (_gate)
            {
                return _lastPrediction;
            }
        }
    }

    public async Task<CodexRadarPrediction> GetCurrentAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!forceRefresh && DateTimeOffset.UtcNow - _lastFetchedAt < TimeSpan.FromMinutes(3))
            {
                return _lastPrediction;
            }
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CurrentUri);
            request.Headers.TryAddWithoutValidation("User-Agent", "WinCodexBar/1.0");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var prediction = Parse(document.RootElement);
            lock (_gate)
            {
                _lastPrediction = prediction;
                _lastFetchedAt = DateTimeOffset.UtcNow;
            }

            return prediction;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogService.LogException(ex, "codex radar current.json");
            lock (_gate)
            {
                _lastFetchedAt = DateTimeOffset.UtcNow;
                return _lastPrediction;
            }
        }
    }

    private static CodexRadarPrediction Parse(JsonElement root)
    {
        if (!root.TryGetProperty("prediction", out var prediction) || prediction.ValueKind != JsonValueKind.Object)
        {
            return CodexRadarPrediction.Unavailable;
        }

        var level = ReadString(prediction, "level") ?? "unknown";
        var expectedWindow = ReadString(prediction, "expected_window")
            ?? ReadString(prediction, "expected_windows")
            ?? string.Empty;
        var updatedAtText = ReadString(prediction, "updated_at") ?? ReadString(root, "monitored_at");
        DateTimeOffset? updatedAt = DateTimeOffset.TryParse(updatedAtText, out var parsed) ? parsed : null;
        return new CodexRadarPrediction(level, LevelLabel(level), expectedWindow, updatedAt, true);
    }

    private static string LevelLabel(string level)
    {
        return (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "high" => "高",
            "medium" or "mid" => "中",
            "low" => "低",
            "none" => "无",
            _ => "未知",
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
