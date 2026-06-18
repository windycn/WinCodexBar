using CodexBarWin.Interop;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.UI;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexBarWin.Tray;

public sealed class CodexBarTrayContext : ApplicationContext
{
    private static readonly System.Collections.Generic.Dictionary<string, Image> TrayIconCache = new();
    private const int TrayMenuWidth = 480;
    private const int TrayVisibleAccountLimit = 8;
    private static readonly Font TrayFont = new("Microsoft YaHei UI", 9f, FontStyle.Regular);
    private static readonly Font TraySmallFont = new("Microsoft YaHei UI", 8.4f, FontStyle.Regular);
    private static readonly Font TrayTinyFont = new("Microsoft YaHei UI", 8f, FontStyle.Regular);
    private static readonly Font TrayTitleFont = new("Microsoft YaHei UI", 10.4f, FontStyle.Bold);
    private static readonly Font TrayMonoFont = new("Consolas", 8.2f, FontStyle.Regular);
    private static readonly Color TrayMenuBack = Color.FromArgb(248, 250, 252);
    private static readonly Color TrayCardBack = Color.FromArgb(255, 255, 255);
    private static readonly Color TraySoftBack = Color.FromArgb(243, 247, 252);
    private static readonly Color TrayActiveBack = Color.FromArgb(235, 246, 255);
    private static readonly Color TrayBorder = Color.FromArgb(216, 224, 235);
    private static readonly Color TrayText = Color.FromArgb(39, 49, 63);
    private static readonly Color TraySecondaryText = Color.FromArgb(96, 108, 123);
    private static readonly Color TrayMutedText = Color.FromArgb(137, 148, 162);
    private static readonly Color TrayAccent = Color.FromArgb(0, 113, 227);
    private static readonly TimeSpan PopupActiveRefreshStaleAfter = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BackgroundInactiveRefreshStaleAfter = TimeSpan.FromMinutes(15);
    private readonly NotifyIcon _notifyIcon;
    private readonly AccountRegistry _registry;
    private readonly CodexBarConfigStore _configStore;
    private readonly CodexSyncService _syncService;
    private readonly KeepAwakeService _keepAwakeService;
    private readonly OpenAIUsageService _usageService;
    private readonly OpenAIOAuthRefreshService _oauthRefreshService;
    private readonly OpenAIOAuthLoginService _oauthLoginService;
    private readonly UsageRefreshCoordinator _refreshCoordinator;
    private readonly OpenAIAccountGatewayService _openAIGatewayService;
    private readonly CodexRadarService _codexRadarService;
    private CodexBarDashboardForm? _dashboardForm;
    private readonly System.Windows.Forms.Timer _autoRefreshTimer;
    private TrayPopupForm? _popupForm;
    private SettingsForm? _settingsForm;
    private bool _activeRefreshRunning;
    private bool _backgroundRefreshRunning;

    public CodexBarTrayContext()
    {
        _registry = new AccountRegistry();
        _configStore = new CodexBarConfigStore();
        _configStore.Load();
        _registry.Load();
        StartupService.SetEnabled(_configStore.Config.StartWithWindows);

        _keepAwakeService = new KeepAwakeService(_configStore);
        _keepAwakeService.Load();
        _syncService = new CodexSyncService(_configStore);
        _usageService = new OpenAIUsageService();
        _oauthRefreshService = new OpenAIOAuthRefreshService();
        _oauthLoginService = new OpenAIOAuthLoginService();
        _refreshCoordinator = new UsageRefreshCoordinator(_usageService, _oauthRefreshService, _registry);
        _openAIGatewayService = new OpenAIAccountGatewayService(_registry, _configStore, _oauthRefreshService);
        _codexRadarService = new CodexRadarService();
        _notifyIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(ResolveTrayUsage()),
            Visible = true,
            Text = BuildTrayHoverText(),
            ContextMenuStrip = BuildMenu(),
        };

        _autoRefreshTimer = new System.Windows.Forms.Timer();
        _autoRefreshTimer.Tick += (_, _) => _ = RefreshUsageInBackgroundAsync();
        ApplyAutoRefreshTimer();

