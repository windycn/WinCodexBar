using System;
using System.Globalization;

namespace CodexBarWin.Models;

/// <summary>
/// 账号用量相关的纯逻辑计算，集中在这里方便测试和被多个 UI 复用。
/// </summary>
public sealed record AccountUsageWindow(string Label, double UsedPercent, DateTimeOffset? ResetAt);

public static class AccountUsageHelpers
{
    public static IReadOnlyList<AccountUsageWindow> Windows(TokenAccount account)
    {
        var windows = new List<AccountUsageWindow>();
        void Add(bool? available, double used, DateTimeOffset? reset, int? seconds, string fallback)
        {
            // 旧数据没有 available 字段时，只接受明确存在的窗口信息；未知不等于 0%。
            if (!(available ?? (reset.HasValue || seconds.HasValue || used > 0)) || !HasUsageValue(used)) return;
            var label = seconds == 604800 ? "7d" : seconds == 18000 ? "5h" : fallback;
            windows.RemoveAll(w => w.Label == label);
            windows.Add(new(label, Clamp(used), reset));
        }
        Add(account.PrimaryWindowAvailable, account.PrimaryUsedPercent, account.PrimaryResetAt, account.PrimaryLimitWindowSeconds, "5h");
        Add(account.SecondaryWindowAvailable, account.SecondaryUsedPercent, account.SecondaryResetAt, account.SecondaryLimitWindowSeconds, "7d");
        return windows.OrderBy(w => w.Label == "5h" ? 0 : 1).ToArray();
    }

    public static string UsageText(TokenAccount account, UsageDisplayMode mode) => Windows(account).Count == 0
        ? "用量未提供" : string.Join(" · ", Windows(account).Select(w => $"{w.Label} {FormatDisplayPercent(w.UsedPercent, mode)}"));

    public static string ResetText(TokenAccount account) => Windows(account).Count == 0
        ? "等待额度数据" : string.Join(" · ", Windows(account).Select(w => $"{w.Label} {FormatResetCountdown(w.ResetAt)}"));

    public static string AverageText(IEnumerable<TokenAccount> accounts, UsageDisplayMode mode)
    {
        var groups = accounts.SelectMany(Windows).GroupBy(w => w.Label).OrderBy(g => g.Key == "5h" ? 0 : 1);
        var text = string.Join(" · ", groups.Select(g => $"{g.Key} {FormatDisplayPercent(g.Average(w => w.UsedPercent), mode)}"));
        return text.Length == 0 ? "用量未提供" : text;
    }

    public static string ExactTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "未提供";

    public static string CreditSummary(TokenAccount account)
    {
        var count = account.ResetCreditsAvailable is { } n ? $"{n} 次" : "次数未提供";
        var cached = account.ResetCreditDetailsStale || account.ResetCreditsCheckedAt is { } at && DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(10);
        return "重置卡 " + count + (cached ? "（缓存）" : "");
    }

    public static string DetailsText(TokenAccount account, UsageDisplayMode mode)
    {
        var lines = new List<string> { DisplayName(account), "", "额度窗口" };
        foreach (var window in Windows(account))
        {
            lines.Add($"{(window.Label == "5h" ? "5 小时" : "7 天")}：{FormatDisplayPercent(window.UsedPercent, mode)}（{(mode == UsageDisplayMode.Remaining ? "剩余" : "已用")}）");
            lines.Add("重置时间：" + ExactTime(window.ResetAt));
            lines.Add("倒计时：" + FormatResetCountdown(window.ResetAt));
            lines.Add("");
        }
        if (Windows(account).Count == 0) lines.Add("额度数据未提供");
        lines.Add(CreditSummary(account));
        if (account.ResetCreditsAvailable != 0)
        {
            if (account.ResetCreditDetails is null) lines.Add("到期时间：未提供");
            else
            {
                for (var i = 0; i < account.ResetCreditDetails.Count; i++)
                {
                    var credit = account.ResetCreditDetails[i];
                    var expiry = !credit.ExpirationKnown ? "未提供" : credit.ExpiresAt is null ? "无到期限制" : ExactTime(credit.ExpiresAt);
                    lines.Add($"第 {i + 1} 张到期：{expiry}");
                }
                if (account.ResetCreditDetails.Count < account.ResetCreditsAvailable)
                    lines.Add("其余重置卡的到期时间未提供。");
            }
        }
        lines.Add("");
        lines.Add("时间按本机时区显示，末尾为 UTC 偏移。");
        lines.Add("重置卡资料更新时间：" + ExactTime(account.ResetCreditsCheckedAt));
        return string.Join(Environment.NewLine, lines);
    }

    public static double Clamp(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    }

    public static bool HasUsageValue(double value)
    {
        return double.IsFinite(value) && value >= 0;
    }

