using CodexBarWin.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexBarWin.Services;

public readonly record struct TokenUsageSummary(
    long Last24HoursTokens,
    long Last30DaysTokens,
    double? PrimaryUsedPercent = null,
    double? SecondaryUsedPercent = null,
    int? PrimaryWindowSeconds = null,
    int? SecondaryWindowSeconds = null,
    DateTimeOffset? PrimaryResetAt = null,
    DateTimeOffset? SecondaryResetAt = null,
    string? PlanType = null,
    DateTimeOffset? ObservedAt = null,
    long TodayTokens = 0,
    long ThisWeekTokens = 0,
    long ThisMonthTokens = 0);

public readonly record struct TokenUsagePoint(DateTime Date, long Tokens);

public readonly record struct TokenHourlyUsagePoint(DateTime Hour, long Tokens);

public sealed record TokenActivitySummary(
    IReadOnlyList<TokenUsagePoint> DailyTokens,
    IReadOnlyList<TokenHourlyUsagePoint> TodayHourlyTokens,
    long TotalTokens,
    long PeakDailyTokens,
    DateTime? PeakDate,
    TimeSpan LongestTaskDuration,
    int CurrentStreakDays,
    int LongestStreakDays,
    long TodayTokens,
    long ThisWeekTokens,
    long ThisMonthTokens,
    TokenCostBreakdown TodayCostTokens,
    TokenCostBreakdown ThisWeekCostTokens,
    TokenCostBreakdown ThisMonthCostTokens,
    TokenCostBreakdown TotalCostTokens)
{
    public static TokenActivitySummary Empty { get; } = new(
        Array.Empty<TokenUsagePoint>(),
        Array.Empty<TokenHourlyUsagePoint>(),
        0,
        0,
        null,
        TimeSpan.Zero,
        0,
        0,
        0,
        0,
        0,
        TokenCostBreakdown.Empty,
        TokenCostBreakdown.Empty,
        TokenCostBreakdown.Empty,
        TokenCostBreakdown.Empty);
}

public sealed record SessionInsight(
    string SessionId,
    string Model,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActivityAt,
    TimeSpan Duration,
    long TotalTokens,
    int EventCount,
    bool IsArchived);

public sealed record ModelTokenSummary(
    string Model,
    long Tokens,
    int SessionCount);

public sealed record SessionAnalysisSummary(
    int SessionCount,
    int ActiveSessionCount,
    int ArchivedSessionCount,
    int ModelCount,
    long TotalTokens,
    long AverageTokensPerSession,
    TimeSpan LongestSessionDuration,
    IReadOnlyList<SessionInsight> RecentSessions,
    IReadOnlyList<SessionInsight> TopTokenSessions,
    IReadOnlyList<ModelTokenSummary> ModelTokens)
{
    public static SessionAnalysisSummary Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        TimeSpan.Zero,
        Array.Empty<SessionInsight>(),
        Array.Empty<SessionInsight>(),
        Array.Empty<ModelTokenSummary>());
}

