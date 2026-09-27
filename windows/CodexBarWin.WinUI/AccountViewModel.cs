using CodexBarWin.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace CodexBarWin.WinUI;

public sealed class AccountViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _plan = string.Empty;
    private string _switchLabel = "切换";
    private bool _canSwitch = true;
    private string _usage = string.Empty;
    private string _reset = string.Empty;
    private string _shortReset = string.Empty;
    private string? _resetCardTooltip;
    private Brush _cardBackground = NormalBackground;
    private Brush _cardBorder = NormalBorder;
    private Brush _healthBrush = UnknownBrush;
    private static readonly Brush NormalBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
    private static readonly Brush ActiveBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 243, 255));
    private static readonly Brush NormalBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 231, 238));
    private static readonly Brush ActiveBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 118, 210));
    private static readonly Brush HealthyBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 150, 92));
    private static readonly Brush WarningBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 202, 121, 0));
    private static readonly Brush DangerBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 216, 48, 55));
    private static readonly Brush UnknownBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 125, 139, 154));

    public TokenAccount Account { get; private set; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Plan { get => _plan; private set => Set(ref _plan, value); }
    public string SwitchLabel { get => _switchLabel; private set => Set(ref _switchLabel, value); }
    public bool CanSwitch { get => _canSwitch; private set => Set(ref _canSwitch, value); }
    public string Usage { get => _usage; private set => Set(ref _usage, value); }
    public string Reset { get => _reset; private set => Set(ref _reset, value); }
    public string ShortReset { get => _shortReset; private set => Set(ref _shortReset, value); }
    public string? ResetCardTooltip { get => _resetCardTooltip; private set => Set(ref _resetCardTooltip, value); }
    public Brush CardBackground { get => _cardBackground; private set => Set(ref _cardBackground, value); }
    public Brush CardBorder { get => _cardBorder; private set => Set(ref _cardBorder, value); }
    public Brush HealthBrush { get => _healthBrush; private set => Set(ref _healthBrush, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AccountViewModel(TokenAccount account, UsageDisplayMode mode, bool active, double warningThreshold, double dangerThreshold)
    {
        Account = account;
        Update(account, mode, active, warningThreshold, dangerThreshold);
    }

    public void Update(TokenAccount account, UsageDisplayMode mode, bool active, double warningThreshold, double dangerThreshold)
    {
        Account = account;
        Name = AccountUsageHelpers.DisplayName(account);
        Plan = AccountUsageHelpers.PlanLabel(account) + (active ? " · 当前" : string.Empty);
        SwitchLabel = active ? "当前" : "切换";
        CanSwitch = !active;
        Usage = AccountUsageHelpers.UsageText(account, mode);
        Reset = AccountUsageHelpers.ResetText(account);
        ShortReset = string.Join(" · ", AccountUsageHelpers.Windows(account).Select(w =>
            $"{w.Label} 重置 {w.ResetAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "未提供"}"));
        var creditCount = Math.Max(account.ResetCreditsAvailable ?? 0, account.ResetCreditDetails?.Count ?? 0);
        ResetCardTooltip = creditCount == 0 ? null : string.Join(Environment.NewLine,
            Enumerable.Range(0, creditCount).Select(index =>
            {
                var credit = account.ResetCreditDetails is { Count: > 0 } details && index < details.Count
                    ? details[index] : null;
                var expires = credit is null || !credit.ExpirationKnown ? "到期时间未提供"
                    : credit.ExpiresAt is null ? "无到期限制"
                    : AccountUsageHelpers.ExactTime(credit.ExpiresAt);
                return $"第 {index + 1} 张重置卡：{expires}";
            }));
        CardBackground = active ? ActiveBackground : NormalBackground;
        CardBorder = active ? ActiveBorder : NormalBorder;
        HealthBrush = AccountUsageHelpers.Health(account, warningThreshold, dangerThreshold) switch
        {
            AccountHealthStatus.Healthy => HealthyBrush,
            AccountHealthStatus.Warning => WarningBrush,
            AccountHealthStatus.Exhausted or AccountHealthStatus.Suspended or AccountHealthStatus.TokenExpired => DangerBrush,
            _ => UnknownBrush
        };
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
