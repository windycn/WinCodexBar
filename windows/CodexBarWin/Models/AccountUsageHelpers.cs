using System;
using System.Globalization;

namespace CodexBarWin.Models;

/// <summary>
/// 账号用量相关的纯逻辑计算，集中在这里方便测试和被多个 UI 复用。
/// </summary>
public static class AccountUsageHelpers
{
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
        return Math.Max(Clamp(account.PrimaryUsedPercent), Clamp(account.SecondaryUsedPercent));
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

        if (account.LastChecked is null)
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
