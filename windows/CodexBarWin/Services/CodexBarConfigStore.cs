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
            if (root.TryGetProperty("keep_awake_enabled", out var keepAwakeNew)
                && keepAwakeNew.ValueKind == JsonValueKind.True)
            {
                config.KeepAwakeEnabled = true;
            }
            else if (root.TryGetProperty("KeepAwakeEnabled", out var keepAwakeLegacy)
                     && keepAwakeLegacy.ValueKind == JsonValueKind.True)
            {
                config.KeepAwakeEnabled = true;
            }

            if (root.TryGetProperty("advanced_keep_awake_enabled", out var advancedKeepAwake)
                && advancedKeepAwake.ValueKind == JsonValueKind.True)
            {
                config.AdvancedKeepAwakeEnabled = true;
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

            if (root.TryGetProperty("openai", out var openAINode))
            {
                config.OpenAI = JsonSerializer.Deserialize<CodexBarOpenAISettings>(openAINode.GetRawText())
                                ?? new CodexBarOpenAISettings();
            }

            // 校验阈值
            config.OpenAI.AutoRefreshIntervalSeconds = Math.Max(60, config.OpenAI.AutoRefreshIntervalSeconds);
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
        File.WriteAllText(CodexPaths.WindowsSettingsPath, text);
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
