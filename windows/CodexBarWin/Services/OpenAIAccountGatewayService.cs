using CodexBarWin.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin.Services;

public sealed class OpenAIAccountGatewayService : IDisposable
{
    public const int Port = 1456;
    public const string ApiKey = "codexbar-local-gateway";
    public const string BaseUrl = "http://127.0.0.1:1456/v1";

    private static readonly Uri OpenAIUpstreamBase = new("https://api.openai.com");
    private static readonly Uri CodexResponsesUpstream = new("https://chatgpt.com/backend-api/codex/responses");
    private static readonly Uri CodexResponsesCompactUpstream = new("https://chatgpt.com/backend-api/codex/responses/compact");
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly AccountRegistry _registry;
    private readonly CodexBarConfigStore _configStore;
    private readonly OpenAIOAuthRefreshService _oauthRefreshService;
    private readonly object _gate = new();
    private readonly object _routeGate = new();
    private readonly Dictionary<string, StickyRoute> _stickyRoutes = new(StringComparer.Ordinal);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public OpenAIAccountGatewayService(
        AccountRegistry registry,
        CodexBarConfigStore configStore,
        OpenAIOAuthRefreshService oauthRefreshService)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _oauthRefreshService = oauthRefreshService ?? throw new ArgumentNullException(nameof(oauthRefreshService));
    }

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new HttpListener { IgnoreWriteExceptions = true };
                _listener.Prefixes.Add("http://127.0.0.1:1456/");
                _listener.Start();
                IsRunning = true;
                LastError = null;
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                IsRunning = false;
                try
                {
                    _listener?.Close();
                }
                catch
                {
                }

                _listener = null;
                _cts?.Dispose();
                _cts = null;
                AppLogService.LogException(ex, "OpenAI aggregate gateway start");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            IsRunning = false;
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
            }
            finally
            {
                _listener = null;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                var listener = _listener;
                if (listener is null || !listener.IsListening)
                {
                    return;
                }

                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLogService.LogException(ex, "OpenAI aggregate gateway accept");
                return;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.RawUrl is not string rawUrl || !rawUrl.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context, 404, "{\"error\":{\"message\":\"unsupported gateway path\"}}", cancellationToken).ConfigureAwait(false);
                return;
            }

            var body = context.Request.HasEntityBody
                ? await ReadBodyAsync(context.Request.InputStream, cancellationToken).ConfigureAwait(false)
                : null;
            var stickyKey = ResolveStickyKey(context.Request, body, rawUrl);
            var account = SelectRouteAccount(stickyKey);
            if (account is null)
            {
                await WriteJsonAsync(context, 503, "{\"error\":{\"message\":\"aggregate gateway unavailable: no routable OpenAI account\"}}", cancellationToken).ConfigureAwait(false);
                return;
            }

            var response = await ForwardAsync(context.Request, rawUrl, body, account, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                var refresh = await _oauthRefreshService.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
                if (refresh.Outcome == OAuthRefreshOutcome.Refreshed)
                {
                    _registry.Save();
                    response = await ForwardAsync(context.Request, rawUrl, body, account, cancellationToken).ConfigureAwait(false);
                }
            }

            using (response)
            {
                if ((int)response.StatusCode < 400)
                {
                    BindStickyRoute(stickyKey, account.AccountId);
                }

                await CopyResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogService.LogException(ex, "OpenAI aggregate gateway request");
            if (context.Response.OutputStream.CanWrite)
            {
                await WriteJsonAsync(context, 502, "{\"error\":{\"message\":\"codexbar gateway failed to reach OpenAI upstream\"}}", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                context.Response.OutputStream.Close();
            }
            catch
            {
            }
        }
    }

    private TokenAccount? SelectRouteAccount(string? stickyKey)
    {
        PruneStickyRoutes();
        var candidates = _registry.Accounts
            .Where(IsRoutable)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var active = string.IsNullOrWhiteSpace(_registry.ActiveAccountId)
            ? null
            : candidates.FirstOrDefault(account => string.Equals(account.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal));

        if (_configStore.Config.OpenAI.AccountUsageMode != AccountUsageMode.AggregateGateway)
        {
            return active ?? candidates.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(stickyKey))
        {
            lock (_routeGate)
            {
                if (_stickyRoutes.TryGetValue(stickyKey, out var route))
                {
                    var sticky = candidates.FirstOrDefault(account => string.Equals(account.AccountId, route.AccountId, StringComparison.Ordinal));
                    if (sticky is not null && AccountUsageHelpers.Clamp(sticky.PrimaryUsedPercent) < 100)
                    {
                        _stickyRoutes[stickyKey] = route with { UpdatedAt = DateTimeOffset.UtcNow };
                        return sticky;
                    }
                }
            }
        }

        return candidates
            .OrderBy(ProxyPlanSortKey)
            .ThenBy(account => AccountUsageHelpers.Clamp(account.SecondaryUsedPercent))
            .ThenBy(account => AccountUsageHelpers.Clamp(account.PrimaryUsedPercent))
            .ThenBy(account => AccountUsageHelpers.DisplayName(account), StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
    }

    private sealed record StickyRoute(string AccountId, DateTimeOffset UpdatedAt);

    private static string? ResolveStickyKey(HttpListenerRequest request, byte[]? body, string rawUrl)
    {
        var headerNames = new[]
        {
            "x-wincodexbar-session",
            "x-codex-session-id",
            "session_id",
            "session-id",
            "x-client-request-id",
            "openai-conversation-id",
            "conversation_id",
            "x-codex-window-id",
        };

        foreach (var headerName in headerNames)
        {
            var value = request.Headers[headerName];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        if (body is null || body.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var propertyNames = new[]
            {
                "sessionId",
                "session_id",
                "conversationId",
                "conversation_id",
                "threadId",
                "thread_id",
                "previous_response_id",
                "previousResponseId",
                "prompt_cache_key",
            };

            foreach (var propertyName in propertyNames)
            {
                if (document.RootElement.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.GetString()))
                {
                    return property.GetString()!.Trim();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static int ProxyPlanSortKey(TokenAccount account)
    {
        var plan = (account.PlanType ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(plan) || plan == "free" ? 0 : 1;
    }

    private void BindStickyRoute(string? stickyKey, string accountId)
    {
        if (string.IsNullOrWhiteSpace(stickyKey) || string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        lock (_routeGate)
        {
            _stickyRoutes[stickyKey] = new StickyRoute(accountId, DateTimeOffset.UtcNow);
            if (_stickyRoutes.Count <= 256)
            {
                return;
            }

            foreach (var key in _stickyRoutes.OrderBy(pair => pair.Value.UpdatedAt).Take(_stickyRoutes.Count - 256).Select(pair => pair.Key).ToArray())
            {
                _stickyRoutes.Remove(key);
            }
        }
    }

    private void PruneStickyRoutes()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
        lock (_routeGate)
        {
            foreach (var key in _stickyRoutes.Where(pair => pair.Value.UpdatedAt < cutoff).Select(pair => pair.Key).ToArray())
            {
                _stickyRoutes.Remove(key);
            }
        }
    }

    private static bool IsRoutable(TokenAccount account)
    {
        return !account.IsSuspended
               && !account.TokenExpired
               && !string.IsNullOrWhiteSpace(account.AccessToken)
               && !string.IsNullOrWhiteSpace(account.RefreshToken)
               && !string.IsNullOrWhiteSpace(account.IdToken);
    }

    private static async Task<HttpResponseMessage> ForwardAsync(
        HttpListenerRequest request,
        string rawUrl,
        byte[]? body,
        TokenAccount account,
        CancellationToken cancellationToken)
    {
        var route = ResolveRoute(rawUrl);
        var target = route switch
        {
            GatewayRoute.CodexResponses => CodexResponsesUpstream,
            GatewayRoute.CodexResponsesCompact => CodexResponsesCompactUpstream,
            _ => new Uri(OpenAIUpstreamBase, rawUrl),
        };

        body = route switch
        {
            GatewayRoute.CodexResponses => NormalizeResponsesBody(body),
            GatewayRoute.CodexResponsesCompact => NormalizeCompactBody(body),
            _ => body,
        };

        var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), target);
        CopyHeaders(request, message.Headers);
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", account.AccessToken);
        if (route != GatewayRoute.OpenAICompatible)
        {
            var accountId = string.IsNullOrWhiteSpace(account.OpenAIAccountId)
                ? account.AccountId
                : account.OpenAIAccountId;
            message.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
            message.Headers.TryAddWithoutValidation("originator", "codexbar");
            message.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
            if (route == GatewayRoute.CodexResponsesCompact)
            {
                message.Headers.TryAddWithoutValidation("accept", "application/json");
                if (string.IsNullOrWhiteSpace(request.Headers["version"]))
                {
                    message.Headers.TryAddWithoutValidation("version", "0.125.0");
                }
            }
        }

        if (body is not null)
        {
            message.Content = new ByteArrayContent(body);
            if (!string.IsNullOrWhiteSpace(request.ContentType))
            {
                message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            }

            CopyContentHeaders(request, message.Content.Headers);
        }

        return await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private enum GatewayRoute
    {
        OpenAICompatible,
        CodexResponses,
        CodexResponsesCompact,
    }

    private static GatewayRoute ResolveRoute(string rawUrl)
    {
        var path = rawUrl;
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
        {
            path = path[..^1];
        }

        if (path.Equals("/v1/responses/compact", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/responses/compact", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/backend-api/codex/responses/compact", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/openai/v1/responses/compact", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRoute.CodexResponsesCompact;
        }

        if (path.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/responses", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/backend-api/codex/responses", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/openai/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRoute.CodexResponses;
        }

        return GatewayRoute.OpenAICompatible;
    }

    private static async Task<byte[]> ReadBodyAsync(Stream input, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static byte[]? NormalizeResponsesBody(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return body;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return body;
            }

            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                var hasInstructions = false;
                var hasTools = false;
                var hasParallelToolCalls = false;
                var hasInclude = false;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("max_output_tokens")
                        || property.NameEquals("temperature")
                        || property.NameEquals("top_p"))
                    {
                        continue;
                    }

                    if (property.NameEquals("store")
                        || property.NameEquals("stream"))
                    {
                        continue;
                    }

                    if (property.NameEquals("instructions"))
                    {
                        hasInstructions = true;
                    }
                    else if (property.NameEquals("tools"))
                    {
                        hasTools = true;
                    }
                    else if (property.NameEquals("parallel_tool_calls"))
                    {
                        hasParallelToolCalls = true;
                    }
                    else if (property.NameEquals("include"))
                    {
                        hasInclude = true;
                        WriteIncludeArray(writer, property.Value);
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteBoolean("store", false);
                writer.WriteBoolean("stream", true);
                if (!hasInstructions)
                {
                    writer.WriteString("instructions", string.Empty);
                }

                if (!hasTools)
                {
                    writer.WritePropertyName("tools");
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }

                if (!hasParallelToolCalls)
                {
                    writer.WriteBoolean("parallel_tool_calls", false);
                }

                if (!hasInclude)
                {
                    writer.WritePropertyName("include");
                    writer.WriteStartArray();
                    writer.WriteStringValue("reasoning.encrypted_content");
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return output.ToArray();
        }
        catch
        {
            return body;
        }
    }

    private static void WriteIncludeArray(Utf8JsonWriter writer, JsonElement value)
    {
        writer.WritePropertyName("include");
        writer.WriteStartArray();
        var hasReasoning = false;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), "reasoning.encrypted_content", StringComparison.Ordinal))
                {
                    hasReasoning = true;
                }

                item.WriteTo(writer);
            }
        }

        if (!hasReasoning)
        {
            writer.WriteStringValue("reasoning.encrypted_content");
        }

        writer.WriteEndArray();
    }

    private static byte[]? NormalizeCompactBody(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return body;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return body;
            }

            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                var hasInstructions = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals("model")
                        && !property.NameEquals("input")
                        && !property.NameEquals("instructions")
                        && !property.NameEquals("previous_response_id"))
                    {
                        continue;
                    }

                    if (property.NameEquals("instructions"))
                    {
                        hasInstructions = true;
                    }

                    property.WriteTo(writer);
                }

                if (!hasInstructions)
                {
                    writer.WriteString("instructions", string.Empty);
                }

                writer.WriteEndObject();
            }

            return output.ToArray();
        }
        catch
        {
            return body;
        }
    }

    private static void CopyHeaders(HttpListenerRequest request, System.Net.Http.Headers.HttpRequestHeaders headers)
    {
        foreach (var key in request.Headers.AllKeys.Where(key => key is not null))
        {
            if (SkipRequestHeader(key!))
            {
                continue;
            }

            headers.TryAddWithoutValidation(key!, request.Headers.GetValues(key!) ?? Array.Empty<string>());
        }
    }

    private static void CopyContentHeaders(HttpListenerRequest request, System.Net.Http.Headers.HttpContentHeaders headers)
    {
        foreach (var key in request.Headers.AllKeys.Where(key => key is not null))
        {
            if (!key!.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers.TryAddWithoutValidation(key, request.Headers.GetValues(key) ?? Array.Empty<string>());
        }
    }

    private static bool SkipRequestHeader(string key)
    {
        return key.Equals("Host", StringComparison.OrdinalIgnoreCase)
               || key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
               || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
               || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
               || key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
               || key.Equals("chatgpt-account-id", StringComparison.OrdinalIgnoreCase)
               || key.Equals("originator", StringComparison.OrdinalIgnoreCase)
               || key.Equals("OpenAI-Beta", StringComparison.OrdinalIgnoreCase)
               || key.Equals("Expect", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyResponseAsync(HttpListenerContext context, HttpResponseMessage upstream, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)upstream.StatusCode;
        foreach (var header in upstream.Headers)
        {
            TrySetResponseHeader(context.Response, header.Key, header.Value);
        }

        foreach (var header in upstream.Content.Headers)
        {
            TrySetResponseHeader(context.Response, header.Key, header.Value);
        }

        if (upstream.Content.Headers.ContentType is not null)
        {
            context.Response.ContentType = upstream.Content.Headers.ContentType.ToString();
        }

        await upstream.Content.CopyToAsync(context.Response.OutputStream, cancellationToken).ConfigureAwait(false);
    }

    private static void TrySetResponseHeader(HttpListenerResponse response, string key, IEnumerable<string> values)
    {
        if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            response.Headers[key] = string.Join(", ", values);
        }
        catch
        {
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, string body, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        using var writer = new StreamWriter(context.Response.OutputStream, leaveOpen: true);
        await writer.WriteAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