public static class TokenUsageScanService
{
    public static TokenUsageSummary Scan()
    {
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.ToLocalTime().Date;
        var weekStart = todayStart.AddDays(-(((int)todayStart.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(todayStart.Year, todayStart.Month, 1);
        long last24h = 0;
        long last30d = 0;
        long today = 0;
        long thisWeek = 0;
        long thisMonth = 0;
        double? primaryUsed = null;
        double? secondaryUsed = null;
        int? primaryWindowSeconds = null;
        int? secondaryWindowSeconds = null;
        DateTimeOffset? primaryResetAt = null;
        DateTimeOffset? secondaryResetAt = null;
        string? planType = null;
        DateTimeOffset? latestRateLimitAt = null;

        foreach (var file in EnumerateSessionFiles())
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (!line.Contains("\"token_count\"", StringComparison.Ordinal)
                        || !line.Contains("\"last_token_usage\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!TryReadTimestamp(root, out var timestamp))
                    {
                        continue;
                    }

                    if (!root.TryGetProperty("payload", out var payload)
                        || !payload.TryGetProperty("info", out var info)
                        || !info.TryGetProperty("last_token_usage", out var lastUsage))
                    {
                        continue;
                    }

                    var usage = ReadTokenUsage(lastUsage);
                    var tokens = usage.TotalTokens;

                    if (tokens <= 0)
                    {
                        continue;
                    }

                    var age = now - timestamp;
                    var localDate = timestamp.ToLocalTime().Date;
                    if (localDate == todayStart)
                    {
                        today += tokens;
                    }

                    if (localDate >= weekStart)
                    {
                        thisWeek += tokens;
                    }

                    if (localDate >= monthStart)
                    {
                        thisMonth += tokens;
                    }

                    if (age.TotalDays <= 30)
                    {
                        last30d += tokens;
                    }

                    if (age.TotalHours <= 24)
                    {
                        last24h += tokens;
                    }

                    if (payload.TryGetProperty("rate_limits", out var rateLimits)
                        && rateLimits.ValueKind == JsonValueKind.Object
                        && (latestRateLimitAt is null || timestamp > latestRateLimitAt))
                    {
                        latestRateLimitAt = timestamp;
                        primaryUsed = ReadWindowUsed(rateLimits, "primary");
                        secondaryUsed = ReadWindowUsed(rateLimits, "secondary");
                        primaryWindowSeconds = ReadWindowSeconds(rateLimits, "primary");
                        secondaryWindowSeconds = ReadWindowSeconds(rateLimits, "secondary");
                        primaryResetAt = ReadWindowReset(rateLimits, "primary");
                        secondaryResetAt = ReadWindowReset(rateLimits, "secondary");
                        planType = ReadString(rateLimits, "plan_type") ?? planType;
                    }
                }
            }
            catch
            {
                // 单个损坏 session 不影响整体统计。
            }
        }

        return new TokenUsageSummary(
            last24h,
            last30d,
            primaryUsed,
            secondaryUsed,
            primaryWindowSeconds,
            secondaryWindowSeconds,
            primaryResetAt,
            secondaryResetAt,
            planType,
            latestRateLimitAt,
            today,
            thisWeek,
            thisMonth);
    }

    public static TokenActivitySummary ScanActivity()
    {
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.ToLocalTime().Date;
        var weekStart = todayStart.AddDays(-(((int)todayStart.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(todayStart.Year, todayStart.Month, 1);
        var dailyTokens = new Dictionary<DateTime, long>();
        var todayHourlyTokens = new Dictionary<DateTime, long>();
        var taskDurations = new List<TimeSpan>();
        var todayCost = TokenCostBreakdown.Empty;
        var thisWeekCost = TokenCostBreakdown.Empty;
        var thisMonthCost = TokenCostBreakdown.Empty;
        var totalCost = TokenCostBreakdown.Empty;

        foreach (var file in EnumerateSessionFiles())
        {
            try
            {
                DateTimeOffset? first = null;
                DateTimeOffset? last = null;
                foreach (var line in File.ReadLines(file))
                {
                    if (!line.Contains("\"token_count\"", StringComparison.Ordinal)
                        || !line.Contains("\"last_token_usage\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!TryReadTimestamp(root, out var timestamp))
                    {
                        continue;
                    }

                    if (!root.TryGetProperty("payload", out var payload)
                        || !payload.TryGetProperty("info", out var info)
                        || !info.TryGetProperty("last_token_usage", out var lastUsage))
                    {
                        continue;
                    }

                    var usage = ReadTokenUsage(lastUsage);
                    var tokens = usage.TotalTokens;

                    if (tokens <= 0)
                    {
                        continue;
                    }

                    var date = timestamp.ToLocalTime().Date;
                    totalCost = TokenCostBreakdown.Add(totalCost, usage);
                    dailyTokens[date] = dailyTokens.TryGetValue(date, out var existing)
                        ? existing + tokens
                        : tokens;

                    if (date == todayStart)
                    {
                        todayCost = TokenCostBreakdown.Add(todayCost, usage);
                        var localTime = timestamp.ToLocalTime();
                        var hour = new DateTime(localTime.Year, localTime.Month, localTime.Day, localTime.Hour, 0, 0);
                        todayHourlyTokens[hour] = todayHourlyTokens.TryGetValue(hour, out var hourly)
                            ? hourly + tokens
                            : tokens;
                    }

                    if (date >= weekStart)
                    {
                        thisWeekCost = TokenCostBreakdown.Add(thisWeekCost, usage);
                    }

                    if (date >= monthStart)
                    {
                        thisMonthCost = TokenCostBreakdown.Add(thisMonthCost, usage);
                    }

                    first = first is null || timestamp < first ? timestamp : first;
                    last = last is null || timestamp > last ? timestamp : last;
                }

                if (first is not null && last is not null && last.Value > first.Value)
                {
                    taskDurations.Add(last.Value - first.Value);
                }
            }
            catch
            {
                // 单个损坏 session 不影响整体统计。
            }
        }

        if (dailyTokens.Count == 0)
        {
            return TokenActivitySummary.Empty;
        }

        var ordered = dailyTokens
            .OrderBy(pair => pair.Key)
            .Select(pair => new TokenUsagePoint(pair.Key, pair.Value))
            .ToArray();
        var orderedHours = todayHourlyTokens
            .OrderBy(pair => pair.Key)
            .Select(pair => new TokenHourlyUsagePoint(pair.Key, pair.Value))
            .ToArray();
        var peak = ordered.OrderByDescending(point => point.Tokens).First();
        var total = ordered.Sum(point => point.Tokens);
        var today = dailyTokens.TryGetValue(todayStart, out var todayValue) ? todayValue : 0;
        var thisWeek = ordered.Where(point => point.Date >= weekStart).Sum(point => point.Tokens);
        var thisMonth = ordered.Where(point => point.Date >= monthStart).Sum(point => point.Tokens);
        var activeDays = dailyTokens.Keys.ToHashSet();

        return new TokenActivitySummary(
            ordered,
            orderedHours,
            total,
            peak.Tokens,
            peak.Date,
            taskDurations.Count == 0 ? TimeSpan.Zero : taskDurations.Max(),
            CalculateCurrentStreak(activeDays, todayStart),
            CalculateLongestStreak(activeDays),
            today,
            thisWeek,
            thisMonth,
            todayCost,
            thisWeekCost,
            thisMonthCost,
            totalCost);
    }

    public static SessionAnalysisSummary ScanSessions()
    {
        var sessions = new List<SessionInsight>();
        var archivedRoot = Path.Combine(CodexPaths.CodexRoot, "archived_sessions");

        foreach (var file in EnumerateSessionFiles())
        {
            try
            {
                var sessionId = Path.GetFileNameWithoutExtension(file);
                var archived = file.StartsWith(archivedRoot, StringComparison.OrdinalIgnoreCase);
                DateTimeOffset? first = null;
                DateTimeOffset? last = null;
                var model = string.Empty;
                long tokens = 0;
                var events = 0;

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (TryReadTimestamp(root, out var timestamp))
                    {
                        first = first is null || timestamp < first ? timestamp : first;
                        last = last is null || timestamp > last ? timestamp : last;
                        events++;
                    }

                    if (string.IsNullOrWhiteSpace(model))
                    {
                        model = FindString(root, "model") ?? FindString(root, "model_slug") ?? string.Empty;
                    }

                    if (TryReadLastTokenUsage(root, out var lastUsage))
                    {
                        var usage = ReadTokenUsage(lastUsage);
                        if (usage.TotalTokens > 0)
                        {
                            tokens += usage.TotalTokens;
                        }
                    }
                }

                if (events == 0 && tokens <= 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(model))
                {
                    model = "unknown";
                }

                var duration = first is not null && last is not null && last.Value > first.Value
                    ? last.Value - first.Value
                    : TimeSpan.Zero;
                sessions.Add(new SessionInsight(sessionId, model, first, last, duration, tokens, events, archived));
            }
            catch
            {
                // 单个损坏 session 不影响整体分析。
            }
        }

        if (sessions.Count == 0)
        {
            return SessionAnalysisSummary.Empty;
        }

        var recent = sessions
            .OrderByDescending(item => item.LastActivityAt ?? item.StartedAt ?? DateTimeOffset.MinValue)
            .Take(5)
            .ToArray();
        var top = sessions
            .OrderByDescending(item => item.TotalTokens)
            .ThenByDescending(item => item.LastActivityAt ?? item.StartedAt ?? DateTimeOffset.MinValue)
            .Take(5)
            .ToArray();
        var modelTokens = sessions
            .GroupBy(item => item.Model, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new ModelTokenSummary(
                group.Key,
                group.Sum(item => item.TotalTokens),
                group.Count()))
            .OrderByDescending(item => item.Tokens)
            .Take(6)
            .ToArray();

        var total = sessions.Sum(item => item.TotalTokens);
        return new SessionAnalysisSummary(
            sessions.Count,
            sessions.Count(item => !item.IsArchived),
            sessions.Count(item => item.IsArchived),
            modelTokens.Length,
            total,
            sessions.Count == 0 ? 0 : total / sessions.Count,
            sessions.Max(item => item.Duration),
            recent,
            top,
            modelTokens);
    }

    private static TokenCostBreakdown ReadTokenUsage(JsonElement lastUsage)
    {
        var input = ReadLong(lastUsage, "input_tokens") ?? 0;
        var output = ReadLong(lastUsage, "output_tokens") ?? 0;
        var cached = ReadLong(lastUsage, "cached_input_tokens")
            ?? ReadLong(lastUsage, "cache_read_input_tokens")
            ?? ReadLong(lastUsage, "cached_tokens")
            ?? ReadNestedLong(lastUsage, "input_token_details", "cached_tokens")
            ?? ReadNestedLong(lastUsage, "input_tokens_details", "cached_tokens")
            ?? 0;
        var total = ReadLong(lastUsage, "total_tokens") ?? input + output;

        if (input <= 0 && output <= 0 && total > 0)
        {
            input = total;
        }

        if (total <= 0)
        {
            total = input + output;
        }

        cached = Math.Clamp(cached, 0, Math.Max(0, input));
        return new TokenCostBreakdown(input, cached, output, total);
    }

    private static bool TryReadLastTokenUsage(JsonElement root, out JsonElement lastUsage)
    {
        if (root.TryGetProperty("payload", out var payload)
            && payload.TryGetProperty("info", out var info)
            && info.TryGetProperty("last_token_usage", out lastUsage))
        {
            return true;
        }

        return TryFindProperty(root, "last_token_usage", out lastUsage);
    }

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value, int depth = 0)
    {
        value = default;
        if (depth > 8)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    value = property.Value;
                    return true;
                }

                if (TryFindProperty(property.Value, name, out value, depth + 1))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, name, out value, depth + 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? FindString(JsonElement element, string name, int depth = 0)
    {
        if (depth > 8)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString();
                }

                var nested = FindString(property.Value, name, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, name, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSessionFiles()
    {
        var sessions = Path.Combine(CodexPaths.CodexRoot, "sessions");
        if (Directory.Exists(sessions))
        {
            foreach (var file in Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }

        var archived = Path.Combine(CodexPaths.CodexRoot, "archived_sessions");
        if (Directory.Exists(archived))
        {
            foreach (var file in Directory.EnumerateFiles(archived, "*.jsonl", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);
    }

    private static double? ReadWindowUsed(JsonElement rateLimits, string name)
    {
        return rateLimits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object
            ? ReadDouble(window, "used_percent")
            : null;
    }

    private static int? ReadWindowSeconds(JsonElement rateLimits, string name)
    {
        if (!rateLimits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var seconds = ReadInt(window, "limit_window_seconds");
        if (seconds is not null)
        {
            return seconds;
        }

        var minutes = ReadInt(window, "window_minutes");
        return minutes is null ? null : minutes.Value * 60;
    }

    private static DateTimeOffset? ReadWindowReset(JsonElement rateLimits, string name)
    {
        return rateLimits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object
            ? ReadUnixTime(window, "resets_at") ?? ReadUnixTime(window, "reset_at")
            : null;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static long? ReadNestedLong(JsonElement element, string objectName, string name)
    {
        return element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadLong(nested, name)
            : null;
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

        long? seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };

        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int CalculateCurrentStreak(HashSet<DateTime> activeDays, DateTime today)
    {
        var streak = 0;
        for (var date = today; activeDays.Contains(date); date = date.AddDays(-1))
        {
            streak++;
        }

        return streak;
    }

    private static int CalculateLongestStreak(HashSet<DateTime> activeDays)
    {
        var longest = 0;
        var current = 0;
        DateTime? previous = null;
        foreach (var date in activeDays.OrderBy(date => date))
        {
            current = previous is not null && date == previous.Value.AddDays(1)
                ? current + 1
                : 1;
            longest = Math.Max(longest, current);
            previous = date;
        }

        return longest;
    }
}
