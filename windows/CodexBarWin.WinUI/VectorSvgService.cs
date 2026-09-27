using CodexBarWin.Models;
using CodexBarWin.Services;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CodexBarWin.WinUI;

public sealed record VectorConversionResult(string Path, long DurationMs, AppUsageEvent Usage);

/// <summary>用明确选择的 Codex 账号和文本模型把图片重绘为可编辑的 SVG 元素。</summary>
public sealed class VectorSvgService : IDisposable
{
    private static readonly Uri Endpoint = new("https://chatgpt.com/backend-api/codex/responses");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(8) };
    private readonly OpenAIOAuthRefreshService _refresh = new();
    private readonly AppState _state;
    public VectorSvgService(AppState state) => _state = state;

    public async Task<VectorConversionResult> ConvertAsync(TokenAccount account, StudioImage image, string model,
        string effort, string guidance, CancellationToken token)
    {
        if (!_state.Registry.Accounts.Any(item => item.AccountId == account.AccountId))
            throw new InvalidOperationException("请选择已保存的账号。");
        if (model is not ("gpt-6-sol" or "gpt-6-astra" or "gpt-6-luna" or
            "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna"))
            throw new InvalidOperationException("不支持该矢量转换模型。");
        if (effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new InvalidOperationException("不支持该思考强度。");
        if (guidance.Length > 2000) throw new InvalidOperationException("矢量说明不能超过 2000 字。");
        var bytes = await File.ReadAllBytesAsync(image.Path, token);
        if (bytes.Length > 24 * 1024 * 1024) throw new InvalidOperationException("图片超过 24 MB，无法提交矢量转换。");
        if (account.ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var refreshed = await _refresh.RefreshAsync(account, token);
            if (refreshed.Outcome != OAuthRefreshOutcome.Refreshed)
                throw new InvalidOperationException("所选账号登录已过期，请重新登录。");
            _state.Registry.Save();
        }
        if (string.IsNullOrWhiteSpace(account.AccessToken)) throw new InvalidOperationException("所选账号缺少登录凭证。");
        var prompt = "将参考图片重绘为真正可编辑的 SVG 矢量图。使用 path、shape、text、gradient 等矢量元素重建主要轮廓、颜色和布局；不要嵌入位图，不要使用 image、foreignObject、脚本或外部资源。输出完整 SVG XML，仅输出 SVG，不要 Markdown。原图提示词：" + image.Prompt;
        if (!string.IsNullOrWhiteSpace(guidance)) prompt += "\n补充要求：" + guidance.Trim();
        var body = new
        {
            model, stream = true, store = false,
            instructions = "Return only a standalone SVG document with vector shapes and paths. Do not use embedded raster images, scripts, external resources, Markdown fences or explanations.",
            reasoning = new { effort },
            input = new[] { new { role = "user", content = new object[]
            {
                new { type = "input_text", text = prompt },
                new { type = "input_image", image_url = "data:image/png;base64," + Convert.ToBase64String(bytes) }
            } } }
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
            throw new InvalidOperationException($"矢量转换请求失败：HTTP {(int)response.StatusCode}。请检查所选账号和模型。" );
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var output = new StringBuilder();
        var complete = false;
        AppUsageEvent? reportedUsage = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(token);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (line == "data: [DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(line[6..]);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type)) continue;
                if (type.GetString() == "response.failed") throw new InvalidOperationException("上游未完成矢量转换。");
                if (type.GetString() == "response.output_text.delta" && root.TryGetProperty("delta", out var delta))
                    output.Append(delta.GetString());
                if (type.GetString() == "response.completed")
                {
                    complete = true;
                    if (root.TryGetProperty("response", out var result))
                        reportedUsage = AppUsageService.FromCompletedResponse(result, "SVG 转换", model,
                            AccountUsageHelpers.DisplayName(account));
                }
                if (output.Length > 1024 * 1024) throw new InvalidOperationException("SVG 超过 1 MiB，已停止读取。");
            }
            catch (JsonException) { }
        }
        if (!complete || output.Length == 0) throw new InvalidOperationException("没有收到完整的 SVG 结果。");
        var svg = ValidateSvg(output.ToString());
        var gallery = string.IsNullOrWhiteSpace(_state.Settings.Config.SvgGalleryPath)
            ? Path.Combine(CodexPaths.CodexBarRoot, "svg-gallery") : _state.Settings.Config.SvgGalleryPath;
        Directory.CreateDirectory(gallery);
        var id = GalleryFileName.Create(model);
        var path = Path.Combine(gallery, id + ".svg");
        await File.WriteAllTextAsync(path, svg, token);
        watch.Stop();
        var usage = reportedUsage ?? AppUsageService.FromCompletedResponse(default,
            "SVG 转换", model, AccountUsageHelpers.DisplayName(account));
        await AppUsageService.SaveAsync(usage, token);
        return new VectorConversionResult(path, watch.ElapsedMilliseconds, usage);
    }

    public static string ValidateSvg(string raw)
    {
        var start = raw.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        var end = raw.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < start) throw new InvalidDataException("模型没有返回 SVG 文档。");
        raw = raw[start..(end + 6)];
        using var text = new StringReader(raw);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024,
            MaxCharactersFromEntities = 0
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "svg") throw new InvalidDataException("SVG 根元素无效。");
        if (document.Descendants().Count() > 10_000) throw new InvalidDataException("SVG 元素过多。");
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "script", "image", "foreignObject", "iframe", "audio", "video", "use", "animate", "animateTransform", "set" };
        foreach (var element in document.Root.DescendantsAndSelf())
        {
            if (forbidden.Contains(element.Name.LocalName)) throw new InvalidDataException("SVG 含有脚本、位图或外部引用元素，已拒绝保存。");
            foreach (var attribute in element.Attributes())
            {
                // SVG 的 xmlns 是标准命名空间声明，不是图片或脚本的网络引用。
                if (attribute.IsNamespaceDeclaration)
                {
                    if (attribute.Value is not ("http://www.w3.org/2000/svg" or "http://www.w3.org/1999/xlink"))
                        throw new InvalidDataException("SVG 含有不支持的命名空间，已拒绝保存。");
                    continue;
                }
                var name = attribute.Name.LocalName;
                var value = attribute.Value;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("http:", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("https:", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("data:", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("@import", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SVG 含有外部资源或事件处理代码，已拒绝保存。");
            }
        }
        if (!document.Descendants().Any(element => element.Name.LocalName is "path" or "rect" or "circle" or "ellipse" or "polygon" or "polyline" or "line" or "text"))
            throw new InvalidDataException("模型没有生成可编辑的矢量形状。");
        return document.ToString(SaveOptions.DisableFormatting);
    }

    public void Dispose() => _http.Dispose();
}