        ApplyGatewayMode();
        SyncActiveIfPossible();
        _ = RefreshUsageInBackgroundAsync(includeInactiveStale: true);
        _notifyIcon.MouseClick += OnTrayIconMouseClick;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keepAwakeService.ClearForProcessExit();
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Dispose();
            _popupForm?.Dispose();
            _settingsForm?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _openAIGatewayService.Dispose();
            _dashboardForm?.Dispose();
        }

        base.Dispose(disposing);
    }

    private CodexBarDashboardForm DashboardForm
    {
        get
        {
            if (_dashboardForm is null || _dashboardForm.IsDisposed)
            {
                _dashboardForm = new CodexBarDashboardForm(
                    _registry,
                    _configStore,
                    _refreshCoordinator,
                    ActivateAccountFromDashboard,
                    AddOpenAIAccount,
                    ImportAccountsFromDashboard,
                    ExportAccountsFromDashboard,
                    OpenSettings,
                    IsAnyKeepAwakeEnabled,
                    SetKeepAwakeEnabled
                );
            }

            return _dashboardForm;
        }
    }

    private void RefreshDashboardIfCreated()
    {
        if (_dashboardForm is { IsDisposed: false })
        {
            _dashboardForm.RefreshData();
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            ImageScalingSize = new Size(20, 20),
            Font = TrayFont,
            BackColor = TrayMenuBack,
            Padding = new Padding(6),
            Renderer = new TrayMenuRenderer(),
        };

        var active = string.IsNullOrWhiteSpace(_registry.ActiveAccountId)
            ? null
            : _registry.Accounts.FirstOrDefault(a => string.Equals(a.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal));

        var (primaryAvg, secondaryAvg, primaryMin, secondaryMin, primaryMax, secondaryMax) = CalculateUsageStats(_registry.Accounts);

        menu.Items.Add(CreateDisabledItem("WinCodexBar", "app"));
        menu.Items.Add(CreateDisabledItem(active is null ? "当前未激活账号" : $"当前：{TrimMiddle(BuildAccountLabel(active), 42)}", "star"));
        menu.Items.Add(CreateDisabledItem($"账号 {_registry.Accounts.Count} · 主池 {FormatShortUsage(primaryAvg)} · 次池 {FormatShortUsage(secondaryAvg)}", "chart"));
        menu.Items.Add(new ToolStripSeparator());

        if (_registry.Accounts.Count == 0)
        {
            menu.Items.Add(CreateDisabledItem("尚未导入 OpenAI 账号", "warning"));
        }
        else
        {
            var accountsToShow = _registry.Accounts
                .OrderBy(a => !string.Equals(a.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal))
                .ThenBy(a => string.IsNullOrWhiteSpace(a.Email) ? a.AccountId : a.Email, StringComparer.CurrentCultureIgnoreCase)
                .Take(TrayVisibleAccountLimit)
                .ToArray();

            foreach (var account in accountsToShow)
            {
                var isActive = string.Equals(_registry.ActiveAccountId, account.AccountId, StringComparison.Ordinal);
                var text = $"{(isActive ? "✓ " : "  ")}{TrimMiddle(BuildAccountLabel(account), 38)}    {FormatShortUsage(account.PrimaryUsedPercent)} / {FormatShortUsage(account.SecondaryUsedPercent)}";
                var item = CreateActionItem(text, isActive ? "healthy" : "app", () => ActivateAccount(account));
                item.Font = isActive ? new Font(TrayFont, FontStyle.Bold) : TrayFont;
                item.ToolTipText = BuildUsageTooltip(account);
                menu.Items.Add(item);
            }

            if (_registry.Accounts.Count > accountsToShow.Length)
            {
                menu.Items.Add(CreateDisabledItem($"还有 {_registry.Accounts.Count - accountsToShow.Length} 个账号，打开主页查看全部", "unknown"));
            }
        }

        menu.Items.Add(new ToolStripSeparator());

        var openHome = CreateActionItem("打开主页", "app", OpenDashboard);
        menu.Items.Add(openHome);

        var openStats = CreateActionItem("会话分析", "chart", OpenSessionAnalysis);
        menu.Items.Add(openStats);

        var keepAwake = CreateActionItem(
            IsAnyKeepAwakeEnabled() ? "关闭保持唤醒" : "开启保持唤醒",
            IsAnyKeepAwakeEnabled() ? "healthy" : "warning",
            () => SetKeepAwakeEnabled(!IsAnyKeepAwakeEnabled()));
        menu.Items.Add(keepAwake);

        var addItem = CreateActionItem("添加 OpenAI 账号", "add", AddOpenAIAccount);
        menu.Items.Add(addItem);

        var importItem = CreateActionItem("导入账号文件", "import", ImportAccountsFromDashboard);
        menu.Items.Add(importItem);

        var exportItem = CreateActionItem("导出 OpenAI 账号", "export", ExportAccountsFromDashboard);
        menu.Items.Add(exportItem);

        var openConfig = CreateActionItem("打开配置目录", "settings", OpenConfigFolder);
        menu.Items.Add(openConfig);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = CreateActionItem("退出", "unknown", ExitThread);
        menu.Items.Add(exitItem);

        menu.Opening += (_, _) =>
        {
            _notifyIcon.Icon = CreateTrayIcon(ResolveTrayUsage());
            var currentActive = string.IsNullOrWhiteSpace(_registry.ActiveAccountId)
                ? null
                : _registry.Accounts.FirstOrDefault(a => string.Equals(a.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal));
            _notifyIcon.Text = BuildTrayHoverText(currentActive ?? active);
            /*
            var activeLabel = currentActive ?? active;
            _notifyIcon.Text = _registry.ActiveAccountId is null
                ? "WinCodexBar"
                : $"WinCodexBar · {(activeLabel is null ? "未找到激活账号" : BuildAccountLabel(activeLabel))}";
            */
        };

        return menu;
    }

    private ToolStripMenuItem CreateDisabledItem(string text, string iconName)
    {
        return new ToolStripMenuItem(text)
        {
            Enabled = false,
            Padding = new Padding(6, 5, 6, 5),
        };
    }

    private ToolStripControlHost CreateHeaderHost(TokenAccount? active)
    {
        var panel = CreateTrayHostPanel(TrayMenuWidth, 82);
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new LinearGradientBrush(panel.ClientRectangle, Color.FromArgb(255, 255, 255), Color.FromArgb(239, 247, 255), LinearGradientMode.Horizontal);
            FillRoundedRectangle(e.Graphics, brush, new Rectangle(4, 4, panel.Width - 8, panel.Height - 8), 10);
            using var pen = new Pen(TrayBorder);
            DrawRoundedRectangle(e.Graphics, pen, new Rectangle(4, 4, panel.Width - 8, panel.Height - 8), 10);
        };

        var icon = new PictureBox
        {
            Image = LoadIcon("app", new Size(28, 28)),
            Size = new Size(30, 30),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(20, 24),
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(icon);

        panel.Controls.Add(CreateTrayLabel("codexbar", TrayTitleFont, TrayText, new Rectangle(62, 17, 170, 24), ContentAlignment.MiddleLeft));
        panel.Controls.Add(CreateTrayLabel("Windows 托盘控制台", TraySmallFont, TraySecondaryText, new Rectangle(62, 42, 190, 22), ContentAlignment.MiddleLeft));

        var activeText = active is null ? "未设置激活账号" : TrimMiddle(BuildAccountLabel(active), 30);
        var activeBadge = CreateBadge(activeText, TrayAccent, Color.FromArgb(232, 244, 255), new Size(210, 28));
        activeBadge.Location = new Point(panel.Width - activeBadge.Width - 20, 27);
        panel.Controls.Add(activeBadge);

        return CreateHost(panel, bottomMargin: 6);
    }

    private ToolStripControlHost CreateMetricsHost(double? primaryAvg, double? secondaryAvg, double? primaryMin, double? secondaryMin, double? primaryMax, double? secondaryMax)
    {
        var panel = CreateTrayHostPanel(TrayMenuWidth, 78);
        panel.Controls.Add(CreateMetricCard("账号", _registry.Accounts.Count.ToString(CultureInfo.InvariantCulture), "已导入", new Rectangle(4, 0, 116, 70), TrayAccent));
        panel.Controls.Add(CreateMetricCard("主池", FormatShortUsage(primaryAvg), FormatRange(primaryMin, primaryMax), new Rectangle(128, 0, 168, 70), UsageAccent(primaryAvg)));
        panel.Controls.Add(CreateMetricCard("次池", FormatShortUsage(secondaryAvg), FormatRange(secondaryMin, secondaryMax), new Rectangle(304, 0, 168, 70), UsageAccent(secondaryAvg)));
        return CreateHost(panel, bottomMargin: 2);
    }

    private ToolStripControlHost CreateSectionHost(string title, string subtitle)
    {
        var panel = CreateTrayHostPanel(TrayMenuWidth, 36);
        panel.Controls.Add(CreateTrayLabel(title, TrayTitleFont, TrayText, new Rectangle(8, 5, 132, 22), ContentAlignment.MiddleLeft));
        panel.Controls.Add(CreateTrayLabel(subtitle, TraySmallFont, TrayMutedText, new Rectangle(148, 6, panel.Width - 162, 20), ContentAlignment.MiddleRight));
        return CreateHost(panel, bottomMargin: 0);
    }

    private ToolStripControlHost CreateEmptyAccountsHost()
    {
        var panel = CreateTrayHostPanel(TrayMenuWidth, 92);
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(TraySoftBack);
            FillRoundedRectangle(e.Graphics, brush, new Rectangle(4, 6, panel.Width - 8, panel.Height - 12), 8);
            using var pen = new Pen(TrayBorder);
            DrawRoundedRectangle(e.Graphics, pen, new Rectangle(4, 6, panel.Width - 8, panel.Height - 12), 8);
        };

        var icon = new PictureBox
        {
            Image = LoadIcon("import", new Size(26, 26)),
            Size = new Size(30, 30),
            Location = new Point(18, 28),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(icon);
        panel.Controls.Add(CreateTrayLabel("尚未导入账号", TrayTitleFont, TrayText, new Rectangle(58, 22, 220, 24), ContentAlignment.MiddleLeft));
        panel.Controls.Add(CreateTrayLabel("支持 mac 版导出的 JSON / CSV", TraySmallFont, TraySecondaryText, new Rectangle(58, 45, 250, 22), ContentAlignment.MiddleLeft));

        AttachClickRecursive(panel, ImportAccountsFromDashboard);
        return CreateHost(panel, bottomMargin: 6);
    }

    private ToolStripControlHost CreateMoreAccountsHost(int count)
    {
        var panel = CreateTrayHostPanel(TrayMenuWidth, 34);
        panel.Controls.Add(CreateTrayLabel($"还有 {count} 个账号未在托盘菜单展示，打开主页可查看全部", TraySmallFont, TrayMutedText, new Rectangle(8, 4, panel.Width - 16, 22), ContentAlignment.MiddleCenter));
        AttachClickRecursive(panel, OpenDashboard);
        return CreateHost(panel, bottomMargin: 4);
    }

    private ToolStripControlHost CreateAccountRowHost(TokenAccount account, bool isActive, ContextMenuStrip menu)
    {
        var row = CreateTrayHostPanel(TrayMenuWidth, 72);
        var hover = false;
        row.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = isActive
                ? TrayActiveBack
                : hover ? Color.FromArgb(244, 248, 253) : TrayCardBack;
            using var brush = new SolidBrush(fill);
            FillRoundedRectangle(e.Graphics, brush, new Rectangle(4, 4, row.Width - 8, row.Height - 8), 8);
            using var pen = new Pen(isActive ? Color.FromArgb(132, 194, 255) : TrayBorder);
            DrawRoundedRectangle(e.Graphics, pen, new Rectangle(4, 4, row.Width - 8, row.Height - 8), 8);

            if (isActive)
            {
                using var accent = new SolidBrush(TrayAccent);
                FillRoundedRectangle(e.Graphics, accent, new Rectangle(4, 14, 3, row.Height - 28), 2);
            }
        };

        row.MouseEnter += (_, _) => { hover = true; row.Invalidate(); };
        row.MouseLeave += (_, _) => { hover = false; row.Invalidate(); };

        var statusDot = CreateStatusDot(ResolveUsageColor(account.PrimaryUsedPercent, account.SecondaryUsedPercent));
        statusDot.Location = new Point(20, 18);
        row.Controls.Add(statusDot);

        row.Controls.Add(CreateTrayLabel(TrimMiddle(BuildAccountLabel(account), 34), TrayFont, TrayText, new Rectangle(40, 10, 250, 24), ContentAlignment.MiddleLeft));

        var plan = string.IsNullOrWhiteSpace(account.PlanType) ? "free" : account.PlanType;
        var planBadge = CreateBadge(plan, PlanColor(plan), Color.FromArgb(245, 247, 250), new Size(64, 24));
        planBadge.Location = new Point(300, 10);
        row.Controls.Add(planBadge);

        if (isActive)
        {
            var activeBadge = CreateBadge("激活", TrayAccent, Color.FromArgb(224, 241, 255), new Size(54, 24));
            activeBadge.Location = new Point(374, 10);
            row.Controls.Add(activeBadge);
        }

        row.Controls.Add(CreateTrayLabel(AccountExpiryText(account), TrayTinyFont, TrayMutedText, new Rectangle(40, 38, 166, 20), ContentAlignment.MiddleLeft));
        row.Controls.Add(CreateUsageChip("P", account.PrimaryUsedPercent, new Point(214, 40)));
        row.Controls.Add(CreateUsageChip("S", account.SecondaryUsedPercent, new Point(344, 40)));

        AttachClickRecursive(row, () =>
        {
            menu.Close(ToolStripDropDownCloseReason.ItemClicked);
            ActivateAccount(account);
        });
        return CreateHost(row, bottomMargin: 4);
    }

    private ToolStripMenuItem CreateActionItem(string text, string iconName, Action onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            Padding = new Padding(6, 6, 6, 6),
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private string BuildAccountSectionSubtitle()
    {
        if (_registry.Accounts.Count == 0)
        {
            return "准备导入账号";
        }

        var active = string.IsNullOrWhiteSpace(_registry.ActiveAccountId) ? 0 : 1;
        var risky = _registry.Accounts.Count(a =>
            Math.Max(
                HasUsageValue(a.PrimaryUsedPercent) ? ClampUsage(a.PrimaryUsedPercent) : 0,
                HasUsageValue(a.SecondaryUsedPercent) ? ClampUsage(a.SecondaryUsedPercent) : 0) >= 90);
        return $"激活 {active} · 高负载 {risky} · 共 {_registry.Accounts.Count}";
    }

    private static Panel CreateTrayHostPanel(int width, int height)
    {
        return new Panel
        {
            Width = width,
            Height = height,
            BackColor = TrayMenuBack,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
    }

    private static ToolStripControlHost CreateHost(Control control, int bottomMargin)
    {
        return new ToolStripControlHost(control)
        {
            AutoSize = false,
            Size = control.Size,
            Margin = new Padding(0, 0, 0, bottomMargin),
            Padding = Padding.Empty,
        };
    }

    private static Label CreateTrayLabel(string text, Font font, Color color, Rectangle bounds, ContentAlignment align)
    {
        return new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize = false,
            Bounds = bounds,
            TextAlign = align,
            UseMnemonic = false,
            AutoEllipsis = true,
        };
    }

    private static Control CreateMetricCard(string title, string value, string detail, Rectangle bounds, Color accent)
    {
        var panel = new Panel
        {
            Bounds = bounds,
            BackColor = Color.Transparent,
        };
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(TrayCardBack);
            FillRoundedRectangle(e.Graphics, brush, new Rectangle(0, 4, panel.Width - 1, panel.Height - 8), 8);
            using var pen = new Pen(TrayBorder);
            DrawRoundedRectangle(e.Graphics, pen, new Rectangle(0, 4, panel.Width - 1, panel.Height - 8), 8);
            using var accentBrush = new SolidBrush(accent);
            FillRoundedRectangle(e.Graphics, accentBrush, new Rectangle(8, 12, 4, panel.Height - 24), 2);
        };
        panel.Controls.Add(CreateTrayLabel(title, TrayTinyFont, TrayMutedText, new Rectangle(18, 10, panel.Width - 28, 16), ContentAlignment.MiddleLeft));
        panel.Controls.Add(CreateTrayLabel(value, TrayTitleFont, TrayText, new Rectangle(18, 25, panel.Width - 28, 22), ContentAlignment.MiddleLeft));
        panel.Controls.Add(CreateTrayLabel(detail, TrayTinyFont, TraySecondaryText, new Rectangle(18, 45, panel.Width - 28, 16), ContentAlignment.MiddleLeft));
        return panel;
    }

    private static Label CreateBadge(string text, Color foreColor, Color backColor, Size size)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Size = size,
            Font = TrayTinyFont,
            ForeColor = foreColor,
            BackColor = backColor,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false,
            AutoEllipsis = true,
        };
    }

    private static Control CreateStatusDot(Color color)
    {
        var dot = new Panel
        {
            Size = new Size(10, 10),
            BackColor = Color.Transparent,
        };
        dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            e.Graphics.FillEllipse(brush, 1, 1, 8, 8);
        };
        return dot;
    }

    private static Control CreateUsageChip(string label, double value, Point location)
    {
        var panel = new Panel
        {
            Size = new Size(112, 20),
            Location = location,
            BackColor = Color.Transparent,
        };

        var hasValue = HasUsageValue(value);
        var clamped = hasValue ? ClampUsage(value) : 0;
        var accent = hasValue ? ResolveUsageColor(value) : TrayMutedText;

        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(239, 243, 248));
            FillRoundedRectangle(e.Graphics, back, new Rectangle(0, 3, panel.Width - 1, 13), 6);
            if (hasValue)
            {
                using var fill = new SolidBrush(Color.FromArgb(70, accent));
                var width = Math.Clamp((int)Math.Round((panel.Width - 44) * clamped / 100d), 0, panel.Width - 44);
                FillRoundedRectangle(e.Graphics, fill, new Rectangle(24, 6, width, 7), 4);
            }
        };

        panel.Controls.Add(CreateTrayLabel(label, TrayTinyFont, TraySecondaryText, new Rectangle(4, 0, 18, 18), ContentAlignment.MiddleLeft));
        panel.Controls.Add(CreateTrayLabel(hasValue ? $"{clamped:F0}%" : "--", TrayMonoFont, accent, new Rectangle(panel.Width - 40, 0, 36, 18), ContentAlignment.MiddleRight));
        return panel;
    }

    private static void AttachClickRecursive(Control control, Action action)
    {
        control.Cursor = Cursors.Hand;
        control.Click += (_, _) => action();
        foreach (Control child in control.Controls)
        {
            AttachClickRecursive(child, action);
        }
    }

    private static string TrimMiddle(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        var left = Math.Max(1, (maxLength - 1) / 2);
        var right = Math.Max(1, maxLength - left - 1);
        return $"{text[..left]}…{text[^right..]}";
    }

    private static string FormatShortUsage(double? value)
    {
        return value is null ? "--" : $"{ClampUsage(value.Value):F0}%";
    }

    private static string FormatRange(double? min, double? max)
    {
        return min is null || max is null
            ? "暂无用量"
            : $"{ClampUsage(min.Value):F0}-{ClampUsage(max.Value):F0}%";
    }

    private static string AccountExpiryText(TokenAccount account)
    {
        return account.ExpiresAt is null
            ? "到期时间未知"
            : $"到期 {account.ExpiresAt.Value.ToLocalTime():MM-dd HH:mm}";
    }

    private static Color PlanColor(string plan)
    {
        return plan.ToLowerInvariant() switch
        {
            "team" => Color.FromArgb(0, 113, 227),
            "plus" => Color.FromArgb(120, 78, 220),
            "pro" or "prolite" => Color.FromArgb(0, 150, 105),
            _ => Color.FromArgb(105, 116, 130),
        };
    }

    private static Color UsageAccent(double? usage)
    {
        return usage is null ? TrayMutedText : ResolveUsageColor(usage.Value);
    }

    private static GraphicsPath RoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, Rectangle rect, int radius)
    {
        using var path = RoundedRectanglePath(rect, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(Graphics graphics, Pen pen, Rectangle rect, int radius)
    {
        using var path = RoundedRectanglePath(rect, radius);
        graphics.DrawPath(pen, path);
    }

    private static Image? LoadIcon(string name, Size size)
    {
        var key = $"{name}_{size.Width}_{size.Height}";
        if (TrayIconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", $"{name}.png");
        if (!File.Exists(path))
        {
            return null;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var source = Image.FromStream(fs);
        var icon = source.Width == size.Width && source.Height == size.Height
            ? new Bitmap(source)
            : new Bitmap(source, size);
        TrayIconCache[key] = icon;
        return icon;
    }

    private void ImportAccounts()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入 OpenAI 账号",
            Filter = "OpenAI 账号文件 (*.json;*.csv)|*.json;*.csv|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            var content = ReadTextSafely(dialog.FileName);
            var parsed = OpenAIAccountCSVService.Parse(content);

            _registry.MergeImportedAccounts(parsed.Accounts, parsed.InteropContext);

            if (string.IsNullOrWhiteSpace(_registry.ActiveAccountId) || string.IsNullOrWhiteSpace(parsed.ActiveAccountId) == false)
            {
                _registry.SetActive(parsed.ActiveAccountId);
            }

            _registry.Save();
            SyncActiveIfPossible();
            RebuildMenu();

            MessageBox.Show($"导入成功：{parsed.RowCount} 条", "WinCodexBar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OpenAIAccountImportException ex)
        {
            MessageBox.Show($"导入失败: {ex.Message}", "WinCodexBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show($"导入失败: 文件名无效或路径不可访问 ({ex.Message})", "WinCodexBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"导入失败: 文件读取异常 ({ex.Message})", "WinCodexBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败: {ex.Message}", "WinCodexBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string ReadTextSafely(string path)
    {
        try
        {
            return File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (DecoderFallbackException)
        {
            using var reader = new StreamReader(path, Encoding.Default, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }

    private void ImportAccountsFromDashboard()
    {
        ImportAccounts();
    }

    private async void AddOpenAIAccount()
    {
        using var form = new OpenAIOAuthLoginForm(_oauthLoginService);
        IWin32Window? owner = _settingsForm is { IsDisposed: false, Visible: true }
            ? _settingsForm
            : _dashboardForm is { IsDisposed: false, Visible: true } dashboard ? dashboard : null;

        var result = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
        if (result != DialogResult.OK || form.CompletedAccount is null)
        {
            return;
        }

        var account = form.CompletedAccount;
        _registry.UpsertAccount(account, activate: true);
        _registry.Save();
        SyncActiveIfPossible();
        RefreshShellOnly(refreshPopup: true);
        RebuildMenu();

        _notifyIcon.ShowBalloonTip(1200, "账号已添加", BuildAccountLabel(account), ToolTipIcon.Info);

        try
        {
            var outcome = await Task.Run(() => _refreshCoordinator.RefreshSingleAsync(account)).ConfigureAwait(true);
            if (outcome != UsageRefreshOutcome.Failed)
            {
                _registry.Save();
            }
        }
        catch
        {
            // 添加账号已完成，用量刷新失败时保留账号，后续可手动刷新。
        }

        RefreshShellOnly(refreshPopup: true);
        RefreshDashboardIfCreated();
    }

    private void ExportAccounts()
    {
        if (_registry.Accounts.Count == 0)
        {
            MessageBox.Show("没有可导出的账号", "WinCodexBar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "导出 OpenAI 账号",
            Filter = "OpenAI 账号文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = "openai_accounts_export.json",
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            var saveDir = Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrWhiteSpace(saveDir) == false && Directory.Exists(saveDir) == false)
            {
                Directory.CreateDirectory(saveDir);
            }

            var text = OpenAIAccountCSVService.ExportInteropBundle(
                _registry.Accounts,
                _registry.MetadataByAccountId,
                _registry.ProxiesJSON,
                _registry.ActiveAccountId
            );

            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            MessageBox.Show("导出完成", "WinCodexBar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "WinCodexBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportAccountsFromDashboard()
    {
        ExportAccounts();
        RefreshDashboardIfCreated();
    }

    private void OpenConfigFolder()
    {
        Process.Start("explorer", $"\"{CodexPaths.CodexBarRoot}\"");
    }

    private void OpenSettings()
    {
        if (_popupForm is { IsDisposed: false, Visible: true })
        {
            _popupForm.Close();
            _popupForm = null;
        }

        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(
                _configStore,
                _keepAwakeService,
                OpenConfigFolder,
                OnSettingsChanged);
        }

        _settingsForm.StartPosition = FormStartPosition.CenterScreen;
        _settingsForm.WindowState = FormWindowState.Normal;
        _settingsForm.Show();
        _settingsForm.BringToFront();
        _settingsForm.Activate();
    }

    private void OnSettingsChanged()
    {
        ApplyAutoRefreshTimer();
        RebuildMenu();
        RefreshDashboardIfCreated();
    }

    private void ApplyAutoRefreshTimer()
    {
        var settings = _configStore.Config.OpenAI;
        if (!settings.AutoRefreshEnabled || settings.AutoRefreshIntervalSeconds <= 0)
        {
            _autoRefreshTimer.Stop();
            return;
        }

        var interval = Math.Max(60, settings.AutoRefreshIntervalSeconds);
        _autoRefreshTimer.Stop();
        _autoRefreshTimer.Interval = interval * 1000;
        _autoRefreshTimer.Start();
    }

    private void OpenDashboard()
    {
        if (_popupForm is { IsDisposed: false, Visible: true })
        {
            _popupForm.Close();
            _popupForm = null;
        }

        var dashboard = DashboardForm;
        dashboard.RefreshData();
        dashboard.StartPosition = FormStartPosition.CenterScreen;
        dashboard.WindowState = FormWindowState.Normal;
        dashboard.Show();
        dashboard.BringToFront();
        dashboard.Activate();
    }

    private void OpenSessionAnalysis()
    {
        if (_popupForm is { IsDisposed: false, Visible: true })
        {
            _popupForm.Close();
            _popupForm = null;
        }

        var dashboard = DashboardForm;
        dashboard.RefreshData();
        dashboard.StartPosition = FormStartPosition.CenterScreen;
        dashboard.ShowSessionAnalysis();
        dashboard.BringToFront();
        dashboard.Activate();
    }

    private async void ActivateAccount(TokenAccount account)
    {
        _registry.SetActive(account.AccountId);
        SyncActiveIfPossible();
        _registry.Save();
        RefreshShellOnly(refreshPopup: true);
        RefreshDashboardIfCreated();
        NotifyAccountSwitchEffect(account);

        try
        {
            var outcome = await Task.Run(() => _refreshCoordinator.RefreshSingleAsync(account)).ConfigureAwait(true);
            if (outcome != UsageRefreshOutcome.Failed)
            {
                _registry.Save();
            }
        }
        catch
        {
            // 切换账号不因用量刷新失败而中断。
        }

        RefreshShellOnly(refreshPopup: true);
        RefreshDashboardIfCreated();
    }

    private void ActivateAccountFromDashboard(TokenAccount account)
    {
        ActivateAccount(account);
        _notifyIcon.ShowBalloonTip(1200, "切换成功", $"已切换账号：{BuildAccountLabel(account)}", ToolTipIcon.Info);
    }

    private void NotifyAccountSwitchEffect(TokenAccount account)
    {
        var label = BuildAccountLabel(account);
        if (_configStore.Config.OpenAI.AccountUsageMode == AccountUsageMode.AggregateGateway)
        {
            _notifyIcon.ShowBalloonTip(
                1800,
                "聚合模式已切换",
                $"当前目标：{label}。新的请求会经由本地网关按余量与会话粘性路由，正在运行的响应流不会中途切换。",
                ToolTipIcon.Info);
            return;
        }

        _notifyIcon.ShowBalloonTip(
            2200,
            "已切换账号，需要重启 Codex",
            $"已写入当前账号：{label}。已打开的 Codex 通常需要重启或新开实例后才会读取新的登录态。",
            ToolTipIcon.Warning);
    }

    private void DeleteAccountFromPopup(TokenAccount account)
    {
        var label = BuildAccountLabel(account);
        using var dialog = new ConfirmActionDialog(
            "删除账号",
            $"确定删除“{label}”吗？\n此操作只移除 WinCodexBar 保存的账号，不会删除 Codex 会话历史。",
            "删除");

        IWin32Window? owner = _popupForm is { IsDisposed: false, Visible: true } ? _popupForm : null;
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (result != DialogResult.OK)
        {
            RefreshShellOnly(refreshPopup: true);
            return;
        }

        var wasActive = string.Equals(_registry.ActiveAccountId, account.AccountId, StringComparison.Ordinal);
        if (!_registry.RemoveAccount(account.AccountId))
        {
            RefreshShellOnly(refreshPopup: true);
            return;
        }

        _registry.Save();
        if (wasActive)
        {
            SyncActiveIfPossible();
        }

        RefreshShellOnly(refreshPopup: true);
        RebuildMenu();
        _notifyIcon.ShowBalloonTip(1200, "账号已删除", label, ToolTipIcon.Info);
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_popupForm is { IsDisposed: false, Visible: true })
        {
            _popupForm.Close();
            _popupForm = null;
            return;
        }

        _popupForm?.Dispose();
        var tokenSummary = TokenUsageScanService.Scan();
        _popupForm = new TrayPopupForm(
            _registry.Accounts,
            _registry.ActiveAccountId,
            IsAnyKeepAwakeEnabled,
            tokenSummary,
            _codexRadarService.LastPrediction,
            _configStore.Config,
            ActivateAccount,
            OpenDashboard,
            OpenSessionAnalysis,
            AddOpenAIAccount,
            ImportAccountsFromDashboard,
            ExportAccountsFromDashboard,
            OpenSettings,
            SetAccountUsageMode,
            () => SetKeepAwakeEnabled(!IsAnyKeepAwakeEnabled()),
            RefreshSingleAccountFromPopup,
            DeleteAccountFromPopup,
            ExitThread);
        _popupForm.FormClosed += (_, _) => _popupForm = null;
        _popupForm.ShowNearCursor();
        _ = RefreshActiveAccountForPopupAsync();
        _ = RefreshCodexRadarForPopupAsync();
    }

    private async Task RefreshCodexRadarForPopupAsync()
    {
        try
        {
            var prediction = await _codexRadarService.GetCurrentAsync(forceRefresh: true).ConfigureAwait(true);
            if (_popupForm is { IsDisposed: false, Visible: true })
            {
                _popupForm.UpdateRadarPrediction(prediction);
            }
        }
        catch
        {
            // Radar 只是辅助提示，失败时不显示，不影响托盘菜单。
        }
    }

    private void SetKeepAwakeEnabled(bool enabled)
    {
        _keepAwakeService.SetEnabled(enabled);
        if (!enabled && _keepAwakeService.IsAdvancedEnabled)
        {
            _keepAwakeService.SetAdvancedEnabled(false);
        }

        RebuildMenu();
        _notifyIcon.ShowBalloonTip(
            1200,
            enabled ? "保持唤醒已开启" : "保持唤醒已关闭",
            enabled ? "系统将避免睡眠与息屏，适合长时间 vibing coding。" : "已恢复 Windows 默认电源策略。",
            ToolTipIcon.Info);
    }

    private bool IsAnyKeepAwakeEnabled()
    {
        return _keepAwakeService.IsEnabled || _keepAwakeService.IsAdvancedEnabled;
    }

    private void SetAccountUsageMode(AccountUsageMode mode)
    {
        _configStore.Update(config => config.OpenAI.AccountUsageMode = mode);
        ApplyGatewayMode();
        SyncActiveIfPossible();
        RefreshShellOnly(refreshPopup: true);
        RebuildMenu();
        RefreshDashboardIfCreated();
        NotifyAccountUsageModeChangedAccurately(mode);
    }

    private void NotifyAccountUsageModeChangedAccurately(AccountUsageMode mode)
    {
        if (mode == AccountUsageMode.AggregateGateway)
        {
            _notifyIcon.ShowBalloonTip(
                3200,
                "已切换到聚合模式",
                $"本地网关已启动：{OpenAIAccountGatewayService.BaseUrl}。如果 Codex 是切换前已打开的实例，通常需要重启 Codex 或新开实例后才会接入聚合路由。",
                ToolTipIcon.Info);
            return;
        }

        _notifyIcon.ShowBalloonTip(
            2200,
            "已切换到手动模式",
            "手动模式会写入当前账号配置；已经运行的 Codex 实例通常需要重启后才会使用新账号。",
            ToolTipIcon.Info);
    }

    private void NotifyAccountUsageModeChanged(AccountUsageMode mode)
    {
        if (mode == AccountUsageMode.AggregateGateway)
        {
            _notifyIcon.ShowBalloonTip(
                2600,
                "已切换到聚合模式",
                $"本地网关已启动：{OpenAIAccountGatewayService.BaseUrl}。让 Codex 使用这个地址后，可在不重启 Codex 的情况下按会话与额度自动路由。",
                ToolTipIcon.Info);
            return;
        }

        _notifyIcon.ShowBalloonTip(
            2200,
            "已切换到手动模式",
            "手动模式会写入当前账号配置；已运行的 Codex 实例通常需要重启后才会使用新账号。",
            ToolTipIcon.Info);
    }

    private void ApplyGatewayMode()
    {
        if (_configStore.Config.OpenAI.AccountUsageMode == AccountUsageMode.AggregateGateway)
        {
            _openAIGatewayService.EnsureStarted();
            return;
        }

        _openAIGatewayService.Stop();
    }

    private void SyncActiveIfPossible()
    {
        if (string.IsNullOrWhiteSpace(_registry.ActiveAccountId))
        {
            return;
        }

        var account = _registry.Accounts.FirstOrDefault(a => string.Equals(a.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal));
        if (account is null)
        {
            return;
        }

        try
        {
            if (_configStore.Config.OpenAI.AccountUsageMode == AccountUsageMode.AggregateGateway)
            {
                _openAIGatewayService.EnsureStarted();
            }

            _syncService.SyncForCurrentMode(account);
        }
        catch
        {
            // 同步失败时静默，不中断托盘运行；下次可重试。
        }
    }

    private void RebuildMenu()
    {
        var old = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = BuildMenu();
        old?.Dispose();

        _notifyIcon.Text = BuildTrayHoverText();
        /*
        _notifyIcon.Text = _registry.ActiveAccountId is null
            ? "WinCodexBar"
            : "WinCodexBar · 已激活账号";
        */
        _notifyIcon.Icon = CreateTrayIcon(ResolveTrayUsage());

        RefreshDashboardIfCreated();
    }

    private void RefreshShellOnly(bool refreshPopup = false)
    {
        _notifyIcon.Text = BuildTrayHoverText();
        _notifyIcon.Icon = CreateTrayIcon(ResolveTrayUsage());
        if (refreshPopup && _popupForm is { IsDisposed: false, Visible: true })
        {
            _popupForm.UpdateSnapshot(_registry.ActiveAccountId, null, _configStore.Config, _registry.Accounts);
        }
    }

    private TokenAccount? ActiveAccount()
    {
        return string.IsNullOrWhiteSpace(_registry.ActiveAccountId)
            ? null
            : _registry.Accounts.FirstOrDefault(a => string.Equals(a.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal));
    }

    private async Task RefreshActiveAccountForPopupAsync()
    {
        var account = ActiveAccount();
        if (account is null || _activeRefreshRunning || !ShouldRefreshAccount(account, PopupActiveRefreshStaleAfter))
        {
            return;
        }

        _activeRefreshRunning = true;
        try
        {
            var outcome = await Task.Run(() => _refreshCoordinator.RefreshSingleAsync(account)).ConfigureAwait(true);
            if (outcome != UsageRefreshOutcome.Failed)
            {
                _registry.Save();
            }

            RefreshShellOnly(refreshPopup: true);
            RefreshDashboardIfCreated();
        }
        catch
        {
            // 打开菜单时的即时刷新失败不打断菜单交互。
        }
        finally
        {
            _activeRefreshRunning = false;
        }
    }

    private async void RefreshSingleAccountFromPopup(TokenAccount account)
    {
        try
        {
            RefreshShellOnly(refreshPopup: true);
            var outcome = await Task.Run(() => _refreshCoordinator.RefreshSingleAsync(account)).ConfigureAwait(true);
            if (outcome != UsageRefreshOutcome.Failed)
            {
                _registry.Save();
            }

            RefreshShellOnly(refreshPopup: true);
            RefreshDashboardIfCreated();
        }
        catch
        {
            _notifyIcon.ShowBalloonTip(1200, "WinCodexBar", "刷新此账号用量失败", ToolTipIcon.Warning);
        }
    }

    private string BuildTrayHoverText(TokenAccount? active = null)
    {
        active ??= string.IsNullOrWhiteSpace(_registry.ActiveAccountId)
            ? null
            : _registry.Accounts.FirstOrDefault(a => string.Equals(a.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal));

        if (active is null)
        {
            return "WinCodexBar · 未激活账号";
        }

        var mode = _configStore.Config.OpenAI.UsageDisplayMode;
        var label = mode == UsageDisplayMode.Remaining ? "剩余" : "已用";
        var text = $"{label} · 5h {AccountUsageHelpers.FormatDisplayPercent(active.PrimaryUsedPercent, mode)} · 7d {AccountUsageHelpers.FormatDisplayPercent(active.SecondaryUsedPercent, mode)}";
        return text.Length <= 63 ? text : text[..63];
    }

    private async Task RefreshUsageInBackgroundAsync(bool includeInactiveStale = true)
    {
        if (_registry.Accounts.Count == 0 || _backgroundRefreshRunning)
        {
            return;
        }

        var accounts = BuildBackgroundRefreshAccounts(includeInactiveStale);
        if (accounts.Length == 0)
        {
            return;
        }

        UsageRefreshReport report;
        _backgroundRefreshRunning = true;
        try
        {
            report = await _refreshCoordinator.RefreshAsync(accounts, maxParallel: Math.Min(2, accounts.Length)).ConfigureAwait(true);
        }
        catch
        {
            // 用量刷新失败不影响托盘切换能力。
            return;
        }
        finally
        {
            _backgroundRefreshRunning = false;
        }

        if (!report.AnyChanged)
        {
            _notifyIcon.Icon = CreateTrayIcon(ResolveTrayUsage());
            _notifyIcon.Text = BuildTrayHoverText();
            RefreshDashboardIfCreated();
            return;
        }

        _notifyIcon.Icon = CreateTrayIcon(ResolveTrayUsage());
        _notifyIcon.Text = BuildTrayHoverText();
        RefreshDashboardIfCreated();
        RebuildMenu();
    }

    private TokenAccount[] BuildBackgroundRefreshAccounts(bool includeInactiveStale)
    {
        var active = ActiveAccount();
        var accounts = _registry.Accounts
            .Where(account =>
            {
                if (active is not null && string.Equals(account.AccountId, active.AccountId, StringComparison.Ordinal))
                {
                    return ShouldRefreshAccount(account, TimeSpan.FromSeconds(Math.Max(60, _configStore.Config.OpenAI.AutoRefreshIntervalSeconds)));
                }

                return includeInactiveStale && ShouldRefreshAccount(account, BackgroundInactiveRefreshStaleAfter);
            })
            .ToArray();

        return accounts;
    }

    private static bool ShouldRefreshAccount(TokenAccount account, TimeSpan staleAfter)
    {
        if (account.LastChecked is null)
        {
            return true;
        }

        return DateTimeOffset.UtcNow - account.LastChecked.Value.ToUniversalTime() >= staleAfter;
    }

    private (double? primary, double? secondary, bool hasActive) ResolveTrayUsage()
    {
        var active = string.IsNullOrWhiteSpace(_registry.ActiveAccountId)
            ? null
            : _registry.Accounts.FirstOrDefault(a => string.Equals(a.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal));

        if (active is not null)
        {
            return (active.PrimaryUsedPercent, active.SecondaryUsedPercent, true);
        }

        var (primaryAvg, secondaryAvg, _, _, _, _) = CalculateUsageStats(_registry.Accounts);
        return (primaryAvg, secondaryAvg, false);
    }

    private static string BuildAccountLabel(TokenAccount account)
    {
        var display = string.IsNullOrWhiteSpace(account.Email)
            ? account.AccountId
            : account.Email;

        if (string.IsNullOrWhiteSpace(display))
        {
            display = "未命名账号";
        }

        return display;
    }

    private static string BuildUsageText(TokenAccount account)
    {
        var primary = NormalizeUsagePercent(account.PrimaryUsedPercent, "P");
        var secondary = NormalizeUsagePercent(account.SecondaryUsedPercent, "S");

        if (primary.hasValue == false && secondary.hasValue == false)
        {
            return "用量: 未统计";
        }

        return $"P:{BuildUsageBar(primary.percent)} {primary.valueText} | S:{BuildUsageBar(secondary.percent)} {secondary.valueText}";
    }

    private static string BuildUsageBar(double? value, int width = 8)
    {
        if (value is null)
        {
            return "[--------]";
        }

        var v = ClampUsage(value.Value);
        var filled = (int)Math.Round(v / 100d * width);
        var rest = Math.Max(0, width - filled);
        return $"[{new string('|', filled)}{new string('-', rest)}]";
    }

    private static (double? percent, string label, string valueText, bool hasValue) NormalizeUsagePercent(double percent, string label)
    {
        if (HasUsageValue(percent) == false)
        {
            return (null, label, "--", false);
        }

        var clamped = ClampUsage(percent);
        return (clamped, label, clamped.ToString("F1", CultureInfo.InvariantCulture) + "%", true);
    }

    private static string FormatUsageValue(double? avg, double? min, double? max)
    {
        if (avg is null)
        {
            return "--% (范围: --% ~ --%)";
        }

        var avgText = avg.Value.ToString("F1", CultureInfo.InvariantCulture);
        var minText = min?.ToString("F1", CultureInfo.InvariantCulture) ?? "--";
        var maxText = max?.ToString("F1", CultureInfo.InvariantCulture) ?? "--";
        return $"{avgText}% (范围 {minText}% ~ {maxText}%)";
    }

    private static string BuildAccountTooltip(TokenAccount account)
    {
        var usedAt = account.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "未知";
        var lines = new[]
        {
            $"账号ID: {account.AccountId}",
            $"OpenAI ID: {account.OpenAIAccountId}",
            $"计划: {account.PlanType}",
            $"邮箱: {account.Email}",
            $"主池: {(HasUsageValue(account.PrimaryUsedPercent) ? ClampUsage(account.PrimaryUsedPercent).ToString("F1", CultureInfo.InvariantCulture) + "%" : "--")}",
            $"次池: {(HasUsageValue(account.SecondaryUsedPercent) ? ClampUsage(account.SecondaryUsedPercent).ToString("F1", CultureInfo.InvariantCulture) + "%" : "--")}",
            $"过期: {usedAt}",
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static (double? avgPrimary, double? avgSecondary, double? minPrimary, double? minSecondary, double? maxPrimary, double? maxSecondary) CalculateUsageStats(IReadOnlyList<TokenAccount> accounts)
    {
        var primaryValues = accounts
            .Select(a => a.PrimaryUsedPercent)
            .Where(HasUsageValue)
            .Select(ClampUsage)
            .ToArray();
        var secondaryValues = accounts
            .Select(a => a.SecondaryUsedPercent)
            .Where(HasUsageValue)
            .Select(ClampUsage)
            .ToArray();

        double? avgPrimary = primaryValues.Length == 0 ? null : primaryValues.Average();
        double? avgSecondary = secondaryValues.Length == 0 ? null : secondaryValues.Average();
        double? minPrimary = primaryValues.Length == 0 ? null : primaryValues.Min();
        double? minSecondary = secondaryValues.Length == 0 ? null : secondaryValues.Min();
        double? maxPrimary = primaryValues.Length == 0 ? null : primaryValues.Max();
        double? maxSecondary = secondaryValues.Length == 0 ? null : secondaryValues.Max();

        return (avgPrimary, avgSecondary, minPrimary, minSecondary, maxPrimary, maxSecondary);
    }

    private static Image CreateAccountIcon(bool isActive, double primaryUsed, double secondaryUsed)
    {
        var statusColor = ResolveUsageColor(primaryUsed, secondaryUsed);
        var baseColor = isActive ? Color.FromArgb(0, 120, 212) : Color.FromArgb(120, 120, 120);
        return GenerateDotIcon(baseColor, statusColor, isActive);
    }

    private static Color ResolveUsageColor(double primaryUsed, double secondaryUsed)
    {
        var max = Math.Max(
            HasUsageValue(primaryUsed) ? ClampUsage(primaryUsed) : 0,
            HasUsageValue(secondaryUsed) ? ClampUsage(secondaryUsed) : 0);
        return UsageThresholdColor(max);
    }

    private static Color ResolveUsageColor(double usedPercent)
    {
        if (HasUsageValue(usedPercent) == false)
        {
            return Color.FromArgb(137, 148, 162);
        }

        return UsageThresholdColor(usedPercent);
    }

    private static Color UsageThresholdColor(double usedPercent)
    {
        var clamped = ClampUsage(usedPercent);
        return clamped >= 90
            ? Color.FromArgb(220, 38, 38)
            : clamped >= 70
                ? Color.FromArgb(245, 124, 0)
                : Color.FromArgb(0, 166, 90);
    }

    private static Image GenerateDotIcon(Color baseColor, Color usageColor, bool active)
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var outer = new SolidBrush(baseColor);
        using var inner = new SolidBrush(Color.White);
        using var ring = new SolidBrush(active ? usageColor : Color.Gray);

        g.FillEllipse(outer, 1, 1, 14, 14);
        g.FillEllipse(inner, 3, 3, 10, 10);
        g.FillEllipse(ring, 5, 5, 6, 6);

        return bitmap;
    }

    private static Icon CreateTrayIcon((double? primary, double? secondary, bool hasActive) usage)
    {
        var size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;

        var hasUsage = usage.primary.HasValue && HasUsageValue(usage.primary.Value);
        var primary = hasUsage ? ClampUsage(usage.primary!.Value) : 0d;
        var accent = hasUsage ? ResolveUsageColor(primary) : Color.FromArgb(132, 143, 156);
        var trackColor = usage.hasActive ? Color.FromArgb(218, 226, 236) : Color.FromArgb(196, 204, 214);
        var ringBounds = new RectangleF(6.25f, 6.25f, 19.5f, 19.5f);

        using (var glow = new Pen(Color.FromArgb(80, 255, 255, 255), 7.4f))
        {
            glow.StartCap = LineCap.Round;
            glow.EndCap = LineCap.Round;
            g.DrawArc(glow, ringBounds, -90, 360);
        }

        using (var track = new Pen(trackColor, 5.2f))
        {
            track.StartCap = LineCap.Round;
            track.EndCap = LineCap.Round;
            g.DrawArc(track, ringBounds, -90, 360);
        }

        if (hasUsage && primary > 0.25)
        {
            using var progress = new Pen(accent, 5.2f);
            progress.StartCap = LineCap.Round;
            progress.EndCap = LineCap.Round;
            g.DrawArc(progress, ringBounds, -90, (float)(360d * primary / 100d));
        }

        using (var center = new SolidBrush(Color.FromArgb(235, 248, 250, 253)))
        {
            g.FillEllipse(center, 11.5f, 11.5f, 9f, 9f);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawMiniUsageBar(Graphics graphics, Brush back, Brush fill, Rectangle rect, double value)
    {
        graphics.FillRectangle(back, rect);
        var width = Math.Clamp((int)Math.Round(rect.Width * value / 100d), 0, rect.Width);
        if (width > 0)
        {
            graphics.FillRectangle(fill, rect.X, rect.Y, width, rect.Height);
        }
    }

    private void ShowQuickStats()
    {
        _ = RefreshUsageInBackgroundAsync();
        using var form = new UsageStatsForm(_registry.Accounts, _registry.ActiveAccountId, _configStore.Config);
        form.ShowDialog();
    }

    private static string BuildUsageTooltip(TokenAccount account)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"5小时：{FormatUsagePercent(account.PrimaryUsedPercent)}",
            $"7天：{FormatUsagePercent(account.SecondaryUsedPercent)}",
            $"5小时重置：{FormatReset(account.PrimaryResetAt)}",
            $"7天重置：{FormatReset(account.SecondaryResetAt)}",
        });
        /*
        return string.Join(Environment.NewLine, new[]
        {
            $"主池：已用 {FormatUsagePercent(account.PrimaryUsedPercent)} · 剩余 {FormatRemainingPercent(account.PrimaryUsedPercent)}",
            $"次池：已用 {FormatUsagePercent(account.SecondaryUsedPercent)} · 剩余 {FormatRemainingPercent(account.SecondaryUsedPercent)}",
            $"主池重置：{FormatReset(account.PrimaryResetAt)}",
            $"次池重置：{FormatReset(account.SecondaryResetAt)}",
        });
        */
    }

    private static string FormatUsagePercent(double value)
    {
        return HasUsageValue(value) ? $"{ClampUsage(value):F1}%" : "--";
    }

    private static string FormatRemainingPercent(double value)
    {
        return HasUsageValue(value) ? $"{Math.Max(0, 100 - ClampUsage(value)):F1}%" : "--";
    }

    private static string FormatReset(DateTimeOffset? value)
    {
        return value is null ? "--" : value.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private string BuildQuickStatsText()
    {
        if (_registry.Accounts.Count == 0)
        {
            return "暂无账号可统计";
        }

        var lines = new[] { "=== Codexbar 使用统计 ===" }
            .Concat(_registry.Accounts.Select(account =>
            {
                var usage = BuildUsageText(account);
                var mark = string.Equals(_registry.ActiveAccountId, account.AccountId, StringComparison.Ordinal) ? "★" : " ·";
                return $"{mark} {BuildAccountLabel(account)} | {usage}";
            }));

        return string.Join(Environment.NewLine, lines);
    }

    private static bool HasUsageValue(double value)
    {
        return double.IsFinite(value) && value >= 0;
    }

    private static double ClampUsage(double value)
    {
        return Math.Clamp(value, 0, 100);
    }

    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer()
            : base(new TrayColorTable())
        {
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(TrayMenuBack);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        public TrayColorTable()
        {
            UseSystemColors = false;
        }

        public override Color MenuItemSelected => Color.FromArgb(235, 244, 255);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(235, 244, 255);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(235, 244, 255);
        public override Color MenuItemBorder => Color.FromArgb(185, 212, 244);
        public override Color ToolStripDropDownBackground => TrayMenuBack;
        public override Color ImageMarginGradientBegin => TrayMenuBack;
        public override Color ImageMarginGradientMiddle => TrayMenuBack;
        public override Color ImageMarginGradientEnd => TrayMenuBack;
        public override Color SeparatorDark => TrayBorder;
        public override Color SeparatorLight => Color.White;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
