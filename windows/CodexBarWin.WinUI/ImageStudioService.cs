using CodexBarWin.Models;
using CodexBarWin.Services;
using System.Net.Http.Headers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace CodexBarWin.WinUI;

public sealed record StudioImage(string Path, string Prompt, string AccountName, DateTimeOffset CreatedAt)
{
    public string RequestModel { get; init; } = string.Empty;
    public string Effort { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Quality { get; init; } = string.Empty;
    public int ReferenceCount { get; init; }
    public string RevisedPrompt { get; init; } = string.Empty;
    public string SvgPath { get; init; } = string.Empty;
    public string SvgModel { get; init; } = string.Empty;
    public DateTimeOffset? SvgCreatedAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string CreatedLabel => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    [System.Text.Json.Serialization.JsonIgnore]
    public string DetailLabel => $"{CreatedLabel} · {AccountName} · {RequestModel} ({Effort}) → {Model} · {Size} · {Quality}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ModelLabel => $"{Model} · {Size}";
    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage Thumbnail => new(new Uri(Path)) { DecodePixelWidth = 240 };
}

public sealed record VectorGalleryItem(string Path, string SourcePath, string Prompt, string AccountName,
    string Model, string Effort, DateTimeOffset CreatedAt)
{
    public long? DurationMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }
    public bool HasReportedUsage { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DetailLabel => $"{CreatedAt:yyyy-MM-dd HH:mm} · {AccountName} · {ModelLabel} · {MetricsLabel}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string CreatedLabel => CreatedAt.ToString("yyyy-MM-dd HH:mm");
    [System.Text.Json.Serialization.JsonIgnore]
    public string ModelLabel => $"{Model} · {Effort switch { "low" => "轻度", "medium" => "中等", "high" => "较高", "xhigh" => "高", "max" => "最高", _ => Effort }}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string MetricsLabel => $"{(DurationMs.HasValue ? DurationMs.Value / 1000d < 60 ? $"{DurationMs.Value / 1000d:0.#} 秒" : $"{DurationMs.Value / 60000d:0.#} 分" : "用时未记录")} · {(HasReportedUsage ? FormatTokens(TotalTokens) + " Token" : "Token 未记录")}";
    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Media.ImageSource Thumbnail => SvgPreview.Thumbnail(Path);

    private static string FormatTokens(long count) => count switch
    {
        >= 100_000_000 => $"{count / 100_000_000d:0.##}亿",
        >= 10_000 => $"{count / 10_000d:0.##}万",
        >= 1_000 => $"{count / 1_000d:0.#}千",
        _ => count.ToString("N0")
    };
}

/// <summary>单账号直连 Codex 生图端点；不提供反向代理、账号池或自动换号。</summary>
public sealed class ImageStudioService : IDisposable
{
    private static readonly Uri Endpoint = new("https://chatgpt.com/backend-api/codex/responses");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(8) };
    private readonly OpenAIOAuthRefreshService _refresh = new();
    private readonly AppState _state;

    public ImageStudioService(AppState state) => _state = state;
    public string GalleryPath => string.IsNullOrWhiteSpace(_state.Settings.Config.ImageGalleryPath)
        ? Path.Combine(CodexPaths.CodexBarRoot, "image-studio") : _state.Settings.Config.ImageGalleryPath;
    public string VectorGalleryPath => string.IsNullOrWhiteSpace(_state.Settings.Config.SvgGalleryPath)
        ? Path.Combine(CodexPaths.CodexBarRoot, "svg-gallery") : _state.Settings.Config.SvgGalleryPath;

    public static string ResolveSize(string aspect, string resolution) => (aspect, resolution) switch
    {
        ("1024x1024", "1k") => "1024x1024",
        ("1536x1024", "1k") => "1536x1024",
        ("1024x1536", "1k") => "1024x1536",
        ("1024x1024", "2k") => "2048x2048",
        ("1536x1024", "2k") => "2048x1152",
        ("1024x1536", "2k") => "1152x2048",
        ("1024x1024", "4k") => "2880x2880",
        ("1536x1024", "4k") => "3840x2160",
        ("1024x1536", "4k") => "2160x3840",
        _ => throw new InvalidOperationException("不支持该画幅或分辨率组合。")
    };

