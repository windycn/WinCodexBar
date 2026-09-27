namespace CodexBarWin.Models;

public sealed record QuotaAlert(string WindowLabel, double RemainingPercent, bool Critical, DateTimeOffset? ResetAt);

/// <summary>Only alert when a freshly checked quota enters a warning band or gets worse.</summary>
public sealed class QuotaAlertTracker
{
    private readonly Dictionary<string, int> _levels = new(StringComparer.Ordinal);

    public IReadOnlyList<QuotaAlert> Evaluate(TokenAccount account, double warningUsed, double dangerUsed)
    {
        var alerts = new List<QuotaAlert>();
        foreach (var window in AccountUsageHelpers.Windows(account))
        {
            var key = account.AccountId + ":" + window.Label;
            var level = window.UsedPercent >= dangerUsed ? 2 : window.UsedPercent >= warningUsed ? 1 : 0;
            _levels.TryGetValue(key, out var previous);
            _levels[key] = level;
            if (level > previous)
                alerts.Add(new QuotaAlert(window.Label, Math.Clamp(100 - window.UsedPercent, 0, 100), level == 2, window.ResetAt));
        }
        return alerts;
    }
}
