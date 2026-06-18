using CodexBarWin.Interop;
using CodexBarWin.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexBarWin.Services;

public class AccountRegistry
{
    public IReadOnlyList<TokenAccount> Accounts { get; private set; } = Array.Empty<TokenAccount>();

    public Dictionary<string, OAuthAccountInteropMetadata> MetadataByAccountId { get; private set; } = new();

    public string? ProxiesJSON { get; private set; }

    public string? ActiveAccountId { get; private set; }

    public void Load()
    {
        Accounts = Array.Empty<TokenAccount>();
        MetadataByAccountId = new Dictionary<string, OAuthAccountInteropMetadata>(StringComparer.Ordinal);
        ProxiesJSON = null;
        ActiveAccountId = null;

        if (!File.Exists(CodexPaths.WindowsRegistryPath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(CodexPaths.WindowsRegistryPath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<WindowsRegistryPayload>(raw);
            if (payload is null)
            {
                return;
            }

            Accounts = payload.Accounts?.ToArray() ?? Array.Empty<TokenAccount>();
            MetadataByAccountId = payload.MetadataByAccountId ?? new Dictionary<string, OAuthAccountInteropMetadata>(StringComparer.Ordinal);
            ProxiesJSON = payload.ProxiesJSON;
            ActiveAccountId = payload.ActiveAccountId;

            var validIds = Accounts.Select(a => a.AccountId).ToHashSet(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(ActiveAccountId) && validIds.Contains(ActiveAccountId) == false)
            {
                ActiveAccountId = null;
            }
        }
        catch
        {
            // 兼容老数据：解析失败时重置为空列表，不打断托盘应用启动
            Accounts = Array.Empty<TokenAccount>();
            MetadataByAccountId = new Dictionary<string, OAuthAccountInteropMetadata>(StringComparer.Ordinal);
            ProxiesJSON = null;
            ActiveAccountId = null;
        }
    }

    public void Save()
    {
        CodexPaths.EnsureDirectories();

        var payload = new WindowsRegistryPayload
        {
            ActiveAccountId = ActiveAccountId,
            ProxiesJSON = ProxiesJSON,
            Accounts = Accounts.ToList(),
            MetadataByAccountId = MetadataByAccountId,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        File.WriteAllText(CodexPaths.WindowsRegistryPath, JsonSerializer.Serialize(payload, options));
    }

    public void SetActive(string? accountId)
    {
        ActiveAccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId;
    }

    public void ReplaceAccounts(IEnumerable<TokenAccount> accounts)
    {
        Accounts = accounts.OrderBy(a => a.Email).ToList();
        var ids = Accounts.Select(a => a.AccountId).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(ActiveAccountId) || ids.Contains(ActiveAccountId) == false)
        {
            ActiveAccountId = null;
        }
    }

    public void UpsertAccount(TokenAccount account, bool activate)
    {
        if (account is null || string.IsNullOrWhiteSpace(account.AccountId))
        {
            return;
        }

        var merged = Accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.AccountId))
            .ToDictionary(a => a.AccountId, StringComparer.Ordinal);

        merged[account.AccountId] = account;
        ReplaceAccounts(merged.Values);

        if (activate || string.IsNullOrWhiteSpace(ActiveAccountId))
        {
            ActiveAccountId = account.AccountId;
        }
    }

    public bool RemoveAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return false;
        }

        var remaining = Accounts
            .Where(account => !string.Equals(account.AccountId, accountId, StringComparison.Ordinal))
            .ToArray();

        if (remaining.Length == Accounts.Count)
        {
            return false;
        }

        Accounts = remaining;
        MetadataByAccountId.Remove(accountId);

        if (string.Equals(ActiveAccountId, accountId, StringComparison.Ordinal))
        {
            ActiveAccountId = Accounts.FirstOrDefault()?.AccountId;
        }

        return true;
    }

    public void MergeImportedAccounts(
        IEnumerable<TokenAccount> imported,
        OAuthAccountImportInterchangeContext context
    )
    {
        var merged = Accounts
            .Where(a => a.AccountId.Length > 0)
            .ToDictionary(a => a.AccountId, StringComparer.Ordinal);

        foreach (var account in imported)
        {
            merged[account.AccountId] = account;
        }

        foreach (var item in context.AccountMetadataByID)
        {
            MetadataByAccountId[item.Key] = item.Value;
        }

        if (!string.IsNullOrWhiteSpace(context.ProxiesJSON))
        {
            ProxiesJSON = context.ProxiesJSON;
        }

        ReplaceAccounts(merged.Values);
    }

    private sealed class WindowsRegistryPayload
    {
        public string? ActiveAccountId { get; set; }
        public List<TokenAccount>? Accounts { get; set; }
        public Dictionary<string, OAuthAccountInteropMetadata>? MetadataByAccountId { get; set; }
        public string? ProxiesJSON { get; set; }
    }
}
