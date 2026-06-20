using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CodexBarWin.Models;

/// <summary>
/// 用量显示模式：剩余额度 vs 已用额度。
/// </summary>
public enum UsageDisplayMode
{
    Remaining,
    Used,
}

/// <summary>
/// Token 数字单位显示方式。
/// </summary>
public enum TokenUnitDisplayMode
{
    Chinese,
    Compact,
}

/// <summary>
/// OpenAI 账号使用模式。Windows 端目前主要支持手动切换；
/// 聚合网关需要一个常驻代理，先以占位形式保留，禁用时回退为切换。
/// </summary>
public enum AccountUsageMode
{
    Switch,
    AggregateGateway,
}

/// <summary>
/// 账号健康度状态。
/// </summary>
public enum AccountHealthStatus
{
    Healthy,
    Warning,
    Exhausted,
    Suspended,
    TokenExpired,
    Unknown,
}

public readonly record struct TokenCostBreakdown(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long TotalTokens)
{
    public long BillableInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    public static TokenCostBreakdown Empty { get; } = new(0, 0, 0, 0);

    public static TokenCostBreakdown Add(TokenCostBreakdown first, TokenCostBreakdown second)
    {
        return new TokenCostBreakdown(
            first.InputTokens + second.InputTokens,
            first.CachedInputTokens + second.CachedInputTokens,
            first.OutputTokens + second.OutputTokens,
            first.TotalTokens + second.TotalTokens);
    }
}

public sealed class TokenPricePreset
{
    [JsonPropertyName("input_usd_per_million")]
    public double InputUsdPerMillion { get; set; }

    [JsonPropertyName("cached_input_usd_per_million")]
    public double CachedInputUsdPerMillion { get; set; }

    [JsonPropertyName("output_usd_per_million")]
    public double OutputUsdPerMillion { get; set; }

    public TokenPricePreset Clone()
    {
        return new TokenPricePreset
        {
            InputUsdPerMillion = InputUsdPerMillion,
            CachedInputUsdPerMillion = CachedInputUsdPerMillion,
            OutputUsdPerMillion = OutputUsdPerMillion,
        };
    }

    public double EstimateUsd(TokenCostBreakdown tokens)
    {
        var input = tokens.InputTokens > 0 || tokens.OutputTokens > 0
            ? tokens.BillableInputTokens
            : tokens.TotalTokens;
        return input / 1_000_000d * InputUsdPerMillion
               + tokens.CachedInputTokens / 1_000_000d * CachedInputUsdPerMillion
               + tokens.OutputTokens / 1_000_000d * OutputUsdPerMillion;
    }

    public static Dictionary<string, TokenPricePreset> CreateDefaults()
    {
        return new Dictionary<string, TokenPricePreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.5"] = new() { InputUsdPerMillion = 5, CachedInputUsdPerMillion = 0.5, OutputUsdPerMillion = 30 },
            ["gpt-5.4"] = new() { InputUsdPerMillion = 2.5, CachedInputUsdPerMillion = 0.25, OutputUsdPerMillion = 15 },
            ["gpt-5.4-mini"] = new() { InputUsdPerMillion = 0.75, CachedInputUsdPerMillion = 0.075, OutputUsdPerMillion = 4.5 },
            ["gpt-5.3-codex-spark"] = new() { InputUsdPerMillion = 1.75, CachedInputUsdPerMillion = 0.175, OutputUsdPerMillion = 14 },
            ["gpt-5.2"] = new() { InputUsdPerMillion = 1.75, CachedInputUsdPerMillion = 0.175, OutputUsdPerMillion = 14 },
            ["gpt-4o"] = new() { InputUsdPerMillion = 2.5, CachedInputUsdPerMillion = 1.25, OutputUsdPerMillion = 10 },
        };
    }
}

public sealed class CodexBarOpenAISettings
{
    [JsonPropertyName("usage_display_mode")]
    public UsageDisplayMode UsageDisplayMode { get; set; } = UsageDisplayMode.Used;

    [JsonPropertyName("token_unit_display_mode")]
    public TokenUnitDisplayMode TokenUnitDisplayMode { get; set; } = TokenUnitDisplayMode.Chinese;

