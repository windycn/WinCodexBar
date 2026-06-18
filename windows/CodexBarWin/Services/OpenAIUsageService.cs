using CodexBarWin.Models;
using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarWin.Services;

public enum UsageRefreshOutcome
{
    Updated,
    Unauthorized,
    Forbidden,
    Failed,
}

public sealed class OpenAIUsageService
{
    private static readonly Uri UsageUri = new("https://chatgpt.com/backend-api/wham/usage");
    private static readonly Uri OrgInfoUri = new("https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27?timezone_offset_min=-480");
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly HttpClient _httpClient;

    public OpenAIUsageService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public async Task<UsageRefreshOutcome> RefreshUsageAsync(TokenAccount account, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return UsageRefreshOutcome.Failed;
        }

        var usageOutcome = await FetchUsageAsync(account, cancellationToken).ConfigureAwait(false);
        if (usageOutcome != UsageRefreshOutcome.Updated)
        {
            switch (usageOutcome)
            {
                case UsageRefreshOutcome.Forbidden:
                    account.IsSuspended = true;
                    break;
                case UsageRefreshOutcome.Unauthorized:
                    account.TokenExpired = true;
                    break;
            }

            return usageOutcome;
        }

        account.IsSuspended = false;
        account.TokenExpired = false;
        account.LastChecked = DateTimeOffset.UtcNow;

        try
        {
            var orgName = await FetchOrgNameAsync(account, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(orgName))
            {
                account.OrganizationName = orgName;
            }
        }
        catch
        {
            // 组织名属于增益信息，失败时静默。
        }

        return UsageRefreshOutcome.Updated;
    }

    private async Task<UsageRefreshOutcome> FetchUsageAsync(TokenAccount account, CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildAuthorizedRequest(HttpMethod.Get, UsageUri, account);
            request.Headers.TryAddWithoutValidation("Referer", "https://chatgpt.com/codex/settings/usage");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Unauthorized:
                    return UsageRefreshOutcome.Unauthorized;
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.PaymentRequired:
                    return UsageRefreshOutcome.Forbidden;
                default:
                    return UsageRefreshOutcome.Failed;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (root.TryGetProperty("plan_type", out var planNode) && planNode.ValueKind == JsonValueKind.String)
            {
                var plan = planNode.GetString();
                if (!string.IsNullOrWhiteSpace(plan))
                {
                    account.PlanType = plan!;
                }
            }

            if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
            {
                ApplyWindow(rateLimit, "primary_window", primary: true, account);
                ApplyWindow(rateLimit, "secondary_window", primary: false, account);
            }

            if (root.TryGetProperty("rate_limits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
            {
                ApplyCodexWindow(rateLimits, "primary", primary: true, account);
                ApplyCodexWindow(rateLimits, "secondary", primary: false, account);
            }

            return UsageRefreshOutcome.Updated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return UsageRefreshOutcome.Failed;
        }
    }

    private async Task<string?> FetchOrgNameAsync(TokenAccount account, CancellationToken cancellationToken)
    {
        using var request = BuildAuthorizedRequest(HttpMethod.Get, OrgInfoUri, account);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var accountId = string.IsNullOrWhiteSpace(account.OpenAIAccountId) ? account.AccountId : account.OpenAIAccountId;
        if (!accounts.TryGetProperty(accountId, out var entry) || entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!entry.TryGetProperty("account", out var inner) || inner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (inner.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String)
        {
            return nameNode.GetString();
        }

        return null;
    }

    private static HttpRequestMessage BuildAuthorizedRequest(HttpMethod method, Uri uri, TokenAccount account)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(ChromeUserAgent);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.Clear();
        request.Headers.AcceptLanguage.ParseAdd("zh-CN");
        request.Headers.TryAddWithoutValidation("oai-language", "zh-CN");

        var accountHeader = string.IsNullOrWhiteSpace(account.OpenAIAccountId)
            ? account.AccountId
            : account.OpenAIAccountId;
        if (!string.IsNullOrWhiteSpace(accountHeader))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountHeader);
        }

        return request;
    }

    private static void ApplyWindow(JsonElement rateLimit, string name, bool primary, TokenAccount account)
    {
        if (!rateLimit.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var used = ReadDouble(window, "used_percent");
        var resetAt = ReadUnixTime(window, "reset_at") ?? ReadResetAfter(window, "reset_after_seconds");
        var windowSeconds = ReadInt(window, "limit_window_seconds");

        if (used is not null)
        {
            if (primary)
            {
                account.PrimaryUsedPercent = Math.Clamp(used.Value, 0, 100);
            }
            else
            {
                account.SecondaryUsedPercent = Math.Clamp(used.Value, 0, 100);
            }
        }

        if (primary)
        {
            account.PrimaryResetAt = resetAt ?? account.PrimaryResetAt;
            account.PrimaryLimitWindowSeconds = windowSeconds ?? account.PrimaryLimitWindowSeconds;
        }
        else
        {
            account.SecondaryResetAt = resetAt ?? account.SecondaryResetAt;
            account.SecondaryLimitWindowSeconds = windowSeconds ?? account.SecondaryLimitWindowSeconds;
        }
    }

    private static void ApplyCodexWindow(JsonElement rateLimits, string name, bool primary, TokenAccount account)
    {
        if (!rateLimits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var used = ReadDouble(window, "used_percent");
        var resetAt = ReadUnixTime(window, "resets_at")
                      ?? ReadUnixTime(window, "reset_at")
                      ?? ReadResetAfter(window, "reset_after_seconds");
        var windowSeconds = ReadInt(window, "limit_window_seconds");
        if (windowSeconds is null && ReadInt(window, "window_minutes") is { } minutes)
        {
            windowSeconds = minutes * 60;
        }

        if (used is not null)
        {
            if (primary)
            {
                account.PrimaryUsedPercent = Math.Clamp(used.Value, 0, 100);
            }
            else
            {
                account.SecondaryUsedPercent = Math.Clamp(used.Value, 0, 100);
            }
        }

        if (primary)
        {
            account.PrimaryResetAt = resetAt ?? account.PrimaryResetAt;
            account.PrimaryLimitWindowSeconds = windowSeconds ?? account.PrimaryLimitWindowSeconds;
        }
        else
        {
            account.SecondaryResetAt = resetAt ?? account.SecondaryResetAt;
            account.SecondaryLimitWindowSeconds = windowSeconds ?? account.SecondaryLimitWindowSeconds;
        }
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadUnixTime(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        double? seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };

        if (seconds is null)
        {
            return null;
        }

        var ms = (long)Math.Round(seconds.Value * 1000d);
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    private static DateTimeOffset? ReadResetAfter(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        double? seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };

        if (seconds is null || seconds.Value < 0)
        {
            return null;
        }

        return DateTimeOffset.UtcNow.AddSeconds(seconds.Value);
    }
}
