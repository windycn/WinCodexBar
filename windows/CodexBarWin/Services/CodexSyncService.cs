using CodexBarWin.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBarWin.Services;

/// <summary>
/// 把当前激活账号写入 ~/.codex/auth.json 与 ~/.codex/config.toml。
/// 行为对齐 macOS 端 CodexSyncService（含备份、模型/服务等级 upsert）。
/// </summary>
public sealed class CodexSyncService
{
    private readonly CodexBarConfigStore _configStore;

    public CodexSyncService(CodexBarConfigStore configStore)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
    }

    public void SyncActiveTokenAccount(TokenAccount account)
    {
        Sync(account, routeThroughGateway: false);
    }

    public void SyncForCurrentMode(TokenAccount account)
    {
        var routeThroughGateway = _configStore.Config.OpenAI.AccountUsageMode == AccountUsageMode.AggregateGateway;
        Sync(account, routeThroughGateway);
    }

    private void Sync(TokenAccount account, bool routeThroughGateway)
    {
        if (account is null) throw new ArgumentNullException(nameof(account));

        CodexPaths.EnsureDirectories();
        BackupIfPresent(CodexPaths.AuthPath, CodexPaths.AuthBackupPath);
        BackupIfPresent(CodexPaths.ConfigTomlPath, CodexPaths.ConfigBackupPath);
        WriteAuthJson(account);
        WriteConfigToml(account, routeThroughGateway);
    }

    private static void BackupIfPresent(string source, string destination)
    {
        try
        {
            if (!File.Exists(source))
            {
                return;
            }

            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(source, destination, overwrite: true);
        }
        catch
        {
            // 备份只是辅助，失败不阻断同步主流程。
        }
    }

    private static void WriteAuthJson(TokenAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AccessToken)
            || string.IsNullOrWhiteSpace(account.RefreshToken)
            || string.IsNullOrWhiteSpace(account.IdToken))
        {
            throw new InvalidOperationException("当前账号缺少 OAuth tokens，无法同步到 Codex。");
        }

        var lastRefresh = (account.TokenLastRefreshAt ?? DateTimeOffset.UtcNow)
            .ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ssZ");

        var accountId = string.IsNullOrWhiteSpace(account.OpenAIAccountId)
            ? account.AccountId
            : account.OpenAIAccountId;

        var payload = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["auth_mode"] = "chatgpt",
            ["OPENAI_API_KEY"] = null,
            ["last_refresh"] = lastRefresh,
            ["tokens"] = new System.Collections.Generic.Dictionary<string, string>
            {
                ["access_token"] = account.AccessToken,
                ["refresh_token"] = account.RefreshToken,
                ["id_token"] = account.IdToken,
                ["account_id"] = accountId,
            },
        };

        if (!string.IsNullOrWhiteSpace(account.OAuthClientId))
        {
            payload["client_id"] = account.OAuthClientId;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(CodexPaths.AuthPath, JsonSerializer.Serialize(payload, options));
    }

    private void WriteConfigToml(TokenAccount account, bool routeThroughGateway)
    {
        var text = File.Exists(CodexPaths.ConfigTomlPath)
            ? File.ReadAllText(CodexPaths.ConfigTomlPath)
            : string.Empty;

        // 清理与 codexbar 互斥的旧 provider 段。
        text = RemoveSection(text, "model_providers.CodexbarRemote");
        text = RemoveSection(text, "model_providers.OpenAI");
        text = RemoveSection(text, "model_providers.openai");
        text = RemoveKey(text, "openai_base_url");
        text = RemoveKey(text, "model_catalog_json");
        text = RemoveKey(text, "preferred_auth_method");
        text = RemoveKey(text, "oss_provider");

        var settings = _configStore.Config.Global;
        text = UpsertKey(text, "model_provider", Quote("openai"));
        text = UpsertKey(text, "model", Quote(settings.DefaultModel));
        text = UpsertKey(text, "review_model", Quote(string.IsNullOrWhiteSpace(settings.ReviewModel) ? settings.DefaultModel : settings.ReviewModel));
        text = UpsertKey(text, "model_reasoning_effort", Quote(string.IsNullOrWhiteSpace(settings.ReasoningEffort) ? "medium" : settings.ReasoningEffort));
        text = UpsertKey(text, "service_tier", Quote(string.IsNullOrWhiteSpace(settings.ServiceTier) ? "standard" : settings.ServiceTier));
        if (routeThroughGateway)
        {
            text = UpsertKey(text, "openai_base_url", Quote(OpenAIAccountGatewayService.BaseUrl));
        }

        File.WriteAllText(CodexPaths.ConfigTomlPath, text.TrimEnd() + "\n");
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }

    private static string UpsertKey(string tomlText, string key, string value)
    {
        var pattern = $@"(?m)^\s*{Regex.Escape(key)}\s*=.*$";
        if (Regex.IsMatch(tomlText, pattern))
        {
            return Regex.Replace(tomlText, pattern, $"{key} = {value}");
        }

        var prefix = string.IsNullOrWhiteSpace(tomlText.Trim()) ? string.Empty : "\n";
        return tomlText.TrimEnd() + prefix + $"{key} = {value}\n";
    }

    private static string RemoveKey(string tomlText, string key)
    {
        var pattern = $@"(?m)^\s*{Regex.Escape(key)}\s*=.*(?:\r?\n|$)";
        return Regex.Replace(tomlText, pattern, string.Empty);
    }

    private static string RemoveSection(string tomlText, string sectionName)
    {
        var pattern = $@"(?ms)^\[\s*{Regex.Escape(sectionName)}\s*\].*?(?=^\[|\Z)";
        return Regex.Replace(tomlText, pattern, string.Empty);
    }
}
