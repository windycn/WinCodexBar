using CodexBarWin.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin.Services;

public sealed record StartedOpenAIOAuthFlow(string FlowId, string AuthUrl);

public sealed class OpenAIOAuthLoginService
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string AuthUrl = "https://auth.openai.com/oauth/authorize";
    private static readonly Uri TokenUri = new("https://auth.openai.com/oauth/token");
    private const string Scope = "openid profile email offline_access api.connectors.read api.connectors.invoke";
    private const string AuthClaim = "https://api.openai.com/auth";

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, PendingFlow> _flows = new(StringComparer.Ordinal);

    public OpenAIOAuthLoginService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public StartedOpenAIOAuthFlow StartFlow()
    {
        var flow = new PendingFlow(
            Guid.NewGuid().ToString("N"),
            GenerateCodeVerifier(),
            Guid.NewGuid().ToString("N"));

        _flows[flow.FlowId] = flow;
        return new StartedOpenAIOAuthFlow(flow.FlowId, BuildAuthorizationUrl(flow));
    }

    public void CancelFlow(string flowId)
    {
        if (!string.IsNullOrWhiteSpace(flowId))
        {
            _flows.Remove(flowId);
        }
    }

    public async Task<TokenAccount> CompleteFlowAsync(
        string flowId,
        string callbackInput,
        CancellationToken cancellationToken = default)
    {
        if (!_flows.TryGetValue(flowId, out var flow))
        {
            throw new InvalidOperationException("登录流程已失效，请重新添加账号。");
        }

        var parsed = ParseCallbackInput(callbackInput);
        if (string.IsNullOrWhiteSpace(parsed.Code))
        {
            throw new InvalidOperationException("没有从回调链接中解析到 code。");
        }

        if (!string.IsNullOrWhiteSpace(parsed.State)
            && !string.Equals(parsed.State, flow.ExpectedState, StringComparison.Ordinal))
        {
            // OpenAI 当前会校验 PKCE；state 不一致时仍允许继续，让服务端返回最终结论。
        }

        var tokens = await ExchangeCodeAsync(parsed.Code!, flow, cancellationToken).ConfigureAwait(false);
        _flows.Remove(flow.FlowId);
        return BuildAccount(tokens);
    }

    public LocalhostOAuthCallbackServer CreateCallbackServer(Action<string> onCallback)
    {
        return new LocalhostOAuthCallbackServer(onCallback);
    }

    private static string BuildAuthorizationUrl(PendingFlow flow)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["code_challenge"] = GenerateCodeChallenge(flow.CodeVerifier),
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = flow.ExpectedState,
            ["originator"] = "Codex Desktop",
        };

        return AuthUrl + "?" + string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private async Task<OAuthTokenSet> ExchangeCodeAsync(
        string code,
        PendingFlow flow,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = flow.CodeVerifier,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri)
        {
            Content = new FormUrlEncodedContent(body),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var errorNode) && errorNode.ValueKind == JsonValueKind.String)
        {
            var error = errorNode.GetString() ?? "unknown_error";
            var description = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(description)
                ? $"OpenAI 授权失败：{error}"
                : $"OpenAI 授权失败：{error}，{description}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI 授权失败：HTTP {(int)response.StatusCode}");
        }

        var accessToken = ReadString(root, "access_token");
        var refreshToken = ReadString(root, "refresh_token");
        var idToken = ReadString(root, "id_token");
        if (string.IsNullOrWhiteSpace(accessToken)
            || string.IsNullOrWhiteSpace(refreshToken)
            || string.IsNullOrWhiteSpace(idToken))
        {
            throw new InvalidOperationException("OpenAI 返回的 OAuth token 不完整。");
        }

        return new OAuthTokenSet(
            accessToken!,
            refreshToken!,
            idToken!,
            ReadString(root, "client_id") ?? ClientId,
            DateTimeOffset.UtcNow);
    }

    private static TokenAccount BuildAccount(OAuthTokenSet tokens)
    {
        var accessClaims = DecodeJwtPayload(tokens.AccessToken);
        var idClaims = DecodeJwtPayload(tokens.IdToken);
        var authClaims = accessClaims.TryGetProperty(AuthClaim, out var authNode) && authNode.ValueKind == JsonValueKind.Object
            ? authNode
            : default;

        var accountId = LocalAccountId(authClaims);
        var openAIAccountId = OpenAIAccountId(authClaims, accountId);
        var planType = StringClaim(authClaims, "chatgpt_plan_type") ?? "free";
        var email = StringClaim(idClaims, "email") ?? string.Empty;
        var expiresAt = Earliest(ReadJwtExpiry(accessClaims), ReadJwtExpiry(idClaims)) ?? ReadSubscriptionUntil(idClaims);

        if (string.IsNullOrWhiteSpace(accountId))
        {
            accountId = openAIAccountId;
        }

        if (string.IsNullOrWhiteSpace(accountId))
        {
            accountId = "oauth-" + Guid.NewGuid().ToString("N");
        }

        return new TokenAccount
        {
            Email = email,
            AccountId = accountId,
            OpenAIAccountId = string.IsNullOrWhiteSpace(openAIAccountId) ? accountId : openAIAccountId,
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            IdToken = tokens.IdToken,
            ExpiresAt = expiresAt,
            OAuthClientId = tokens.ClientId,
            PlanType = planType,
            TokenLastRefreshAt = tokens.TokenLastRefreshAt,
        };
    }

    private static (string? Code, string? State) ParseCallbackInput(string input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return (null, null);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var query = ParseQuery(uri.Query);
            return (query.GetValueOrDefault("code"), query.GetValueOrDefault("state"));
        }

        if (trimmed.Contains("code=", StringComparison.OrdinalIgnoreCase))
        {
            var queryStart = trimmed.IndexOf('?', StringComparison.Ordinal);
            var query = queryStart >= 0 ? trimmed[queryStart..] : "?" + trimmed;
            var values = ParseQuery(query);
            return (values.GetValueOrDefault("code"), values.GetValueOrDefault("state"));
        }

        return (trimmed, null);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = query.TrimStart('?');
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 0) continue;
            var key = Uri.UnescapeDataString(pieces[0].Replace("+", " "));
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1].Replace("+", " ")) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static string GenerateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var bytes = Encoding.ASCII.GetBytes(verifier);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
        }

        return Convert.FromBase64String(base64);
    }

    private static JsonElement DecodeJwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return default;
        }

        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }
        catch
        {
            return default;
        }
    }

    private static string? StringClaim(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = node.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var node)
               && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static string LocalAccountId(JsonElement authClaims)
    {
        var accountUserId = StringClaim(authClaims, "chatgpt_account_user_id");
        if (!string.IsNullOrWhiteSpace(accountUserId))
        {
            return accountUserId!;
        }

        var remoteAccountId = StringClaim(authClaims, "chatgpt_account_id");
        var userId = StringClaim(authClaims, "chatgpt_user_id") ?? StringClaim(authClaims, "user_id");
        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(remoteAccountId))
        {
            return $"{userId}__{remoteAccountId}";
        }

        return remoteAccountId ?? userId ?? string.Empty;
    }

    private static string OpenAIAccountId(JsonElement authClaims, string fallback)
    {
        return StringClaim(authClaims, "chatgpt_account_id") ?? fallback;
    }

    private static DateTimeOffset? ReadJwtExpiry(JsonElement claims)
    {
        if (claims.ValueKind != JsonValueKind.Object || !claims.TryGetProperty("exp", out var node))
        {
            return null;
        }

        double? seconds = node.ValueKind switch
        {
            JsonValueKind.Number when node.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };

        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds((long)seconds.Value);
    }

    private static DateTimeOffset? ReadSubscriptionUntil(JsonElement idClaims)
    {
        if (idClaims.ValueKind != JsonValueKind.Object
            || !idClaims.TryGetProperty(AuthClaim, out var authNode)
            || authNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var value = StringClaim(authNode, "chatgpt_subscription_active_until");
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right)
    {
        return (left, right) switch
        {
            ({ } l, { } r) => l <= r ? l : r,
            ({ } l, null) => l,
            (null, { } r) => r,
            _ => null,
        };
    }

    private sealed record PendingFlow(string FlowId, string CodeVerifier, string ExpectedState);

    private sealed record OAuthTokenSet(
        string AccessToken,
        string RefreshToken,
        string IdToken,
        string? ClientId,
        DateTimeOffset TokenLastRefreshAt);
}

