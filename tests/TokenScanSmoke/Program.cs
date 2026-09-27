using CodexBarWin.Services;

var summary = TokenUsageScanService.ScanActivity();
Console.WriteLine($"Today={summary.TodayTokens} Week={summary.ThisWeekTokens} Month={summary.ThisMonthTokens} Days={summary.DailyTokens.Count}");
if (args.Contains("--expect-recent") && summary.ThisWeekTokens == 0)
    return 1;
return 0;
