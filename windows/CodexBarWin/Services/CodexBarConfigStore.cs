using CodexBarWin.Models;
using System;
using System.IO;
using System.Text.Json;

namespace CodexBarWin.Services;

/// <summary>
/// 负责把 <see cref="CodexBarConfig"/> 落到 <c>~/.codexbar/windows_settings.json</c>，
/// 并合并旧版仅有 keep_awake 字段的配置。
/// </summary>
public sealed class CodexBarConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public CodexBarConfig Config { get; private set; } = new();

    public void Load()
    {
        Config = new CodexBarConfig();

        if (!File.Exists(CodexPaths.WindowsSettingsPath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(CodexPaths.WindowsSettingsPath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            // 同时兼容老的 { "KeepAwakeEnabled": true } 与新的结构化配置。
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            var config = new CodexBarConfig();
            config.KeepAwakeEnabled = ReadBool(root, "keep_awake_enabled",
                ReadBool(root, "KeepAwakeEnabled", config.KeepAwakeEnabled));
            var awayModeDelay = ReadInt(root, "away_mode_delay_seconds", config.AwayModeDelaySeconds);
            config.AwayModeDelaySeconds = awayModeDelay is >= 0 and <= 3600 ? awayModeDelay : 0;
            config.AdvancedKeepAwakeEnabled = ReadBool(root, "advanced_keep_awake_enabled", config.AdvancedKeepAwakeEnabled);
            config.AutoCheckUpdates = ReadBool(root, "auto_check_updates", true);
            config.SilentUpdates = ReadBool(root, "silent_updates", false);
            config.SystemNotificationsEnabled = ReadBool(root, "system_notifications_enabled", true);
            config.ImageStudioEnabled = ReadBool(root, "image_studio_enabled", false);
            config.VectorStudioEnabled = ReadBool(root, "vector_studio_enabled", false);
            config.QualityCheckEnabled = ReadBool(root, "quality_check_enabled", false);
            var uiScale = ReadInt(root, "ui_scale_percent", 0);
            config.UiScalePercent = uiScale is 100 or 125 or 150 or 175 or 200 or 250 or 300 ? uiScale : 0;
            if (root.TryGetProperty("tray_icon_style", out var trayStyle) && trayStyle.ValueKind == JsonValueKind.String)
            {
                var style = trayStyle.GetString();
                config.TrayIconStyle = style is "ring" or "dual" or "percent" or "status" or "bars" or "ringpercent" or "numberbars" or "badge" or "dotnumber" or "pillring" or "classic" ? style : config.TrayIconStyle;
            }

            config.AdvancedKeepAwakeIdleThresholdMs = ReadInt(root, "advanced_keep_awake_idle_threshold_ms", config.AdvancedKeepAwakeIdleThresholdMs);
            config.AdvancedKeepAwakeIntervalMs = ReadInt(root, "advanced_keep_awake_interval_ms", config.AdvancedKeepAwakeIntervalMs);
            config.AdvancedKeepAwakeJitterMs = ReadInt(root, "advanced_keep_awake_jitter_ms", config.AdvancedKeepAwakeJitterMs);
            if (root.TryGetProperty("advanced_keep_awake_move_pattern", out var movePattern)
                && movePattern.ValueKind == JsonValueKind.String)
            {
                config.AdvancedKeepAwakeMovePattern = movePattern.GetString() ?? config.AdvancedKeepAwakeMovePattern;
            }

            if (root.TryGetProperty("advanced_keep_awake_pause_on_fullscreen", out var pauseOnFullscreen)
                && (pauseOnFullscreen.ValueKind == JsonValueKind.True || pauseOnFullscreen.ValueKind == JsonValueKind.False))
            {
                config.AdvancedKeepAwakePauseOnFullscreen = pauseOnFullscreen.GetBoolean();
            }

            if (root.TryGetProperty("start_with_windows", out var startWithWindows)
                && (startWithWindows.ValueKind == JsonValueKind.True || startWithWindows.ValueKind == JsonValueKind.False))
            {
                config.StartWithWindows = startWithWindows.GetBoolean();
            }

            if (root.TryGetProperty("global", out var globalNode))
            {
                config.Global = JsonSerializer.Deserialize<CodexBarGlobalSettings>(globalNode.GetRawText())
                                ?? new CodexBarGlobalSettings();
            }

            if (root.TryGetProperty("image_studio", out var imageStudioNode))
                config.ImageStudio = JsonSerializer.Deserialize<ImageStudioPreferences>(imageStudioNode.GetRawText())
                                     ?? new ImageStudioPreferences();

            if (root.TryGetProperty("vector_studio", out var vectorStudioNode))
                config.VectorStudio = JsonSerializer.Deserialize<VectorStudioPreferences>(vectorStudioNode.GetRawText())
                                      ?? new VectorStudioPreferences();

            config.ImageGalleryPath = ReadString(root, "image_gallery_path") ?? string.Empty;
            config.SvgGalleryPath = ReadString(root, "svg_gallery_path") ?? string.Empty;

            if (root.TryGetProperty("quality_check", out var qualityCheckNode))
                config.QualityCheck = JsonSerializer.Deserialize<QualityCheckPreferences>(qualityCheckNode.GetRawText())
                                      ?? new QualityCheckPreferences();

            if (root.TryGetProperty("openai", out var openAINode))
            {
                config.OpenAI = JsonSerializer.Deserialize<CodexBarOpenAISettings>(openAINode.GetRawText())
                                ?? new CodexBarOpenAISettings();
                // 旧设置没有价格来源字段；与当时内置价不同的条目按手动价格保留。
                var defaults = TokenPricePreset.CreateDefaults();
                foreach (var (model, preset) in config.OpenAI.TokenPricePresets)
                {
                    if (preset.Source != "built_in" || !defaults.TryGetValue(model, out var fallback)) continue;
                    if (preset.InputUsdPerMillion != fallback.InputUsdPerMillion ||
                        preset.CachedInputUsdPerMillion != fallback.CachedInputUsdPerMillion ||
                        preset.OutputUsdPerMillion != fallback.OutputUsdPerMillion)
                        preset.Source = "custom";
                }
            }

            // 校验阈值
            config.OpenAI.AutoRefreshIntervalSeconds = Math.Clamp(config.OpenAI.AutoRefreshIntervalSeconds, 60, 86400);
            config.OpenAI.WarningThresholdPercent = ClampPercent(config.OpenAI.WarningThresholdPercent, 70);
            config.OpenAI.DangerThresholdPercent = ClampPercent(config.OpenAI.DangerThresholdPercent, 90);
            config.OpenAI.EnsurePricingDefaults();
            config.AdvancedKeepAwakeIdleThresholdMs = Math.Clamp(config.AdvancedKeepAwakeIdleThresholdMs, 5_000, 3_600_000);
            config.AdvancedKeepAwakeIntervalMs = Math.Clamp(config.AdvancedKeepAwakeIntervalMs, 1_000, 600_000);
            config.AdvancedKeepAwakeJitterMs = Math.Clamp(config.AdvancedKeepAwakeJitterMs, 0, 120_000);
            config.AdvancedKeepAwakeMovePattern = NormalizeMovePattern(config.AdvancedKeepAwakeMovePattern);
            if (config.OpenAI.DangerThresholdPercent < config.OpenAI.WarningThresholdPercent)
            {
                config.OpenAI.DangerThresholdPercent = Math.Min(100, config.OpenAI.WarningThresholdPercent + 10);
            }

            Config = config;
        }
        catch
        {
            // 配置损坏时回退到默认值。
            Config = new CodexBarConfig();
        }
    }

    public void Save()
    {
        CodexPaths.EnsureDirectories();
        Config.OpenAI.EnsurePricingDefaults();
        var text = JsonSerializer.Serialize(Config, WriteOptions);
        AtomicFile.WriteAllText(CodexPaths.WindowsSettingsPath, text);
    }

    public void Update(Action<CodexBarConfig> mutate)
    {
        if (mutate is null)
        {
            return;
        }

        mutate(Config);
        Save();
    }

    private static bool ReadBool(JsonElement root, string key, bool fallback) =>
        root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : fallback;

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double ClampPercent(double value, double fallback)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 100)
        {
            return fallback;
        }

        return value;
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result))
        {
            return result;
        }

        return fallback;
    }

    private static string NormalizeMovePattern(string? pattern)
    {
        return (pattern ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "micro_jitter" => "micro_jitter",
            "random_walk_box" => "random_walk_box",
            _ => "ping_pong",
        };
    }
}