    [JsonPropertyName("account_usage_mode")]
    public AccountUsageMode AccountUsageMode { get; set; } = AccountUsageMode.Switch;

    /// <summary>
    /// 后台自动刷新间隔，单位：秒。最小 60 秒。
    /// </summary>
    [JsonPropertyName("auto_refresh_interval_seconds")]
    public int AutoRefreshIntervalSeconds { get; set; } = 60;

    [JsonPropertyName("auto_refresh_enabled")]
    public bool AutoRefreshEnabled { get; set; } = true;

    [JsonPropertyName("warning_threshold_percent")]
    public double WarningThresholdPercent { get; set; } = 70;

    [JsonPropertyName("danger_threshold_percent")]
    public double DangerThresholdPercent { get; set; } = 90;

    [JsonPropertyName("token_pricing_model")]
    public string TokenPricingModel { get; set; } = "gpt-5.5";

    [JsonPropertyName("usd_to_cny_rate")]
    public double UsdToCnyRate { get; set; } = 7.25;

    [JsonPropertyName("token_price_presets")]
    public Dictionary<string, TokenPricePreset> TokenPricePresets { get; set; } = TokenPricePreset.CreateDefaults();

    public void EnsurePricingDefaults()
    {
        if (TokenPricePresets is null || TokenPricePresets.Count == 0)
        {
            TokenPricePresets = TokenPricePreset.CreateDefaults();
        }

        var defaults = TokenPricePreset.CreateDefaults();
        foreach (var pair in defaults)
        {
            if (!TokenPricePresets.ContainsKey(pair.Key))
            {
                TokenPricePresets[pair.Key] = pair.Value.Clone();
            }
        }

        foreach (var key in TokenPricePresets.Keys.ToArray())
        {
            var preset = TokenPricePresets[key] ?? defaults["gpt-5.5"].Clone();
            preset.InputUsdPerMillion = ClampPrice(preset.InputUsdPerMillion, defaults.TryGetValue(key, out var fallback) ? fallback.InputUsdPerMillion : 5);
            preset.CachedInputUsdPerMillion = ClampPrice(preset.CachedInputUsdPerMillion, defaults.TryGetValue(key, out fallback) ? fallback.CachedInputUsdPerMillion : 0.5);
            preset.OutputUsdPerMillion = ClampPrice(preset.OutputUsdPerMillion, defaults.TryGetValue(key, out fallback) ? fallback.OutputUsdPerMillion : 30);
            TokenPricePresets[key] = preset;
        }

        if (string.IsNullOrWhiteSpace(TokenPricingModel))
        {
            TokenPricingModel = "gpt-5.5";
        }

        if (!TokenPricePresets.ContainsKey(TokenPricingModel))
        {
            TokenPricePresets[TokenPricingModel] = defaults["gpt-5.5"].Clone();
        }

        UsdToCnyRate = double.IsFinite(UsdToCnyRate) && UsdToCnyRate > 0 ? Math.Clamp(UsdToCnyRate, 0.1, 50) : 7.25;
    }

    public TokenPricePreset GetOrCreateTokenPricePreset(string? model = null)
    {
        EnsurePricingDefaults();
        var key = string.IsNullOrWhiteSpace(model) ? TokenPricingModel : model.Trim();
        if (!TokenPricePresets.TryGetValue(key, out var preset))
        {
            preset = TokenPricePresets["gpt-5.5"].Clone();
            TokenPricePresets[key] = preset;
        }

        return preset;
    }

    public Dictionary<string, TokenPricePreset> CloneTokenPricePresets()
    {
        EnsurePricingDefaults();
        return TokenPricePresets.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static double ClampPrice(double value, double fallback)
    {
        return double.IsFinite(value) && value >= 0 ? Math.Min(value, 10_000) : fallback;
    }
}

public sealed class CodexBarGlobalSettings
{
    [JsonPropertyName("default_model")]
    public string DefaultModel { get; set; } = "gpt-5.5";

    [JsonPropertyName("review_model")]
    public string ReviewModel { get; set; } = "gpt-5.5";

