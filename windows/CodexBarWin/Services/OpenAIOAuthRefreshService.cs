using CodexBarWin.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin.Services;

public enum OAuthRefreshOutcome
{
    Refreshed,
    TerminalFailure,
    TransientFailure,
    Skipped,
}

public sealed record OAuthRefreshResult(OAuthRefreshOutcome Outcome, TokenAccount? Account, string? Message);

/// <summary>
/// 用 ChatGPT OAuth refresh_token 兑换新的 access_token。
/// </summary>
public sealed class OpenAIOAuthRefreshService
{
    private const string DefaultClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private static readonly Uri TokenUri = new("https://auth.openai.com/oauth/token");

    private readonly HttpClient _httpClient;

    public OpenAIOAuthRefreshService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public async Task<OAuthRefreshResult> RefreshAsync(TokenAccount account, CancellationToken cancellationToken = default)
    {
        if (account is null)
        {
            return new OAuthRefreshResult(OAuthRefreshOutcome.Skipped, null, null);
        }

        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            return new OAuthRefreshResult(OAuthRefreshOutcome.Skipped, account, "缺少 refresh_token");
        }

        var clientId = string.IsNullOrWhiteSpace(account.OAuthClientId) ? DefaultClientId : account.OAuthClientId!;

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = account.RefreshToken,
        };

        try
        {
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
                var errorCode = errorNode.GetString() ?? string.Empty;
                var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                var message = string.IsNullOrWhiteSpace(description) ? errorCode : $"{errorCode}: {description}";
                if (errorCode is "invalid_grant" or "unauthorized_client")
                {
                    account.TokenExpired = true;
                    return new OAuthRefreshResult(OAuthRefreshOutcome.TerminalFailure, account, message);
                }

                return new OAuthRefreshResult(OAuthRefreshOutcome.TransientFailure, account, message);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new OAuthRefreshResult(OAuthRefreshOutcome.TransientFailure, account, $"HTTP {(int)response.StatusCode}");
            }

            if (!root.TryGetProperty("access_token", out var accessNode) || accessNode.ValueKind != JsonValueKind.String)
            {
                return new OAuthRefreshResult(OAuthRefreshOutcome.TransientFailure, account, "未获取到 access_token");
            }

            var accessToken = accessNode.GetString() ?? string.Empty;
            var refreshToken = root.TryGetProperty("refresh_token", out var rtNode) && rtNode.ValueKind == JsonValueKind.String
                ? rtNode.GetString()
                : null;
            var idToken = root.TryGetProperty("id_token", out var idNode) && idNode.ValueKind == JsonValueKind.String
                ? idNode.GetString()
                : null;
            var newClientId = root.TryGetProperty("client_id", out var cidNode) && cidNode.ValueKind == JsonValueKind.String
                ? cidNode.GetString()
                : null;
            var expiresIn = root.TryGetProperty("expires_in", out var expNode) && expNode.ValueKind == JsonValueKind.Number && expNode.TryGetInt64(out var seconds)
                ? seconds
                : (long?)null;

            account.AccessToken = accessToken;
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                account.RefreshToken = refreshToken!;
            }
            if (!string.IsNullOrWhiteSpace(idToken))
            {
                account.IdToken = idToken!;
            }
            if (!string.IsNullOrWhiteSpace(newClientId))
            {
                account.OAuthClientId = newClientId;
            }

            account.TokenLastRefreshAt = DateTimeOffset.UtcNow;
            if (expiresIn is not null)
            {
                account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value);
            }
            account.TokenExpired = false;

            return new OAuthRefreshResult(OAuthRefreshOutcome.Refreshed, account, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OAuthRefreshResult(OAuthRefreshOutcome.TransientFailure, account, ex.Message);
        }
    }
}