public sealed class LocalhostOAuthCallbackServer : IDisposable
{
    private const int Port = 1455;
    private const string CallbackPath = "/auth/callback";
    private readonly Action<string> _onCallback;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _completed;

    public LocalhostOAuthCallbackServer(Action<string> onCallback)
    {
        _onCallback = onCallback ?? throw new ArgumentNullException(nameof(onCallback));
    }

    public void Start()
    {
        Stop();
        _completed = 0;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start(5);
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // best effort
        }
        finally
        {
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                await WriteResponseAsync(stream, "400 Bad Request", "Invalid callback request.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var request = Encoding.UTF8.GetString(buffer, 0, read);
            var callbackUrl = CallbackUrlFromRequest(request);
            if (callbackUrl is null)
            {
                await WriteResponseAsync(stream, "404 Not Found", "Callback route not found.", cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(stream, "200 OK", SuccessHtml, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                Stop();
                _onCallback(callbackUrl);
            }
        }
        catch
        {
            // The login window still supports manual callback paste.
        }
    }

    private static string? CallbackUrlFromRequest(string request)
    {
        var line = request.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault();
        if (line is null || !line.StartsWith("GET ", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = line.Split(' ');
        if (parts.Length < 2 || !parts[1].StartsWith(CallbackPath, StringComparison.Ordinal))
        {
            return null;
        }

        return $"http://localhost:{Port}{parts[1]}";
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        string status,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = string.Join("\r\n", new[]
        {
            $"HTTP/1.1 {status}",
            "Content-Type: text/html; charset=utf-8",
            $"Content-Length: {bodyBytes.Length}",
            "Cache-Control: no-store",
            "Connection: close",
            "",
            "",
        });
        var headerBytes = Encoding.UTF8.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }

    private const string SuccessHtml = """
<!doctype html>
<html lang="zh-CN">
<meta charset="utf-8">
<title>WinCodexBar 登录已接收</title>
<style>
body{margin:0;min-height:100vh;display:grid;place-items:center;background:#111;color:#f7f7f7;font-family:'Segoe UI','Microsoft YaHei UI',sans-serif}
.card{width:min(92vw,420px);padding:28px 24px;border-radius:10px;background:#1b1b1b;border:1px solid #333;box-shadow:0 24px 60px rgba(0,0,0,.35)}
h1{margin:0 0 10px;font-size:22px}p{margin:0;color:#bbb;line-height:1.6}
</style>
<div class="card"><h1>登录回调已接收</h1><p>WinCodexBar 已捕获授权回调，可以回到应用继续。</p></div>
</html>
""";
}
