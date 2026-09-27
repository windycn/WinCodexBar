using CodexBarWin.Services;

namespace CodexBarWin.WinUI;

public sealed record ActivityChartPoint(string Label, long Tokens);
public sealed record ActivitySourcePoint(string Name, int TaskCount, int ReportedCount, long Tokens);

public sealed record TokenActivityDashboard(
    long TodayTokens, long WeekTokens, long MonthTokens, long TotalTokens,
    IReadOnlyList<ActivityChartPoint> Daily, IReadOnlyList<ActivityChartPoint> Weekly,
    IReadOnlyList<ActivityChartPoint> Monthly, IReadOnlyList<ActivityChartPoint> Hourly,
    IReadOnlyList<ActivitySourcePoint> Sources, IReadOnlyList<ActivitySourcePoint> Models,
    int DirectTaskCount, int ReportedTaskCount, string PeakHour)
{
    public static TokenActivityDashboard Build(TokenActivitySummary codex, IReadOnlyList<AppUsageEvent> direct, DateTime now)
    {
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var daily = codex.DailyTokens.ToDictionary(item => item.Date.Date, item => item.Tokens);
        var hourly = codex.TodayHourlyTokens.ToDictionary(item => item.Hour.Hour, item => item.Tokens);
        var reported = direct.Where(item => item.HasReportedUsage).ToArray();
        foreach (var item in reported)
        {
            var local = item.At.LocalDateTime;
            var date = local.Date;
            daily[date] = daily.GetValueOrDefault(date) + item.TotalTokens;
            if (date == today) hourly[local.Hour] = hourly.GetValueOrDefault(local.Hour) + item.TotalTokens;
        }
        var days = Enumerable.Range(0, 30).Select(index => today.AddDays(index - 29))
            .Select(date => new ActivityChartPoint(date.ToString("MM-dd"), daily.GetValueOrDefault(date))).ToArray();
        var weeks = Enumerable.Range(0, 12).Select(index => weekStart.AddDays((index - 11) * 7))
            .Select(start => new ActivityChartPoint(start.ToString("MM-dd"), daily.Where(pair => pair.Key >= start && pair.Key < start.AddDays(7)).Sum(pair => pair.Value)))
            .ToArray();
        var months = Enumerable.Range(0, 12).Select(index => monthStart.AddMonths(index - 11))
            .Select(start => new ActivityChartPoint(start.ToString("yyyy-MM"), daily.Where(pair => pair.Key >= start && pair.Key < start.AddMonths(1)).Sum(pair => pair.Value)))
            .ToArray();
        var hours = Enumerable.Range(0, 24)
            .Select(hour => new ActivityChartPoint($"{hour:00}:00", hourly.GetValueOrDefault(hour))).ToArray();
        var sourceRows = reported.GroupBy(item => item.Category)
            .Select(group => new ActivitySourcePoint(group.Key, direct.Count(item => item.Category == group.Key),
                group.Count(), group.Sum(item => item.TotalTokens))).ToList();
        foreach (var group in direct.GroupBy(item => item.Category))
            if (!sourceRows.Any(row => row.Name == group.Key))
                sourceRows.Add(new ActivitySourcePoint(group.Key, group.Count(), 0, 0));
        sourceRows.Add(new ActivitySourcePoint("Codex 会话", 0, 0, codex.TotalTokens));
        var models = direct.GroupBy(item => item.Model)
            .Select(group => new ActivitySourcePoint(group.Key, group.Count(), group.Count(item => item.HasReportedUsage),
                group.Where(item => item.HasReportedUsage).Sum(item => item.TotalTokens)))
            .OrderByDescending(row => row.Tokens).Take(10).ToArray();
        var peak = hours.MaxBy(item => item.Tokens);
        return new TokenActivityDashboard(
            daily.GetValueOrDefault(today), daily.Where(item => item.Key >= weekStart).Sum(item => item.Value),
            daily.Where(item => item.Key >= monthStart).Sum(item => item.Value), daily.Values.Sum(),
            days, weeks, months, hours, sourceRows.OrderByDescending(row => row.Tokens).ToArray(),
            models, direct.Count, reported.Length, peak?.Tokens > 0 ? peak.Label : "暂无数据");
    }
}