    public static double MaxUsage(TokenAccount account)
    {
        return Windows(account).Select(w => w.UsedPercent).DefaultIfEmpty(0).Max();
    }

    public static AccountHealthStatus Health(
        TokenAccount account,
        double warningThreshold,
        double dangerThreshold)
    {
        if (account.IsSuspended)
        {
            return AccountHealthStatus.Suspended;
        }

        if (account.TokenExpired)
        {
            return AccountHealthStatus.TokenExpired;
        }

        var usage = MaxUsage(account);
        if (usage >= 100)
        {
            return AccountHealthStatus.Exhausted;
        }

        if (usage >= dangerThreshold)
        {
            return AccountHealthStatus.Exhausted;
        }

        if (usage >= warningThreshold)
        {
            return AccountHealthStatus.Warning;
        }

        if (account.LastChecked is null || Windows(account).Count == 0)
        {
            return AccountHealthStatus.Unknown;
        }

        return AccountHealthStatus.Healthy;
    }

    public static string HealthLabel(AccountHealthStatus status)
    {
        return status switch
        {
            AccountHealthStatus.Healthy => "正常",
            AccountHealthStatus.Warning => "警戒",
            AccountHealthStatus.Exhausted => "额度耗尽",
            AccountHealthStatus.Suspended => "已停用",
            AccountHealthStatus.TokenExpired => "需重新授权",
            AccountHealthStatus.Unknown => "未刷新",
            _ => "未知",
        };
    }

    public static double DisplayPercent(double usedPercent, UsageDisplayMode mode)
    {
        var clamped = Clamp(usedPercent);
        return mode == UsageDisplayMode.Remaining ? Math.Max(0, 100 - clamped) : clamped;
    }

    public static string FormatDisplayPercent(double usedPercent, UsageDisplayMode mode)
    {
        if (!HasUsageValue(usedPercent))
        {
            return "--";
        }

        return DisplayPercent(usedPercent, mode).ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    public static string FormatUsedPercent(double usedPercent)
    {
        return HasUsageValue(usedPercent)
            ? Clamp(usedPercent).ToString("F1", CultureInfo.InvariantCulture) + "%"
            : "--";
    }

    public static string FormatRemainingPercent(double usedPercent)
    {
        return HasUsageValue(usedPercent)
            ? Math.Max(0, 100 - Clamp(usedPercent)).ToString("F1", CultureInfo.InvariantCulture) + "%"
            : "--";
    }

    public static string FormatResetCountdown(DateTimeOffset? resetAt, DateTimeOffset? now = null)
    {
        if (resetAt is null)
        {
            return "--";
        }

        var current = now ?? DateTimeOffset.Now;
        var span = resetAt.Value.ToLocalTime() - current.ToLocalTime();
        if (span.TotalSeconds <= 0)
        {
            return "即将重置";
        }

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}天{span.Hours:D2}时";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}时{span.Minutes:D2}分";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)}分钟";
    }

    public static string FormatLastChecked(DateTimeOffset? lastChecked, DateTimeOffset? now = null)
    {
        if (lastChecked is null)
        {
            return "未刷新";
        }

        var current = now ?? DateTimeOffset.UtcNow;
        var span = current - lastChecked.Value.ToUniversalTime();
        if (span.TotalSeconds < 30)
        {
            return "刚刚刷新";
        }

        if (span.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)span.TotalSeconds)} 秒前";
        }

        if (span.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟前";
        }

        if (span.TotalDays < 1)
        {
            return $"{(int)span.TotalHours} 小时前";
        }

        return lastChecked.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string FormatTokenCount(long tokens, TokenUnitDisplayMode mode)
    {
        if (tokens <= 0)
        {
            return "0";
        }

        if (mode == TokenUnitDisplayMode.Compact)
        {
            if (tokens >= 1_000_000_000) return $"{tokens / 1_000_000_000d:0.##}B";
            if (tokens >= 1_000_000) return $"{tokens / 1_000_000d:0.##}M";
            if (tokens >= 10_000) return $"{tokens / 1_000d:0.#}K";
            return tokens.ToString("N0", CultureInfo.InvariantCulture);
        }

        if (tokens >= 100_000_000) return $"{tokens / 100_000_000d:0.##}亿";
        if (tokens >= 10_000) return $"{tokens / 10_000d:0.##}万";
        return tokens.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string DisplayName(TokenAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.Email))
        {
            return account.Email;
        }

        if (!string.IsNullOrWhiteSpace(account.OrganizationName))
        {
            return account.OrganizationName!;
        }

        return string.IsNullOrWhiteSpace(account.AccountId) ? "未命名账号" : account.AccountId;
    }

    public static string PlanLabel(TokenAccount account)
    {
        var plan = string.IsNullOrWhiteSpace(account.PlanType) ? "free" : account.PlanType;
        return plan.ToUpperInvariant();
    }
}
