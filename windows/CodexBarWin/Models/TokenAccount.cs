using System.Text.Json.Serialization;

namespace CodexBarWin.Models;

public class TokenAccount
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("openai_account_id")]
    public string OpenAIAccountId { get; set; } = string.Empty;

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("id_token")]
    public string IdToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("client_id")]
    public string? OAuthClientId { get; set; }

    [JsonPropertyName("plan_type")]
    public string PlanType { get; set; } = "free";

    [JsonPropertyName("primary_used_percent")]
    public double PrimaryUsedPercent { get; set; }

    [JsonPropertyName("secondary_used_percent")]
    public double SecondaryUsedPercent { get; set; }

    [JsonPropertyName("primary_reset_at")]
    public DateTimeOffset? PrimaryResetAt { get; set; }

    [JsonPropertyName("secondary_reset_at")]
    public DateTimeOffset? SecondaryResetAt { get; set; }

    [JsonPropertyName("primary_limit_window_seconds")]
    public int? PrimaryLimitWindowSeconds { get; set; }

    [JsonPropertyName("secondary_limit_window_seconds")]
    public int? SecondaryLimitWindowSeconds { get; set; }

    [JsonPropertyName("last_checked")]
    public DateTimeOffset? LastChecked { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("is_suspended")]
    public bool IsSuspended { get; set; }

    [JsonPropertyName("token_expired")]
    public bool TokenExpired { get; set; }

    [JsonPropertyName("token_last_refresh_at")]
    public DateTimeOffset? TokenLastRefreshAt { get; set; }

    [JsonPropertyName("organization_name")]
    public string? OrganizationName { get; set; }
}
