namespace CodexBarWin.WinUI;

public sealed record TokenDayRow(string Date, string Tokens, double Percent);
public sealed record TokenBreakdownRow(string Name, string Tasks, string Tokens);
public sealed record ModelUsageRow(string Model, string Sessions, string Tokens);
public sealed record RecentSessionRow(string Model, string Started, string Duration, string Tokens);
