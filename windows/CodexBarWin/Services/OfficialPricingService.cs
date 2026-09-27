using CodexBarWin.Models;
using System.Globalization;
using System.Net.Http;
using System.Text;

namespace CodexBarWin.Services;

/// <summary>从 OpenAI 官方 Markdown 价格表读取标准短上下文 Token 价格。</summary>
public sealed class OfficialPricingService : IDisposable
{
    public const string SourceUrl = "https://developers.openai.com/api/docs/pricing.md";
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public OfficialPricingService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<IReadOnlyDictionary<string, TokenPricePreset>> FetchAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
        request.Headers.UserAgent.ParseAdd("WinCodexBar/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (limited.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("官方价格页面过大。");
            limited.Write(buffer, 0, count);
        }
        var parsed = ParseStandardPrices(Encoding.UTF8.GetString(limited.ToArray()));
        if (parsed.Count == 0) throw new InvalidDataException("官方页面没有可识别的标准价格表。");
        return parsed;
    }

    public static IReadOnlyDictionary<string, TokenPricePreset> ParseStandardPrices(string markdown)
    {
        var result = new Dictionary<string, TokenPricePreset>(StringComparer.OrdinalIgnoreCase);
        var inTable = false;
        foreach (var original in markdown.Split('\n'))
        {
            var line = original.Trim();
            if (line.StartsWith("### Standard pricing data", StringComparison.OrdinalIgnoreCase))
            {
                inTable = true;
                continue;
            }
            if (!inTable) continue;
            if (line.StartsWith("### ", StringComparison.Ordinal)) break;
            if (!line.StartsWith('|')) continue;
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 11) continue;
            var model = cells[1];
            if (model == "Model" || model.StartsWith("---", StringComparison.Ordinal)) continue;
            var noteAt = model.IndexOf(" (", StringComparison.Ordinal);
            if (noteAt >= 0) model = model[..noteAt];
            if (model.Length == 0 || model.Any(char.IsWhiteSpace)) continue;
            if (!ReadPrice(cells[2], out var input) || !ReadPrice(cells[5], out var output)) continue;
            _ = ReadPrice(cells[3], out var cached);
            result[model] = new TokenPricePreset
            {
                InputUsdPerMillion = input,
                CachedInputUsdPerMillion = cached,
                OutputUsdPerMillion = output,
                Source = "official",
                SyncedAt = DateTimeOffset.UtcNow
            };
        }
        return result;
    }

    private static bool ReadPrice(string cell, out double price)
    {
        var value = cell.Trim().TrimStart('$');
        var end = value.IndexOfAny([' ', '<', '*']);
        if (end >= 0) value = value[..end];
        return double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out price)
            && double.IsFinite(price) && price >= 0 && price <= 10_000;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