    [JsonPropertyName("reasoning_effort")]
    public string ReasoningEffort { get; set; } = "medium";

    [JsonPropertyName("service_tier")]
    public string ServiceTier { get; set; } = "standard";
}

public sealed class CodexBarConfig
{
    [JsonPropertyName("global")]
    public CodexBarGlobalSettings Global { get; set; } = new();

    [JsonPropertyName("openai")]
    public CodexBarOpenAISettings OpenAI { get; set; } = new();

    [JsonPropertyName("keep_awake_enabled")]
    public bool KeepAwakeEnabled { get; set; } = true;

    [JsonPropertyName("advanced_keep_awake_enabled")]
    public bool AdvancedKeepAwakeEnabled { get; set; } = true;

    [JsonPropertyName("advanced_keep_awake_idle_threshold_ms")]
    public int AdvancedKeepAwakeIdleThresholdMs { get; set; } = 120_000;

    [JsonPropertyName("advanced_keep_awake_interval_ms")]
    public int AdvancedKeepAwakeIntervalMs { get; set; } = 30_000;

    [JsonPropertyName("advanced_keep_awake_jitter_ms")]
    public int AdvancedKeepAwakeJitterMs { get; set; } = 5_000;

    [JsonPropertyName("advanced_keep_awake_move_pattern")]
    public string AdvancedKeepAwakeMovePattern { get; set; } = "ping_pong";

    [JsonPropertyName("advanced_keep_awake_pause_on_fullscreen")]
    public bool AdvancedKeepAwakePauseOnFullscreen { get; set; } = true;

    [JsonPropertyName("start_with_windows")]
    public bool StartWithWindows { get; set; } = true;

    public CodexBarConfig Clone()
    {
        return new CodexBarConfig
        {
            KeepAwakeEnabled = KeepAwakeEnabled,
            AdvancedKeepAwakeEnabled = AdvancedKeepAwakeEnabled,
            AdvancedKeepAwakeIdleThresholdMs = AdvancedKeepAwakeIdleThresholdMs,
            AdvancedKeepAwakeIntervalMs = AdvancedKeepAwakeIntervalMs,
            AdvancedKeepAwakeJitterMs = AdvancedKeepAwakeJitterMs,
            AdvancedKeepAwakeMovePattern = AdvancedKeepAwakeMovePattern,
            AdvancedKeepAwakePauseOnFullscreen = AdvancedKeepAwakePauseOnFullscreen,
            StartWithWindows = StartWithWindows,
            Global = new CodexBarGlobalSettings
            {
                DefaultModel = Global.DefaultModel,
                ReviewModel = Global.ReviewModel,
                ReasoningEffort = Global.ReasoningEffort,
                ServiceTier = Global.ServiceTier,
            },
            OpenAI = new CodexBarOpenAISettings
            {
                UsageDisplayMode = OpenAI.UsageDisplayMode,
                TokenUnitDisplayMode = OpenAI.TokenUnitDisplayMode,
                AccountUsageMode = OpenAI.AccountUsageMode,
                AutoRefreshEnabled = OpenAI.AutoRefreshEnabled,
                AutoRefreshIntervalSeconds = OpenAI.AutoRefreshIntervalSeconds,
                WarningThresholdPercent = OpenAI.WarningThresholdPercent,
                DangerThresholdPercent = OpenAI.DangerThresholdPercent,
                TokenPricingModel = OpenAI.TokenPricingModel,
                UsdToCnyRate = OpenAI.UsdToCnyRate,
                TokenPricePresets = OpenAI.CloneTokenPricePresets(),
            },
        };
    }

    public static readonly string[] AvailableModels =
    {
        "gpt-5.5",
        "gpt-5.4",
        "gpt-5.4-mini",
        "gpt-5.3-codex-spark",
        "gpt-5.2",
        "gpt-4o",
    };

    public static readonly string[] AvailableReasoningEfforts =
    {
        "low",
        "medium",
        "high",
        "xhigh",
    };

    public static readonly string[] AvailableServiceTiers =
    {
        "standard",
        "fast",
    };
}
