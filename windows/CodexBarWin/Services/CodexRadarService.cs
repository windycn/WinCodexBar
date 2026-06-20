using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin.Services;

public sealed record CodexRadarPrediction(
    string Level,
    string LevelLabel,
    string ExpectedWindow,
    double? Probability24h,
    double? Probability48h,
    DateTimeOffset? UpdatedAt,
    bool IsAvailable)
{
    public static CodexRadarPrediction Loading { get; } = new("loading", "获取中", string.Empty, null, null, null, false);

    public static CodexRadarPrediction Unavailable { get; } = new("unknown", "暂不可用", string.Empty, null, null, null, false);

    public string DisplayText
    {
        get
        {
            if (!IsAvailable)
            {
                return string.Empty;
            }

            var parts = new List<string> { "预测当前重置概率", LevelLabel };
            var probability = ProbabilityDisplayText();
            if (!string.IsNullOrWhiteSpace(probability))
            {
                parts.Add(probability);
            }

            if (!string.IsNullOrWhiteSpace(ExpectedWindow))
            {
                parts.Add(ExpectedWindow);
            }

            return string.Join(" · ", parts);
        }
    }

    private string ProbabilityDisplayText()
    {
        var parts = new List<string>();
        AddProbability(parts, "24h", Probability24h);
        AddProbability(parts, "48h", Probability48h);
        return string.Join(" / ", parts);
    }

    private static void AddProbability(List<string> parts, string label, double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || value.Value < 0)
        {
            return;
        }

        var percent = value.Value <= 1d ? value.Value * 100d : value.Value;
        parts.Add($"{label} {percent.ToString("F0", CultureInfo.InvariantCulture)}%");
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
        var expectedWindow = NormalizeWhitespace(ReadString(prediction, "expected_window")
            ?? ReadString(prediction, "expected_windows")
            ?? string.Empty);
        var probability24h = ReadDouble(prediction, "probability_24h");
        var probability48h = ReadDouble(prediction, "probability_48h");
        var updatedAtText = ReadString(prediction, "updated_at") ?? ReadString(root, "monitored_at");
        DateTimeOffset? updatedAt = DateTimeOffset.TryParse(updatedAtText, out var parsed) ? parsed : null;
        return new CodexRadarPrediction(level, LevelLabel(level), expectedWindow, probability24h, probability48h, updatedAt, true);
    }

    private static string LevelLabel(string level)
    {
        return (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "very_high" => "很高",
            "high" => "高",
            "medium_high" => "中高",
            "medium" or "mid" => "中",
            "medium_low" => "中低",
            "low" => "低",
            "very_low" => "很低",
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

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(" ", value.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
    }
}