    public IReadOnlyList<VectorGalleryItem> LoadVectorGallery()
    {
        MigrateLegacyVectors();
        var items = GalleryStorage.Load<VectorGalleryItem>(VectorGalleryPath, ".svg", item => item.Path);
        if (items.Any(item => !item.HasReportedUsage))
        {
            var usage = AppUsageService.Load().Where(item => item.Category == "SVG 转换" && item.HasReportedUsage).ToArray();
            items = items.Select(item =>
            {
                if (item.HasReportedUsage) return item;
                var match = usage.Where(entry => entry.Model == item.Model && entry.AccountName == item.AccountName)
                    .OrderBy(entry => Math.Abs((entry.At - item.CreatedAt).TotalSeconds)).FirstOrDefault();
                if (match is null || Math.Abs((match.At - item.CreatedAt).TotalMinutes) > 2) return item;
                var updated = item with
                {
                    InputTokens = match.InputTokens, OutputTokens = match.OutputTokens,
                    TotalTokens = match.TotalTokens, HasReportedUsage = true
                };
                GalleryStorage.SaveMetadata(VectorGalleryPath, item.Path, updated);
                return updated;
            }).ToArray();
        }
        return items
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    private void MigrateLegacyVectors()
    {
        foreach (var image in LoadGallery())
        {
            if (string.IsNullOrWhiteSpace(image.SvgPath) || !File.Exists(image.SvgPath)) continue;
            if (Path.GetDirectoryName(Path.GetFullPath(image.SvgPath))?.Equals(
                    Path.GetFullPath(GalleryPath), StringComparison.OrdinalIgnoreCase) != true) continue;
            if (Path.GetDirectoryName(Path.GetFullPath(image.SvgPath))?.Equals(
                    Path.GetFullPath(VectorGalleryPath), StringComparison.OrdinalIgnoreCase) == true) continue;
            var target = Path.Combine(VectorGalleryPath,
                GalleryFileName.Create(string.IsNullOrWhiteSpace(image.SvgModel) ? "svg" : image.SvgModel) + ".svg");
            Directory.CreateDirectory(VectorGalleryPath);
            File.Copy(image.SvgPath, target);
            SaveVectorResult(image, target, image.SvgModel, "未记录", image.AccountName);
        }
    }

    public async Task<StudioImage> ImportImageAsync(string sourcePath, CancellationToken token = default)
    {
        if (Path.GetExtension(sourcePath).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new InvalidDataException("仅支持 PNG、JPG 和 WebP 图片。");
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length is < 100 or > 24 * 1024 * 1024)
            throw new InvalidDataException("图片不存在或大小超出 24 MB 限制。");
        byte[] png = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var source = new Bitmap(sourcePath);
            if ((long)source.Width * source.Height > 40_000_000)
                throw new InvalidDataException("图片像素超过 4000 万，无法导入。");
            using var output = new MemoryStream();
            source.Save(output, ImageFormat.Png);
            return output.ToArray();
        }, token);
        var galleryRoot = GalleryPath;
        var id = GalleryFileName.Create("import");
        var path = Path.Combine(galleryRoot, id + ".png");
        var item = new StudioImage(path, "本机导入 · " + Path.GetFileName(sourcePath), "本机导入", DateTimeOffset.Now)
        { Model = "本机图片", Size = "原图" };
        await Task.Run(() => GalleryStorage.SaveMedia(galleryRoot, path, png, item), token);
        return item;
    }

    public VectorGalleryItem SaveVectorResult(StudioImage source, string svgPath, string model, string effort, string accountName,
        long? durationMs = null, AppUsageEvent? usage = null)
    {
        var item = new VectorGalleryItem(svgPath, source.Path, source.Prompt, accountName, model, effort, DateTimeOffset.Now)
        {
            DurationMs = durationMs,
            InputTokens = usage?.InputTokens ?? 0,
            OutputTokens = usage?.OutputTokens ?? 0,
            TotalTokens = usage?.TotalTokens ?? 0,
            HasReportedUsage = usage?.HasReportedUsage ?? false
        };
        GalleryStorage.SaveMetadata(VectorGalleryPath, svgPath, item);
        AttachSvg(source, svgPath, model);
        return item;
    }

    public IReadOnlyList<StudioImage> LoadGallery()
    {
        return GalleryStorage.Load<StudioImage>(GalleryPath, ".png", item => item.Path)
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public async Task<StudioImage> GenerateAsync(TokenAccount account, string prompt, string requestModel, string model,
        string size, string quality, string effort, IReadOnlyList<string> referencePaths, CancellationToken cancellationToken)
    {
        if (!_state.Registry.Accounts.Any(item => item.AccountId == account.AccountId))
            throw new InvalidOperationException("请选择已保存的账号。");
        prompt = prompt.Trim();
        if (prompt.Length is < 1 or > 8000) throw new InvalidOperationException("描述长度需要在 1 到 8000 字之间。");
        if (requestModel is not ("gpt-6-luna" or "gpt-6-sol" or "gpt-6-astra" or
            "gpt-5.6-luna" or "gpt-5.6-terra" or "gpt-5.6-sol"))
            throw new InvalidOperationException("不支持该执行模型。");
        if (model is not ("gpt-image-2" or "gpt-image-2.5-flare" or "gpt-image-2.5-sunburst"))
            throw new InvalidOperationException("不支持该生图模型。");
        if (size is not ("1024x1024" or "1536x1024" or "1024x1536" or
            "2048x2048" or "2048x1152" or "1152x2048" or
            "2880x2880" or "3840x2160" or "2160x3840"))
            throw new InvalidOperationException("不支持该尺寸。");
        if (quality is not ("low" or "medium" or "high" or "xhigh" or "max")) throw new InvalidOperationException("不支持该质量。");
        if (effort is not ("low" or "medium" or "high" or "xhigh" or "max")) throw new InvalidOperationException("不支持该思考强度。");
        if (model == "gpt-image-2" && quality is "xhigh" or "max")
            throw new InvalidOperationException("极高和最高质量仅适用于 GPT Image 2.5 系列。");
        if (account.ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var refresh = await _refresh.RefreshAsync(account, cancellationToken);
            if (refresh.Outcome != OAuthRefreshOutcome.Refreshed)
                throw new InvalidOperationException("所选账号登录已过期，请在账号页重新登录。");
            _state.Registry.Save();
        }
        if (string.IsNullOrWhiteSpace(account.AccessToken))
            throw new InvalidOperationException("所选账号缺少登录凭证。");

        var content = new List<object> { new { type = "input_text", text = prompt } };
        if (referencePaths.Count > 8) throw new InvalidOperationException("最多添加 8 张参考图。");
        var totalReferenceBytes = 0L;
        foreach (var referencePath in referencePaths)
        {
            var bytes = await File.ReadAllBytesAsync(referencePath, cancellationToken);
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidOperationException("参考图不能超过 16 MB。");
            totalReferenceBytes += bytes.Length;
            if (totalReferenceBytes > 32 * 1024 * 1024) throw new InvalidOperationException("参考图总大小不能超过 32 MB。");
            var ext = Path.GetExtension(referencePath).ToLowerInvariant();
            var mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => throw new InvalidOperationException("仅支持 PNG、JPG 和 WebP 参考图。") };
            content.Add(new { type = "input_image", image_url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" });
        }
        var requestBody = new
        {
            instructions = "",
            stream = true,
            reasoning = new { effort, summary = "auto" },
            parallel_tool_calls = true,
            include = new[] { "reasoning.encrypted_content" },
            model = requestModel,
            store = false,
            tool_choice = new { type = "image_generation" },
            input = new[] { new { type = "message", role = "user", content } },
            tools = new[] { new { type = "image_generation", action = referencePaths.Count == 0 ? "generate" : "edit", model, size, quality, output_format = "png" } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("Originator", "codex-tui");
        request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/1.0.0");
        if (!string.IsNullOrWhiteSpace(account.OpenAIAccountId))
            request.Headers.TryAddWithoutValidation("Chatgpt-Account-Id", account.OpenAIAccountId);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"所选账号生图请求失败：HTTP {(int)response.StatusCode}。请检查该账号是否支持生图。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? imageBase64 = null;
        string? revisedPrompt = null;
        string? upstreamError = null;
        AppUsageEvent? reportedUsage = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type)) continue;
                if (type.GetString() == "error" || type.GetString() == "response.failed")
                    upstreamError = "上游拒绝了生图请求，请检查账号额度或稍后重试。";
                if (type.GetString() == "response.output_item.done" && root.TryGetProperty("item", out var item))
                {
                    imageBase64 = ImageResult(item) ?? imageBase64;
                    revisedPrompt = RevisedPrompt(item) ?? revisedPrompt;
                }
                if (type.GetString() == "response.completed" && root.TryGetProperty("response", out var completed))
                {
                    reportedUsage = AppUsageService.FromCompletedResponse(completed, "生图", model, AccountUsageHelpers.DisplayName(account));
                    if (completed.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                        foreach (var outputItem in output.EnumerateArray())
                        {
                            imageBase64 = ImageResult(outputItem) ?? imageBase64;
                            revisedPrompt = RevisedPrompt(outputItem) ?? revisedPrompt;
                        }
                }
            }
            catch (JsonException) { }
        }
        if (imageBase64 is null) throw new InvalidOperationException(upstreamError ?? "该账号没有返回图片；请确认所选模型可用。");
        byte[] image;
        try { image = Convert.FromBase64String(imageBase64); }
        catch (FormatException) { throw new InvalidOperationException("返回的图片数据无效。"); }
        if (image.Length is < 100 or > 50 * 1024 * 1024) throw new InvalidOperationException("返回的图片尺寸异常。");
        var galleryRoot = GalleryPath;
        var id = GalleryFileName.Create(model);
        var path = Path.Combine(galleryRoot, id + ".png");
        var result = new StudioImage(path, prompt, AccountUsageHelpers.DisplayName(account), DateTimeOffset.Now)
        {
            RequestModel = requestModel, Effort = effort, Model = model, Size = size, Quality = quality,
            ReferenceCount = referencePaths.Count, RevisedPrompt = revisedPrompt ?? string.Empty
        };
        await Task.Run(() => GalleryStorage.SaveMedia(galleryRoot, path, image, result), cancellationToken);
        await AppUsageService.SaveAsync(reportedUsage ?? AppUsageService.FromCompletedResponse(default,
            "生图", model, AccountUsageHelpers.DisplayName(account)), cancellationToken);
        return result;
    }

    private static string? ImageResult(JsonElement item) =>
        item.TryGetProperty("type", out var type) && type.GetString() == "image_generation_call" &&
        item.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString() : null;

    private static string? RevisedPrompt(JsonElement item) =>
        item.TryGetProperty("revised_prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String
            ? prompt.GetString() : null;

    public void Delete(StudioImage image)
    {
        GalleryStorage.TrashMedia(GalleryPath, image.Path);
    }

    public StudioImage AttachSvg(StudioImage image, string svgPath, string model)
    {
        var updated = image with { SvgPath = svgPath, SvgModel = model, SvgCreatedAt = DateTimeOffset.Now };
        GalleryStorage.SaveMetadata(GalleryPath, image.Path, updated);
        return updated;
    }

    public void Dispose() => _http.Dispose();
}
