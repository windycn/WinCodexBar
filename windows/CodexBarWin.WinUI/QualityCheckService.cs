using CodexBarWin.Models;
using CodexBarWin.Services;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexBarWin.WinUI;

public sealed record QualityCheckRecord(string Id, string AccountName, string Model, string Effort,
    string Prompt, string Html, DateTimeOffset CreatedAt, long DurationMs, int OutputTokens)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => $"{CreatedAt:MM-dd HH:mm} · {AccountName} · {Model} · {DurationMs / 1000d:0.0} 秒";
}

/// <summary>手动运行单账号 HTML 生成探针，保存原始结果，不推断模型“降智”分数。</summary>
public sealed class QualityCheckService : IDisposable
{
    private static readonly Uri Endpoint = new("https://chatgpt.com/backend-api/codex/responses");
    private const string Instructions = "Return a complete, self-contained HTML document for the user's request. Include all SVG, CSS and JavaScript inline. Do not use external resources. Return only HTML, without Markdown fences or explanations.";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly OpenAIOAuthRefreshService _refresh = new();
    private readonly AppState _state;
    public string RecordsPath => Path.Combine(CodexPaths.CodexBarRoot, "quality-checks");

    public QualityCheckService(AppState state) => _state = state;

    public IReadOnlyList<QualityCheckRecord> LoadRecords()
    {
        if (!Directory.Exists(RecordsPath)) return [];
        return Directory.EnumerateFiles(RecordsPath, "*.json")
            .Select(path =>
            {
                try { return JsonSerializer.Deserialize<QualityCheckRecord>(File.ReadAllText(path)); }
                catch { return null; }
            })
            .OfType<QualityCheckRecord>()
            .OrderByDescending(record => record.CreatedAt).ToArray();
    }

    public async Task<QualityCheckRecord> RunAsync(TokenAccount account, string model,
        string effort, string prompt, CancellationToken token)
    {
        if (!_state.Registry.Accounts.Any(item => item.AccountId == account.AccountId))
            throw new InvalidOperationException("请选择已保存的账号。");
        prompt = prompt.Trim();
        if (prompt.Length is < 1 or > 16000) throw new InvalidOperationException("提示词长度需要在 1 到 16000 字之间。");
        if (!CodexBarConfig.AvailableModels.Contains(model, StringComparer.Ordinal))
            throw new InvalidOperationException("不支持该模型。");
        if (effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new InvalidOperationException("不支持该思考强度。");
        if (account.ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var refreshed = await _refresh.RefreshAsync(account, token);
            if (refreshed.Outcome != OAuthRefreshOutcome.Refreshed)
                throw new InvalidOperationException("所选账号登录已过期，请在账号页重新登录。");
            _state.Registry.Save();
        }
        if (string.IsNullOrWhiteSpace(account.AccessToken)) throw new InvalidOperationException("所选账号缺少登录凭证。");
        var body = new
        {
            model, stream = true, store = false, instructions = Instructions,
            reasoning = new { effort },
            input = new[] { new { role = "user", content = new[] { new { type = "input_text", text = prompt } } } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("Originator", "codex-tui");
        request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/1.0.0");
        if (!string.IsNullOrWhiteSpace(account.OpenAIAccountId))
            request.Headers.TryAddWithoutValidation("Chatgpt-Account-Id", account.OpenAIAccountId);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"检测请求失败：HTTP {(int)response.StatusCode}。请检查账号权限和模型。" );
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var html = new StringBuilder();
        var outputTokens = 0;
        AppUsageEvent? reportedUsage = null;
        var completed = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(token);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (line == "data: [DONE]") break;
            try
            {
                using var document = JsonDocument.Parse(line[6..]);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type)) continue;
                switch (type.GetString())
                {
                    case "response.output_text.delta":
                        if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                            html.Append(delta.GetString());
                        break;
                    case "response.completed":
                        completed = true;
                        if (root.TryGetProperty("response", out var result))
                        {
                            reportedUsage = AppUsageService.FromCompletedResponse(result, "降智检测", model,
                                AccountUsageHelpers.DisplayName(account));
                            if (result.TryGetProperty("usage", out var usage) && usage.TryGetProperty("output_tokens", out var count))
                                outputTokens = count.GetInt32();
                            if (html.Length == 0 && result.TryGetProperty("output", out var output))
                                foreach (var item in output.EnumerateArray())
                                    if (item.TryGetProperty("content", out var parts))
                                        foreach (var part in parts.EnumerateArray())
                                            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                                                html.Append(text.GetString());
                        }
                        break;
                    case "response.failed": throw new InvalidOperationException("上游模型未完成检测请求。");
                }
                if (html.Length > 1024 * 1024) throw new InvalidOperationException("结果超过 1 MiB，已停止读取。");
            }
            catch (JsonException) { }
        }
        watch.Stop();
        if (!completed || html.Length == 0) throw new InvalidOperationException("没有收到完整的 HTML 结果。");
        var record = new QualityCheckRecord(Guid.NewGuid().ToString("N"), AccountUsageHelpers.DisplayName(account),
            model, effort, prompt, html.ToString(), DateTimeOffset.Now, watch.ElapsedMilliseconds, outputTokens);
        Directory.CreateDirectory(RecordsPath);
        await File.WriteAllTextAsync(Path.Combine(RecordsPath, record.Id + ".json"), JsonSerializer.Serialize(record), token);
        await AppUsageService.SaveAsync(reportedUsage ?? AppUsageService.FromCompletedResponse(default,
            "降智检测", model, AccountUsageHelpers.DisplayName(account)), token);
        return record;
    }

    public void Delete(QualityCheckRecord record) => File.Delete(Path.Combine(RecordsPath, record.Id + ".json"));
    public void Dispose() => _http.Dispose();
}
