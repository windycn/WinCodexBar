using CodexBarWin.Models;
using CodexBarWin.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexBarWin.UI;

public sealed class CodexBarDashboardForm : Form
{
    private enum DashboardView
    {
        Accounts,
        Activity,
        Sessions,
    }

    private readonly AccountRegistry _registry;
    private readonly CodexBarConfigStore _configStore;
    private readonly UsageRefreshCoordinator _refreshCoordinator;
    private readonly Action<TokenAccount> _activateAccount;
    private readonly Action _addAccountRequested;
    private readonly Action _importRequested;
    private readonly Action _exportRequested;
    private readonly Action _openSettingsRequested;
    private readonly Func<bool> _isKeepAwakeEnabled;
    private readonly Action<bool> _setKeepAwakeEnabled;
    private readonly ToolTip _toolTip = new() { InitialDelay = 320, ReshowDelay = 120, AutoPopDelay = 4000 };

    private readonly Label _countLabel = new();
    private readonly Label _activeLabel = new();
    private readonly Label _usageLabel = new();
    private readonly Label _healthLabel = new();
    private readonly Label _statusLabel = new();
    private readonly SmoothFlowLayoutPanel _accountList = new();
    private readonly Panel _viewHost = new();
    private readonly Button _accountsTab = new FluentButton();
    private readonly Button _activityTab = new FluentButton();
    private readonly Button _sessionsTab = new FluentButton();
    private readonly Button _refreshButton = new FluentButton();
    private readonly Button _keepAwakeButton = new FluentButton();
    private static readonly TimeSpan InsightScanCacheDuration = TimeSpan.FromSeconds(20);
    private TokenActivityOverviewPanel? _activityOverview;
    private SessionAnalysisPanel? _sessionAnalysis;
    private Control? _accountListSurface;
    private DashboardView _selectedView = DashboardView.Accounts;
    private CancellationTokenSource? _refreshCts;
    private DateTimeOffset _activityScannedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _sessionScannedAt = DateTimeOffset.MinValue;
    private bool _isRefreshing;
    private bool _activityScanRunning;
    private bool _sessionScanRunning;

    public CodexBarDashboardForm(
        AccountRegistry registry,
        CodexBarConfigStore configStore,
        UsageRefreshCoordinator refreshCoordinator,
        Action<TokenAccount> activateAccount,
        Action addAccountRequested,
        Action importRequested,
        Action exportRequested,
        Action openSettingsRequested,
        Func<bool> isKeepAwakeEnabled,
        Action<bool> setKeepAwakeEnabled)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _activateAccount = activateAccount ?? throw new ArgumentNullException(nameof(activateAccount));
        _addAccountRequested = addAccountRequested ?? throw new ArgumentNullException(nameof(addAccountRequested));
        _importRequested = importRequested ?? throw new ArgumentNullException(nameof(importRequested));
        _exportRequested = exportRequested ?? throw new ArgumentNullException(nameof(exportRequested));
        _openSettingsRequested = openSettingsRequested ?? throw new ArgumentNullException(nameof(openSettingsRequested));
        _isKeepAwakeEnabled = isKeepAwakeEnabled ?? throw new ArgumentNullException(nameof(isKeepAwakeEnabled));
        _setKeepAwakeEnabled = setKeepAwakeEnabled ?? throw new ArgumentNullException(nameof(setKeepAwakeEnabled));

        Text = "WinCodexBar 工作台";
        Font = FluentTheme.TextFontPx(14);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1120, 860);
        Size = new Size(1280, 960);
        BackColor = FluentTheme.LayerBackground;
        AppIconProvider.Apply(this);

        BuildLayout();
        Shown += (_, _) => RefreshData();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MicaSupport.TryApply(this);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0)
        {
            base.OnPaintBackground(e);
            return;
        }

        using var brush = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(250, 251, 253),
            Color.FromArgb(235, 243, 247),
            LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _refreshCts?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _refreshCts?.Dispose();
        }

        base.Dispose(disposing);
    }

    public void RefreshData()
    {
        UpdateMetrics();
        RefreshAccountListOnly();
        RefreshSelectedInsightViewIfNeeded();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(28, 22, 28, 22),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);
        root.Controls.Add(BuildViewTabs(), 0, 2);
        root.Controls.Add(BuildViewHost(), 0, 3);
        root.Controls.Add(BuildFooter(), 0, 4);
        SelectDashboardView(DashboardView.Accounts);
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            RowCount = 2,
            ColumnCount = 1,
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titlePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
        };
        titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        header.Controls.Add(titlePanel, 0, 0);

        var titleStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            RowCount = 2,
            ColumnCount = 1,
        };
        titleStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        titleStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        titlePanel.Controls.Add(titleStack, 0, 0);

        titleStack.Controls.Add(new Label
        {
            Text = "WinCodexBar",
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(30, FontStyle.Bold),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        titleStack.Controls.Add(new Label
        {
            Text = "管理 OpenAI 账号、5h / 7d 额度与 Codex 同步。",
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(15),
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);

        var provider = new Label
        {
            Text = "OpenAI",
            Width = 128,
            Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = FluentTheme.TextFontPx(16, FontStyle.Bold),
            ForeColor = FluentTheme.Accent,
            BackColor = Color.FromArgb(229, 240, 252),
            Margin = new Padding(0, 6, 0, 0),
        };
        titlePanel.Controls.Add(provider, 1, 0);

        var metrics = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 8, 0, 0),
        };
        metrics.Controls.Add(MetricCard("账号", _countLabel, 140));
        metrics.Controls.Add(MetricCard("当前账号", _activeLabel, 300));
        metrics.Controls.Add(MetricCard("用量", _usageLabel, 270));
        metrics.Controls.Add(MetricCard("健康", _healthLabel, 220));
        header.Controls.Add(metrics, 0, 1);

        return header;
    }

    private Control MetricCard(string title, Label value, int width)
    {
        var card = new Panel
        {
            Width = width,
            Height = 66,
            Margin = new Padding(0, 0, 12, 10),
            BackColor = FluentTheme.CardBackground,
            Padding = new Padding(12, 8, 12, 8),
        };
        card.Paint += (_, e) => DrawRoundedPanel(e.Graphics, card.ClientRectangle, FluentTheme.CardBackground, FluentTheme.StrokeDefault, 6);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            RowCount = 2,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            Font = FluentTheme.TextFontPx(13),
        }, 0, 0);

        value.Dock = DockStyle.Fill;
        value.ForeColor = FluentTheme.TextPrimary;
        value.BackColor = Color.Transparent;
        value.Font = FluentTheme.TextFontPx(17, FontStyle.Bold);
        value.AutoEllipsis = true;
        value.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(value, 0, 1);
        return card;
    }

    private Control BuildToolbar()
    {
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 9, 0, 7),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));

        var commands = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        toolbar.Controls.Add(commands, 0, 0);

        ConfigureButton(_refreshButton, "刷新用量", true, 118);
        _refreshButton.Click += async (_, _) => await ManualRefreshAsync().ConfigureAwait(false);
        commands.Controls.Add(_refreshButton);
        commands.Controls.Add(MakeCommandButton("添加账号", _addAccountRequested));
        commands.Controls.Add(MakeCommandButton("导入账号", _importRequested));
        commands.Controls.Add(MakeCommandButton("导出账号", _exportRequested));
        commands.Controls.Add(MakeCommandButton("设置", _openSettingsRequested));

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = FluentTheme.TextSecondary;
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.TextAlign = ContentAlignment.MiddleRight;
        _statusLabel.Font = FluentTheme.TextFontPx(14);
        toolbar.Controls.Add(_statusLabel, 1, 0);

        return toolbar;
    }

    private Control BuildViewTabs()
    {
        var wrap = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 5, 0, 5),
        };

        var tabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 360,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        wrap.Controls.Add(tabs);

        ConfigureTabButton(_accountsTab, "账号", DashboardView.Accounts);
        ConfigureTabButton(_activityTab, "Token 活动", DashboardView.Activity);
        ConfigureTabButton(_sessionsTab, "会话分析", DashboardView.Sessions);
        tabs.Controls.Add(_accountsTab);
        tabs.Controls.Add(_activityTab);
        tabs.Controls.Add(_sessionsTab);
        return wrap;
    }

    private Control BuildViewHost()
    {
        _viewHost.Dock = DockStyle.Fill;
        _viewHost.BackColor = Color.Transparent;
        _accountListSurface = BuildAccountList();
        return _viewHost;
    }

    private void ConfigureTabButton(Button button, string text, DashboardView view)
    {
        button.Text = text;
        button.Width = view == DashboardView.Activity ? 112 : 92;
        button.Height = 32;
        button.Margin = new Padding(0, 0, 8, 0);
        button.Font = FluentTheme.TextFontPx(13, FontStyle.Bold);
        button.Click += (_, _) => SelectDashboardView(view);
        _toolTip.SetToolTip(button, text);
    }

    public void ShowSessionAnalysis()
    {
        SelectDashboardView(DashboardView.Sessions);
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void SelectDashboardView(DashboardView view)
    {
        _selectedView = view;
        UpdateTabButton(_accountsTab, view == DashboardView.Accounts);
        UpdateTabButton(_activityTab, view == DashboardView.Activity);
        UpdateTabButton(_sessionsTab, view == DashboardView.Sessions);

        var target = view switch
        {
            DashboardView.Activity => ActivityOverview,
            DashboardView.Sessions => SessionAnalysis,
            _ => _accountListSurface,
        };

        if (target is null)
        {
            return;
        }

        _viewHost.SuspendLayout();
        _viewHost.Controls.Clear();
        target.Dock = DockStyle.Fill;
        target.Margin = Padding.Empty;
        _viewHost.Controls.Add(target);
        _viewHost.ResumeLayout(true);
        if (view == DashboardView.Accounts)
        {
            ResizeAccountRows();
        }
        target.Invalidate();
        RefreshSelectedInsightViewIfNeeded();
    }

    private static void UpdateTabButton(Button button, bool selected)
    {
        if (button is FluentButton fluent)
        {
            fluent.Primary = selected;
        }

        button.BackColor = selected ? FluentTheme.Accent : FluentTheme.ControlBackground;
        button.ForeColor = selected ? FluentTheme.TextOnAccent : FluentTheme.TextPrimary;
        button.Invalidate();
    }

    private TokenActivityOverviewPanel ActivityOverview
        => _activityOverview ??= BuildActivityOverview();

    private SessionAnalysisPanel SessionAnalysis
        => _sessionAnalysis ??= BuildSessionAnalysis();

    private TokenActivityOverviewPanel BuildActivityOverview()
    {
        var panel = new TokenActivityOverviewPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14),
            BackColor = FluentTheme.CardBackground,
            TokenUnitDisplayMode = _configStore.Config.OpenAI.TokenUnitDisplayMode,
            OpenAISettings = _configStore.Config.OpenAI,
        };
        return panel;
    }

    private SessionAnalysisPanel BuildSessionAnalysis()
    {
        var panel = new SessionAnalysisPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14),
            BackColor = FluentTheme.CardBackground,
            TokenUnitDisplayMode = _configStore.Config.OpenAI.TokenUnitDisplayMode,
        };
        return panel;
    }

    private void RefreshSelectedInsightViewIfNeeded(bool force = false)
    {
        if (_selectedView == DashboardView.Activity)
        {
            RefreshActivityOverviewAsync(force);
        }
        else if (_selectedView == DashboardView.Sessions)
        {
            RefreshSessionAnalysisAsync(force);
        }
    }

    private async void RefreshActivityOverviewAsync(bool force)
    {
        var panel = ActivityOverview;
        panel.TokenUnitDisplayMode = _configStore.Config.OpenAI.TokenUnitDisplayMode;
        panel.OpenAISettings = _configStore.Config.OpenAI;

        if (_activityScanRunning)
        {
            return;
        }

        if (!force && DateTimeOffset.UtcNow - _activityScannedAt < InsightScanCacheDuration)
        {
            panel.Invalidate();
            return;
        }

        _activityScanRunning = true;
        if (_selectedView == DashboardView.Activity)
        {
            _statusLabel.Text = "正在加载 Token 活动...";
        }

        try
        {
            var activity = await Task.Run(TokenUsageScanService.ScanActivity).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            panel.TokenUnitDisplayMode = _configStore.Config.OpenAI.TokenUnitDisplayMode;
            panel.OpenAISettings = _configStore.Config.OpenAI;
            panel.Activity = activity;
            _activityScannedAt = DateTimeOffset.UtcNow;
            if (_selectedView == DashboardView.Activity)
            {
                _statusLabel.Text = LatestRefreshNote(_registry.Accounts.ToArray());
            }
        }
        catch (Exception ex)
        {
            if (_selectedView == DashboardView.Activity)
            {
                _statusLabel.Text = "Token 活动加载失败：" + ex.Message;
            }
        }
        finally
        {
            _activityScanRunning = false;
        }
    }

    private async void RefreshSessionAnalysisAsync(bool force)
    {
        var panel = SessionAnalysis;
        panel.TokenUnitDisplayMode = _configStore.Config.OpenAI.TokenUnitDisplayMode;

        if (_sessionScanRunning)
        {
            return;
        }

        if (!force && DateTimeOffset.UtcNow - _sessionScannedAt < InsightScanCacheDuration)
        {
            panel.Invalidate();
            return;
        }

        _sessionScanRunning = true;
        if (_selectedView == DashboardView.Sessions)
        {
            _statusLabel.Text = "正在加载会话分析...";
        }

        try
        {
            var analysis = await Task.Run(TokenUsageScanService.ScanSessions).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            panel.TokenUnitDisplayMode = _configStore.Config.OpenAI.TokenUnitDisplayMode;
            panel.Analysis = analysis;
            _sessionScannedAt = DateTimeOffset.UtcNow;
            if (_selectedView == DashboardView.Sessions)
            {
                _statusLabel.Text = LatestRefreshNote(_registry.Accounts.ToArray());
            }
        }
        catch (Exception ex)
        {
            if (_selectedView == DashboardView.Sessions)
            {
                _statusLabel.Text = "会话分析加载失败：" + ex.Message;
            }
        }
        finally
        {
            _sessionScanRunning = false;
        }
    }

    private Control BuildAccountList()
    {
        var surface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = FluentTheme.CardBackground,
            Padding = new Padding(1),
        };
        surface.Paint += (_, e) => DrawRoundedPanel(e.Graphics, surface.ClientRectangle, FluentTheme.CardBackground, FluentTheme.StrokeDefault, 6);

        _accountList.Dock = DockStyle.Fill;
        _accountList.FlowDirection = FlowDirection.TopDown;
        _accountList.WrapContents = false;
        _accountList.AutoScroll = true;
        _accountList.BackColor = FluentTheme.CardBackground;
        _accountList.Padding = new Padding(12);
        _accountList.ScrollQuantum = 0;
        _accountList.Resize += (_, _) => ResizeAccountRows();
        surface.Controls.Add(_accountList);
        return surface;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0, 12, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));

        var note = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ForeColor = FluentTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = string.Empty,
            AutoEllipsis = true,
        };
        footer.Controls.Add(note, 0, 0);

        ConfigureButton(_keepAwakeButton, "保持唤醒", false, 132);
        _keepAwakeButton.Click += (_, _) =>
        {
            _setKeepAwakeEnabled(!_isKeepAwakeEnabled());
            UpdateMetrics();
        };
        footer.Controls.Add(_keepAwakeButton, 1, 0);

        var close = new FluentButton();
        ConfigureButton(close, "关闭窗口", false, 104);
        close.Click += (_, _) => Hide();
        footer.Controls.Add(close, 2, 0);

        return footer;
    }

    private Button MakeCommandButton(string text, Action action)
    {
        var button = new FluentButton();
        ConfigureButton(button, text, false, 118);
        button.Click += (_, _) => action();
        return button;
    }

    private static void ConfigureButton(Button button, string text, bool primary, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 34;
        button.Margin = new Padding(0, 0, 10, 0);
        button.Font = FluentTheme.TextFontPx(14, primary ? FontStyle.Bold : FontStyle.Regular);
        button.TextAlign = ContentAlignment.MiddleCenter;
        FluentTheme.ApplyButton(button, primary);
    }

    private void UpdateMetrics()
    {
        var accounts = _registry.Accounts.ToArray();
        var config = _configStore.Config;
        var active = accounts.FirstOrDefault(a => string.Equals(a.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal));

        _countLabel.Text = accounts.Length.ToString(CultureInfo.InvariantCulture);
        _activeLabel.Text = active is null ? "未激活" : TrimMiddle(AccountUsageHelpers.DisplayName(active), 30);
        _usageLabel.Text = BuildUsageSummary(accounts, config);
        var warning = accounts.Count(a => AccountUsageHelpers.Health(a, config.OpenAI.WarningThresholdPercent, config.OpenAI.DangerThresholdPercent) == AccountHealthStatus.Warning);
        var danger = accounts.Count(a => AccountUsageHelpers.Health(a, config.OpenAI.WarningThresholdPercent, config.OpenAI.DangerThresholdPercent) == AccountHealthStatus.Exhausted);
        _healthLabel.Text = $"警戒 {warning} · 高负载 {danger}";
        _keepAwakeButton.Text = _isKeepAwakeEnabled() ? "关闭保持唤醒" : "开启保持唤醒";
        _statusLabel.Text = LatestRefreshNote(accounts);
    }

    private void RefreshAccountListOnly()
    {
        var config = _configStore.Config;
        var accounts = OrderedAccounts(_registry.Accounts.ToArray()).ToArray();

        _accountList.SuspendLayout();
        _accountList.Controls.Clear();
        if (accounts.Length == 0)
        {
            _accountList.Controls.Add(BuildEmptyState());
        }
        else
        {
            foreach (var account in accounts)
            {
                _accountList.Controls.Add(BuildAccountRow(account, config));
            }
        }
        _accountList.ResumeLayout();
        ResizeAccountRows();
    }

    private Control BuildEmptyState()
    {
        var panel = new RoundedPanel
        {
            Width = Math.Max(520, _accountList.ClientSize.Width - 36),
            Height = 120,
            BackColor = FluentTheme.SubtleBackground,
            BorderColor = FluentTheme.StrokeDefault,
            CornerRadius = 6,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(18),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            RowCount = 3,
            ColumnCount = 1,
        };
        panel.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "还没有导入账号",
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(18, FontStyle.Bold),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "可以直接添加 OpenAI OAuth 账号，也支持常见 JSON / CSV 账号文件导入导出格式。",
            Dock = DockStyle.Fill,
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
        }, 0, 1);
        var import = new FluentButton();
        ConfigureButton(import, "添加账号", true, 112);
        import.Click += (_, _) => _addAccountRequested();
        layout.Controls.Add(import, 0, 2);
        return panel;
    }

    private Control BuildAccountRow(TokenAccount account, CodexBarConfig config)
    {
        var isActive = string.Equals(account.AccountId, _registry.ActiveAccountId, StringComparison.Ordinal);
        var status = AccountUsageHelpers.Health(account, config.OpenAI.WarningThresholdPercent, config.OpenAI.DangerThresholdPercent);
        var accent = HealthColor(status);
        var row = new RoundedPanel
        {
            Width = Math.Max(640, _accountList.ClientSize.Width - 36),
            Height = 112,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(14, 10, 14, 10),
            BackColor = isActive ? Color.FromArgb(229, 240, 252) : FluentTheme.SubtleBackground,
            BorderColor = isActive ? FluentTheme.Accent : FluentTheme.StrokeDefault,
            CornerRadius = 6,
            Cursor = Cursors.Hand,
        };
        row.DoubleClick += (_, _) =>
        {
            _activateAccount(account);
            RefreshData();
        };
        _toolTip.SetToolTip(row, isActive ? "当前账号" : "切换到此账号");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 5,
            RowCount = 1,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
        row.Controls.Add(layout);

        var marker = new Label
        {
            Text = "●",
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(18),
            ForeColor = accent,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        layout.Controls.Add(marker, 0, 0);

        var accountInfo = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            RowCount = 3,
            ColumnCount = 1,
        };
        accountInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        accountInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        accountInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.Controls.Add(accountInfo, 1, 0);

        accountInfo.Controls.Add(new Label
        {
            Text = AccountUsageHelpers.DisplayName(account),
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(18, FontStyle.Bold),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        accountInfo.Controls.Add(new Label
        {
            Text = AccountUsageHelpers.HealthLabel(status),
            Dock = DockStyle.Fill,
            ForeColor = accent,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);
        accountInfo.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(account.OrganizationName) ? AccountUsageHelpers.FormatLastChecked(account.LastChecked) : account.OrganizationName,
            Dock = DockStyle.Fill,
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 2);

        layout.Controls.Add(MakePlanBadge(account), 2, 0);
        layout.Controls.Add(MakeUsageBlock(account, config), 3, 0);
        layout.Controls.Add(MakeRowActions(account, isActive), 4, 0);

        return row;
    }

    private Control MakePlanBadge(TokenAccount account)
    {
        var label = new Label
        {
            Text = AccountUsageHelpers.PlanLabel(account),
            Width = 78,
            Height = 28,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = FluentTheme.TextFontPx(14, FontStyle.Bold),
            ForeColor = PlanColor(account),
            BackColor = Color.FromArgb(238, 238, 238),
        };
        return label;
    }

    private Control MakeUsageBlock(TokenAccount account, CodexBarConfig config)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(0, 2, 0, 0),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        panel.Controls.Add(new Label
        {
            Text = $"5h {AccountUsageHelpers.FormatDisplayPercent(account.PrimaryUsedPercent, config.OpenAI.UsageDisplayMode)}   ·   7d {AccountUsageHelpers.FormatDisplayPercent(account.SecondaryUsedPercent, config.OpenAI.UsageDisplayMode)}",
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(16, FontStyle.Bold),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        panel.Controls.Add(new Label
        {
            Text = $"重置 5h {AccountUsageHelpers.FormatResetCountdown(account.PrimaryResetAt)} · 7d {AccountUsageHelpers.FormatResetCountdown(account.SecondaryResetAt)}",
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(13),
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);
        panel.Controls.Add(new Label
        {
            Text = "刷新 " + AccountUsageHelpers.FormatLastChecked(account.LastChecked),
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(13),
            ForeColor = FluentTheme.TextTertiary,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 2);
        return panel;
    }

    private Control MakeRowActions(TokenAccount account, bool isActive)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 26, 0, 0),
        };
        var refresh = new FluentButton();
        ConfigureButton(refresh, "刷新", false, 54);
        _toolTip.SetToolTip(refresh, "刷新此账号");
        refresh.Click += async (_, _) => await RefreshSingleAsync(account, refresh).ConfigureAwait(false);
        panel.Controls.Add(refresh);

        var activate = new FluentButton();
        ConfigureButton(activate, isActive ? "已选" : "切换", !isActive, 68);
        activate.Enabled = !isActive;
        activate.Click += (_, _) =>
        {
            _activateAccount(account);
            RefreshData();
        };
        panel.Controls.Add(activate);
        return panel;
    }

    private async Task ManualRefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;

        _refreshButton.Enabled = false;
        _refreshButton.Text = "刷新中";
        _statusLabel.Text = "正在刷新所有账号...";

        try
        {
            var report = await _refreshCoordinator.RefreshAllAsync(cancellationToken: token).ConfigureAwait(true);
            RefreshData();
            _statusLabel.Text = $"刷新完成：成功 {report.Updated}，授权失效 {report.Unauthorized}，不可用 {report.Forbidden}，失败 {report.Failed}";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "刷新已取消";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            _refreshButton.Text = "刷新用量";
            _refreshButton.Enabled = true;
            _isRefreshing = false;
        }
    }

    private async Task RefreshSingleAsync(TokenAccount account, Button trigger)
    {
        var oldText = trigger.Text;
        trigger.Enabled = false;
        trigger.Text = "...";
        _statusLabel.Text = $"正在刷新 {AccountUsageHelpers.DisplayName(account)}";
        try
        {
            var outcome = await _refreshCoordinator.RefreshSingleAsync(account).ConfigureAwait(true);
            _registry.Save();
            RefreshData();
            _statusLabel.Text = outcome == UsageRefreshOutcome.Updated ? "账号用量已刷新" : "账号刷新未成功：" + outcome;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            trigger.Text = oldText;
            trigger.Enabled = true;
        }
    }

    private void ResizeAccountRows()
    {
        var width = Math.Max(640, _accountList.ClientSize.Width - _accountList.Padding.Horizontal - 8);
        foreach (Control control in _accountList.Controls)
        {
            control.Width = width;
        }
    }

    private IEnumerable<TokenAccount> OrderedAccounts(IReadOnlyCollection<TokenAccount> accounts)
    {
        var activeId = _registry.ActiveAccountId;
        return accounts
            .OrderBy(a => !string.Equals(a.AccountId, activeId, StringComparison.Ordinal))
            .ThenBy(a => HealthSortKey(a))
            .ThenByDescending(AccountUsageHelpers.MaxUsage)
            .ThenBy(AccountUsageHelpers.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    private static int HealthSortKey(TokenAccount account)
    {
        if (account.IsSuspended) return 4;
        if (account.TokenExpired) return 3;
        if (AccountUsageHelpers.MaxUsage(account) >= 100) return 2;
        if (AccountUsageHelpers.MaxUsage(account) >= 70) return 1;
        return 0;
    }

    private static string BuildUsageSummary(IReadOnlyCollection<TokenAccount> accounts, CodexBarConfig config)
    {
        if (accounts.Count == 0)
        {
            return "暂无账号";
        }

        var primary = accounts.Select(a => AccountUsageHelpers.Clamp(a.PrimaryUsedPercent)).DefaultIfEmpty(0).Average();
        var secondary = accounts.Select(a => AccountUsageHelpers.Clamp(a.SecondaryUsedPercent)).DefaultIfEmpty(0).Average();
        return $"5h {AccountUsageHelpers.DisplayPercent(primary, config.OpenAI.UsageDisplayMode):F1}% · 7d {AccountUsageHelpers.DisplayPercent(secondary, config.OpenAI.UsageDisplayMode):F1}%";
    }

    private static string LatestRefreshNote(IReadOnlyCollection<TokenAccount> accounts)
    {
        var latest = accounts
            .Select(a => a.LastChecked)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .OrderByDescending(t => t)
            .FirstOrDefault();

        return latest == default ? "尚未刷新" : "最近刷新 " + AccountUsageHelpers.FormatLastChecked(latest);
    }

    private static Color HealthColor(AccountHealthStatus status)
    {
        return status switch
        {
            AccountHealthStatus.Healthy => FluentTheme.Success,
            AccountHealthStatus.Warning => FluentTheme.Warning,
            AccountHealthStatus.Exhausted => Color.FromArgb(220, 38, 38),
            AccountHealthStatus.Suspended => Color.FromArgb(99, 102, 241),
            AccountHealthStatus.TokenExpired => Color.FromArgb(220, 38, 38),
            _ => FluentTheme.TextTertiary,
        };
    }

    private static Color PlanColor(TokenAccount account)
    {
        return (account.PlanType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "team" => FluentTheme.Accent,
            "plus" => Color.FromArgb(120, 78, 220),
            "pro" or "prolite" => FluentTheme.Success,
            _ => FluentTheme.TextSecondary,
        };
    }

    private static string TrimMiddle(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= max)
        {
            return text;
        }

        var left = Math.Max(1, (max - 1) / 2);
        var right = Math.Max(1, max - left - 1);
        return $"{text[..left]}...{text[^right..]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "--";
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}天 {duration.Hours}小时";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}小时 {duration.Minutes}分";
        return $"{Math.Max(1, (int)duration.TotalMinutes)}分";
    }

    private static void DrawRoundedPanel(Graphics graphics, Rectangle bounds, Color fill, Color stroke, int radius)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1));
        using var path = FluentTheme.RoundedRectanglePath(rect, radius);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        if (stroke.A > 0)
        {
            using var pen = new Pen(stroke);
            graphics.DrawPath(pen, path);
        }
    }

    private sealed class RoundedPanel : Panel
    {
        public Color BorderColor { get; set; } = FluentTheme.StrokeDefault;

        public int CornerRadius { get; set; } = 6;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawRoundedPanel(e.Graphics, ClientRectangle, BackColor, BorderColor, CornerRadius);
        }
    }

    private sealed class SmoothFlowLayoutPanel : FlowLayoutPanel
    {
        public int ScrollQuantum { get; set; } = 120;

        public SmoothFlowLayoutPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!VerticalScroll.Visible)
            {
                base.OnMouseWheel(e);
                return;
            }

            var max = Math.Max(VerticalScroll.Minimum, VerticalScroll.Maximum - VerticalScroll.LargeChange + 1);
            var delta = Math.Sign(e.Delta) * Math.Max(24, ScrollQuantum);
            var target = Math.Clamp(VerticalScroll.Value - delta, VerticalScroll.Minimum, max);
            AutoScrollPosition = new Point(0, target);
            PerformLayout();
            Invalidate(true);
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            if (se.ScrollOrientation == ScrollOrientation.VerticalScroll
                && se.Type is not ScrollEventType.ThumbTrack)
            {
                BeginInvoke(new Action(SnapVerticalScroll));
            }
            Invalidate(true);
        }

        private void SnapVerticalScroll()
        {
            if (IsDisposed || !VerticalScroll.Visible || ScrollQuantum <= 0)
            {
                return;
            }

            var max = Math.Max(VerticalScroll.Minimum, VerticalScroll.Maximum - VerticalScroll.LargeChange + 1);
            var snapped = (int)Math.Round(VerticalScroll.Value / (double)ScrollQuantum) * ScrollQuantum;
            snapped = Math.Clamp(snapped, VerticalScroll.Minimum, max);
            if (snapped != VerticalScroll.Value)
            {
                AutoScrollPosition = new Point(0, snapped);
            }

            PerformLayout();
            Invalidate(true);
        }
    }

    private sealed class SessionAnalysisPanel : Panel
    {
        private SessionAnalysisSummary _analysis = SessionAnalysisSummary.Empty;
        private TokenUnitDisplayMode _tokenUnitDisplayMode = TokenUnitDisplayMode.Chinese;

        public SessionAnalysisSummary Analysis
        {
            get => _analysis;
            set
            {
                _analysis = value ?? SessionAnalysisSummary.Empty;
                Invalidate();
            }
        }

        public TokenUnitDisplayMode TokenUnitDisplayMode
        {
            get => _tokenUnitDisplayMode;
            set
            {
                _tokenUnitDisplayMode = value;
                Invalidate();
            }
        }

        public SessionAnalysisPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            DrawRoundedPanel(g, ClientRectangle, FluentTheme.CardBackground, FluentTheme.StrokeDefault, 8);

            var bounds = new Rectangle(18, 14, Math.Max(1, Width - 36), Math.Max(1, Height - 28));
            using var title = FluentTheme.TextFontPx(18, FontStyle.Bold);
            using var body = FluentTheme.TextFontPx(12);
            TextRenderer.DrawText(g, "会话分析", title, new Rectangle(bounds.Left, bounds.Top, 120, 26), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "按本地 Codex session 聚合模型、Token 与最近活动", body, new Rectangle(bounds.Left + 96, bounds.Top + 3, bounds.Width - 110, 22), FluentTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (_analysis.SessionCount == 0)
            {
                TextRenderer.DrawText(g, "暂无可分析的 session 记录。", body, new Rectangle(bounds.Left, bounds.Top + 48, bounds.Width, 24), FluentTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            var statTop = bounds.Top + 36;
            DrawSessionStat(g, new Rectangle(bounds.Left, statTop, 132, 50), _analysis.SessionCount.ToString(CultureInfo.InvariantCulture), "Session");
            DrawSessionStat(g, new Rectangle(bounds.Left + 142, statTop, 132, 50), $"{_analysis.ActiveSessionCount}/{_analysis.ArchivedSessionCount}", "活跃 / 归档");
            DrawSessionStat(g, new Rectangle(bounds.Left + 284, statTop, 152, 50), FormatTokens(_analysis.AverageTokensPerSession), "平均 Token");
            DrawSessionStat(g, new Rectangle(bounds.Left + 446, statTop, 150, 50), FormatDuration(_analysis.LongestSessionDuration), "最长会话");

            var contentTop = bounds.Top + 96;
            var leftWidth = Math.Max(300, (int)Math.Round(bounds.Width * 0.38));
            var left = new Rectangle(bounds.Left, contentTop, leftWidth, bounds.Bottom - contentTop);
            var right = new Rectangle(left.Right + 16, contentTop, bounds.Right - left.Right - 16, bounds.Bottom - contentTop);
            var modelHeight = Math.Min(190, Math.Max(112, left.Height / 2));
            DrawModelBars(g, new Rectangle(left.Left, left.Top, left.Width, modelHeight));
            DrawSessionList(g, new Rectangle(left.Left, left.Top + modelHeight + 16, left.Width, Math.Max(80, left.Height - modelHeight - 16)), "Token 最高会话", _analysis.TopTokenSessions);
            DrawSessionList(g, right, "最近会话", _analysis.RecentSessions);
        }

        private void DrawSessionStat(Graphics g, Rectangle rect, string value, string label)
        {
            DrawRoundedPanel(g, rect, Color.FromArgb(248, 250, 252), FluentTheme.StrokeDefault, 6);
            using var valueFont = FluentTheme.TextFontPx(16, FontStyle.Bold);
            using var labelFont = FluentTheme.TextFontPx(11);
            TextRenderer.DrawText(g, value, valueFont, new Rectangle(rect.Left + 10, rect.Top + 7, rect.Width - 20, 22), FluentTheme.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, label, labelFont, new Rectangle(rect.Left + 10, rect.Top + 29, rect.Width - 20, 18), FluentTheme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawModelBars(Graphics g, Rectangle rect)
        {
            using var title = FluentTheme.TextFontPx(13, FontStyle.Bold);
            using var small = FluentTheme.TextFontPx(11);
            TextRenderer.DrawText(g, "模型分布", title, new Rectangle(rect.Left, rect.Top, rect.Width, 22), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            var max = Math.Max(1, _analysis.ModelTokens.Count == 0 ? 0 : _analysis.ModelTokens.Max(item => item.Tokens));
            var y = rect.Top + 28;
            var visibleRows = Math.Max(3, (rect.Height - 34) / 28);
            foreach (var item in _analysis.ModelTokens.Take(visibleRows))
            {
                var row = new Rectangle(rect.Left, y, rect.Width, 24);
                TextRenderer.DrawText(g, TrimMiddle(item.Model, 24), small, new Rectangle(row.Left, row.Top, 128, row.Height), FluentTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                var bar = new Rectangle(row.Left + 136, row.Top + 8, Math.Max(20, row.Width - 236), 8);
                using var track = new SolidBrush(Color.FromArgb(231, 235, 240));
                using var fill = new SolidBrush(FluentTheme.Accent);
                FillRound(g, track, new RectangleF(bar.Left, bar.Top, bar.Width, bar.Height), 4);
                var width = Math.Max(3, (int)Math.Round(bar.Width * item.Tokens / (double)max));
                FillRound(g, fill, new RectangleF(bar.Left, bar.Top, width, bar.Height), 4);
                TextRenderer.DrawText(g, FormatTokens(item.Tokens), small, new Rectangle(bar.Right + 8, row.Top, 90, row.Height), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                y += 28;
            }
        }

        private void DrawSessionList(Graphics g, Rectangle rect, string titleText, IReadOnlyList<SessionInsight> sessions)
        {
            using var title = FluentTheme.TextFontPx(13, FontStyle.Bold);
            using var small = FluentTheme.TextFontPx(11);
            using var strong = FluentTheme.TextFontPx(11, FontStyle.Bold);
            TextRenderer.DrawText(g, titleText, title, new Rectangle(rect.Left, rect.Top, rect.Width, 22), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            var y = rect.Top + 28;
            var rowHeight = 54;
            var visibleRows = Math.Max(2, (rect.Bottom - y) / rowHeight);
            if (sessions.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无会话记录。", small, new Rectangle(rect.Left, y, rect.Width, 24), FluentTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            foreach (var item in sessions.Take(visibleRows))
            {
                var row = new Rectangle(rect.Left, y, rect.Width, rowHeight - 8);
                DrawRoundedPanel(g, row, Color.FromArgb(248, 250, 252), FluentTheme.StrokeDefault, 6);
                var titleLine = $"{TrimMiddle(item.SessionId, 22)} · {TrimMiddle(item.Model, 20)}";
                var meta = $"{FormatSessionTime(item.StartedAt)} - {FormatSessionTime(item.LastActivityAt)} · {FormatDuration(item.Duration)} · {(item.IsArchived ? "已归档" : "当前")}";
                TextRenderer.DrawText(g, titleLine, strong, new Rectangle(row.Left + 10, row.Top + 5, row.Width - 128, 20), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, FormatTokens(item.TotalTokens), strong, new Rectangle(row.Right - 112, row.Top + 5, 100, 20), FluentTheme.TextPrimary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, meta, small, new Rectangle(row.Left + 10, row.Top + 26, row.Width - 20, 18), FluentTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                y += rowHeight;
            }
        }

        private void DrawRecentSessions(Graphics g, Rectangle rect)
        {
            using var title = FluentTheme.TextFontPx(13, FontStyle.Bold);
            using var small = FluentTheme.TextFontPx(11);
            using var strong = FluentTheme.TextFontPx(11, FontStyle.Bold);
            TextRenderer.DrawText(g, "最近会话", title, new Rectangle(rect.Left, rect.Top, rect.Width, 22), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            var y = rect.Top + 28;
            var visibleRows = Math.Max(3, (rect.Height - 34) / 28);
            foreach (var item in _analysis.RecentSessions.Take(visibleRows))
            {
                var row = new Rectangle(rect.Left, y, rect.Width, 26);
                var name = $"{TrimMiddle(item.SessionId, 16)} · {TrimMiddle(item.Model, 18)}";
                TextRenderer.DrawText(g, name, strong, new Rectangle(row.Left, row.Top, row.Width - 176, row.Height), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, FormatTokens(item.TotalTokens), strong, new Rectangle(row.Right - 168, row.Top, 90, row.Height), FluentTheme.TextPrimary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, FormatTime(item.LastActivityAt), small, new Rectangle(row.Right - 70, row.Top, 70, row.Height), FluentTheme.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                y += 28;
            }
        }

        private string FormatTokens(long tokens)
        {
            return AccountUsageHelpers.FormatTokenCount(tokens, TokenUnitDisplayMode);
        }

        private static string FormatTime(DateTimeOffset? value)
        {
            if (value is null)
            {
                return "--";
            }

            var local = value.Value.ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("M/d", CultureInfo.InvariantCulture);
        }

        private static string FormatSessionTime(DateTimeOffset? value)
        {
            if (value is null)
            {
                return "--";
            }

            var local = value.Value.ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        }

        private static void FillRound(Graphics g, Brush brush, RectangleF rect, float radius)
        {
            using var path = FluentTheme.RoundedRectanglePath(rect, radius);
            g.FillPath(brush, path);
        }
    }

    private sealed class TokenActivityOverviewPanel : Panel
    {
        private enum ActivityView
        {
            Today,
            Week,
            Month,
            AllTime,
        }

        private TokenActivitySummary _activity = TokenActivitySummary.Empty;
        private TokenUnitDisplayMode _tokenUnitDisplayMode = TokenUnitDisplayMode.Chinese;
        private CodexBarOpenAISettings _openAISettings = new();
        private ActivityView _selectedView = ActivityView.Week;
        private Rectangle[] _tabRects = Array.Empty<Rectangle>();
        private readonly ToolTip _chartToolTip = new() { InitialDelay = 140, ReshowDelay = 80, AutoPopDelay = 5000, ShowAlways = true };
        private readonly List<ChartHit> _chartHits = new();
        private int _hoveredChartHit = -1;
        private string? _visibleChartTip;

        private readonly record struct BarPoint(string AxisLabel, string TooltipTitle, long Tokens);

        private readonly record struct ChartHit(RectangleF Bounds, string Tooltip);

        public TokenActivitySummary Activity
        {
            get => _activity;
            set
            {
                _activity = value ?? TokenActivitySummary.Empty;
                Invalidate();
            }
        }

        public TokenUnitDisplayMode TokenUnitDisplayMode
        {
            get => _tokenUnitDisplayMode;
            set
            {
                _tokenUnitDisplayMode = value;
                Invalidate();
            }
        }

        public CodexBarOpenAISettings OpenAISettings
        {
            get => _openAISettings;
            set
            {
                _openAISettings = value ?? new CodexBarOpenAISettings();
                _openAISettings.EnsurePricingDefaults();
                Invalidate();
            }
        }

        public TokenActivityOverviewPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(18);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chartToolTip.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            DrawRoundedPanel(g, ClientRectangle, FluentTheme.CardBackground, FluentTheme.StrokeDefault, 8);

            var bounds = new Rectangle(18, 16, Math.Max(1, Width - 36), Math.Max(1, Height - 32));
            using var title = FluentTheme.TextFontPx(18, FontStyle.Bold);
            using var body = FluentTheme.TextFontPx(12);
            TextRenderer.DrawText(g, "Token 活动", title, new Rectangle(bounds.Left, bounds.Top, 180, 28), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "今日、本周、本月柱状图与所有时间活动热力图", body, new Rectangle(bounds.Left + 112, bounds.Top + 2, bounds.Width - 350, 24), FluentTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            DrawViewTabs(g, new Rectangle(bounds.Right - 292, bounds.Top, 292, 30));

            DrawMetricStrip(g, new Rectangle(bounds.Left, bounds.Top + 38, bounds.Width, 60));
            var chartTop = bounds.Top + 112;
            var chartHeight = Math.Max(132, bounds.Bottom - chartTop);
            _chartHits.Clear();
            DrawSelectedActivity(g, new Rectangle(bounds.Left, chartTop, bounds.Width, chartHeight));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = HitTestChart(e.Location);
            var overTab = _tabRects.Any(rect => rect.Contains(e.Location));
            Cursor = hit >= 0 || overTab ? Cursors.Hand : Cursors.Default;
            if (hit != _hoveredChartHit)
            {
                _hoveredChartHit = hit;
                Invalidate();
            }

            if (hit >= 0)
            {
                var text = _chartHits[hit].Tooltip;
                if (!string.Equals(_visibleChartTip, text, StringComparison.Ordinal))
                {
                    _visibleChartTip = text;
                    _chartToolTip.Show(text, this, e.X + 14, e.Y + 14, 5000);
                }
            }
            else if (_visibleChartTip is not null)
            {
                HideChartTooltip();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredChartHit != -1)
            {
                _hoveredChartHit = -1;
                Invalidate();
            }

            HideChartTooltip();
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (var i = 0; i < _tabRects.Length; i++)
            {
                if (!_tabRects[i].Contains(e.Location))
                {
                    continue;
                }

                _selectedView = i switch
                {
                    0 => ActivityView.Today,
                    1 => ActivityView.Week,
                    2 => ActivityView.Month,
                    _ => ActivityView.AllTime,
                };
                _hoveredChartHit = -1;
                HideChartTooltip();
                Invalidate();
                return;
            }
        }

        private void DrawMetricStrip(Graphics g, Rectangle rect)
        {
            using var bg = new SolidBrush(Color.FromArgb(249, 250, 252));
            using var border = new Pen(FluentTheme.StrokeDefault);
            DrawRoundedPanel(g, rect, Color.FromArgb(249, 250, 252), FluentTheme.StrokeDefault, 8);

            var values = new[]
            {
                (FormatTokenCount(_activity.TotalTokens), "累计 Token 数"),
                (FormatCost(_activity.TotalCostTokens), "累计估值"),
                (FormatTokenCount(_activity.PeakDailyTokens), "峰值 Token 数"),
                (FormatDuration(_activity.LongestTaskDuration), "最长任务时长"),
                ($"{_activity.CurrentStreakDays} 天", "当前连续天数"),
                ($"{_activity.LongestStreakDays} 天", "最长连续天数"),
            };
            using var valueFont = FluentTheme.TextFontPx(17, FontStyle.Regular);
            using var labelFont = FluentTheme.TextFontPx(12);
            var segmentWidth = rect.Width / values.Length;
            for (var i = 0; i < values.Length; i++)
            {
                var segment = new Rectangle(rect.Left + i * segmentWidth, rect.Top + 6, i == values.Length - 1 ? rect.Right - (rect.Left + i * segmentWidth) : segmentWidth, rect.Height - 12);
                if (i > 0)
                {
                    using var sep = new Pen(FluentTheme.StrokeDefault);
                    g.DrawLine(sep, segment.Left, segment.Top + 8, segment.Left, segment.Bottom - 8);
                }

                TextRenderer.DrawText(g, values[i].Item1, valueFont, new Rectangle(segment.Left + 8, segment.Top + 2, segment.Width - 16, 24), FluentTheme.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, values[i].Item2, labelFont, new Rectangle(segment.Left + 8, segment.Top + 28, segment.Width - 16, 22), FluentTheme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawViewTabs(Graphics g, Rectangle rect)
        {
            var labels = new[] { "今日", "本周", "本月", "累计" };
            _tabRects = new Rectangle[labels.Length];
            DrawRoundedPanel(g, rect, Color.FromArgb(245, 247, 250), FluentTheme.StrokeDefault, 6);
            using var font = FluentTheme.TextFontPx(12, FontStyle.Bold);
            var width = rect.Width / labels.Length;
            for (var i = 0; i < labels.Length; i++)
            {
                var item = new Rectangle(rect.Left + i * width + 2, rect.Top + 2, i == labels.Length - 1 ? rect.Right - (rect.Left + i * width) - 4 : width - 4, rect.Height - 4);
                _tabRects[i] = item;
                var selected = _selectedView == (ActivityView)i;
                if (selected)
                {
                    DrawRoundedPanel(g, item, FluentTheme.Accent, Color.Transparent, 5);
                }

                TextRenderer.DrawText(
                    g,
                    labels[i],
                    font,
                    item,
                    selected ? Color.White : FluentTheme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawSelectedActivity(Graphics g, Rectangle rect)
        {
            switch (_selectedView)
            {
                case ActivityView.Today:
                    DrawBarSeries(g, rect, "今日每小时", BuildTodayHourlyBars(), Color.FromArgb(0, 166, 90));
                    break;
                case ActivityView.Month:
                    DrawBarSeries(g, rect, "本月每日", BuildMonthDailyBars(), Color.FromArgb(99, 102, 241));
                    break;
                case ActivityView.AllTime:
                    DrawHeatmap(g, rect);
                    break;
                case ActivityView.Week:
                default:
                    DrawBarSeries(g, rect, "近 7 天每日", BuildWeekDailyBars(), FluentTheme.Accent);
                    break;
            }
        }

        private IReadOnlyList<BarPoint> BuildTodayHourlyBars()
        {
            var byHour = _activity.TodayHourlyTokens.ToDictionary(point => point.Hour.Hour, point => point.Tokens);
            var bars = new List<BarPoint>(24);
            for (var hour = 0; hour < 24; hour++)
            {
                var label = hour % 4 == 0 ? $"{hour}时" : string.Empty;
                var title = $"{hour:00}:00 - {hour:00}:59";
                bars.Add(new BarPoint(label, title, byHour.TryGetValue(hour, out var tokens) ? tokens : 0));
            }

            return bars;
        }

        private IReadOnlyList<BarPoint> BuildWeekDailyBars()
        {
            var byDate = _activity.DailyTokens.ToDictionary(point => point.Date, point => point.Tokens);
            var today = DateTime.Today;
            var start = today.AddDays(-6);
            var bars = new List<BarPoint>(7);
            for (var i = 0; i < 7; i++)
            {
                var date = start.AddDays(i);
                bars.Add(new BarPoint(date.ToString("M/d", CultureInfo.InvariantCulture), date.ToString("M月d日", CultureInfo.InvariantCulture), byDate.TryGetValue(date, out var tokens) ? tokens : 0));
            }

            return bars;
        }

        private IReadOnlyList<BarPoint> BuildMonthDailyBars()
        {
            var byDate = _activity.DailyTokens.ToDictionary(point => point.Date, point => point.Tokens);
            var today = DateTime.Today;
            var days = today.Day;
            var start = new DateTime(today.Year, today.Month, 1);
            var bars = new List<BarPoint>(days);
            for (var i = 0; i < days; i++)
            {
                var date = start.AddDays(i);
                var label = date.Day == 1 || date.Day == days || date.Day % 5 == 0 ? date.Day.ToString(CultureInfo.InvariantCulture) : string.Empty;
                bars.Add(new BarPoint(label, date.ToString("M月d日", CultureInfo.InvariantCulture), byDate.TryGetValue(date, out var tokens) ? tokens : 0));
            }

            return bars;
        }

        private void DrawBarSeries(Graphics g, Rectangle rect, string titleText, IReadOnlyList<BarPoint> bars, Color accent)
        {
            DrawRoundedPanel(g, rect, FluentTheme.SubtleBackground, Color.Transparent, 8);
            using var title = FluentTheme.TextFontPx(14, FontStyle.Bold);
            using var label = FluentTheme.TextFontPx(10);
            using var valueFont = FluentTheme.TextFontPx(11, FontStyle.Bold);

            var total = bars.Sum(item => item.Tokens);
            TextRenderer.DrawText(g, titleText, title, new Rectangle(rect.Left + 14, rect.Top + 10, 180, 22), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, $"合计 {FormatTokenCount(total)}", label, new Rectangle(rect.Right - 200, rect.Top + 12, 186, 20), FluentTheme.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (bars.Count == 0 || total == 0)
            {
                TextRenderer.DrawText(g, "暂无可绘制的 Token 活动。", label, new Rectangle(rect.Left + 14, rect.Top + 48, rect.Width - 28, 24), FluentTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            var max = Math.Max(1, bars.Max(item => item.Tokens));
            var plot = new Rectangle(rect.Left + 24, rect.Top + 48, rect.Width - 48, rect.Height - 78);
            var slot = Math.Max(1, plot.Width / bars.Count);
            var barMaxHeight = Math.Max(12, plot.Height - 26);
            var barWidth = Math.Clamp(slot - 7, 5, bars.Count <= 10 ? 46 : 20);
            using var track = new SolidBrush(Color.FromArgb(230, 235, 241));
            using var brush = new SolidBrush(accent);
            using var gridPen = new Pen(Color.FromArgb(224, 229, 236), 1f);
            using var hoverBrush = new SolidBrush(Color.FromArgb(20, accent));
            using var hoverPen = new Pen(Color.FromArgb(150, accent), 1.4f);

            for (var line = 0; line <= 3; line++)
            {
                var y = plot.Top + (barMaxHeight * line / 3f);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            }

            for (var i = 0; i < bars.Count; i++)
            {
                var barHeight = bars[i].Tokens <= 0 ? 0 : Math.Max(3, (int)Math.Round(barMaxHeight * bars[i].Tokens / (double)max));
                var barLeft = plot.Left + i * slot + (slot - barWidth) / 2;
                var trackRect = new RectangleF(barLeft, plot.Top, barWidth, barMaxHeight);
                var hitRect = new RectangleF(plot.Left + i * slot, plot.Top, slot, barMaxHeight + 24);
                var hitIndex = AddChartHit(hitRect, $"{bars[i].TooltipTitle}\nToken：{FormatTokenCount(bars[i].Tokens)}");
                var hovered = hitIndex == _hoveredChartHit;
                if (hovered)
                {
                    FillRound(g, hoverBrush, hitRect, 6);
                }

                FillRound(g, track, trackRect, Math.Min(5, barWidth / 2f));
                if (barHeight > 0)
                {
                    var valueRect = new RectangleF(barLeft, plot.Bottom - 26 - barHeight, barWidth, barHeight);
                    FillRound(g, brush, valueRect, Math.Min(5, barWidth / 2f));
                    if (hovered)
                    {
                        g.DrawRectangle(hoverPen, valueRect.X, valueRect.Y, valueRect.Width, valueRect.Height);
                    }
                }

                if (!string.IsNullOrEmpty(bars[i].AxisLabel))
                {
                    TextRenderer.DrawText(
                        g,
                        bars[i].AxisLabel,
                        label,
                        new Rectangle(plot.Left + i * slot, plot.Bottom - 20, slot, 18),
                        hovered ? accent : FluentTheme.TextSecondary,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }

            var peak = bars.OrderByDescending(item => item.Tokens).First();
            TextRenderer.DrawText(
                g,
                $"峰值 {FormatTokenCount(peak.Tokens)}",
                valueFont,
                new Rectangle(rect.Left + 14, rect.Bottom - 26, rect.Width - 28, 18),
                FluentTheme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawHeatmap(Graphics g, Rectangle rect)
        {
            DrawRoundedPanel(g, rect, FluentTheme.SubtleBackground, Color.Transparent, 8);
            using var title = FluentTheme.TextFontPx(14, FontStyle.Bold);
            using var small = FluentTheme.TextFontPx(11);
            TextRenderer.DrawText(g, "所有时间", title, new Rectangle(rect.Left + 14, rect.Top + 10, 130, 22), FluentTheme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            var rangeText = _activity.DailyTokens.Count == 0
                ? "暂无 session 记录"
                : $"{_activity.DailyTokens.First().Date:M月d日} - {DateTime.Today:M月d日}";
            TextRenderer.DrawText(g, rangeText, small, new Rectangle(rect.Right - 220, rect.Top + 12, 206, 20), FluentTheme.TextTertiary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (_activity.DailyTokens.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无可绘制的 Token 活动。", small, new Rectangle(rect.Left + 14, rect.Top + 46, rect.Width - 28, 24), FluentTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            var byDate = _activity.DailyTokens.ToDictionary(point => point.Date, point => point.Tokens);
            var first = _activity.DailyTokens.First().Date;
            var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
            var end = DateTime.Today;
            var days = Math.Max(1, (end - start).Days + 1);
            var columns = (int)Math.Ceiling(days / 7d);
            var area = new Rectangle(rect.Left + 32, rect.Top + 42, rect.Width - 50, rect.Height - 48);
            var gap = columns > 90 ? 2 : 4;
            var cell = Math.Max(5, Math.Min(22, Math.Min((area.Width - Math.Max(0, columns - 1) * gap) / Math.Max(1, columns), (area.Height - 6 * gap) / 7)));
            var actualWidth = columns * cell + Math.Max(0, columns - 1) * gap;
            var x0 = area.Left + Math.Max(0, (area.Width - actualWidth) / 2);
            var max = Math.Max(1, _activity.PeakDailyTokens);

            using var empty = new SolidBrush(Color.FromArgb(235, 237, 240));
            using var hoverPen = new Pen(FluentTheme.Accent, 1.4f);
            DrawWeekdayLabels(g, area);
            for (var i = 0; i < days; i++)
            {
                var date = start.AddDays(i);
                var col = i / 7;
                var row = i % 7;
                var x = x0 + col * (cell + gap);
                var y = area.Top + row * (cell + gap);
                if (x + cell < area.Left || x > area.Right)
                {
                    continue;
                }

                var tokens = byDate.TryGetValue(date, out var value) ? value : 0;
                using var brush = new SolidBrush(HeatColor(tokens, max));
                var cellRect = new RectangleF(x, y, cell, cell);
                var hitIndex = AddChartHit(RectangleF.Inflate(cellRect, Math.Max(2, gap), Math.Max(2, gap)), $"{date:M月d日}\nToken：{FormatTokenCount(tokens)}");
                FillRound(g, tokens > 0 ? brush : empty, cellRect, Math.Max(1, cell / 3f));
                if (hitIndex == _hoveredChartHit)
                {
                    g.DrawRectangle(hoverPen, cellRect.X - 1, cellRect.Y - 1, cellRect.Width + 2, cellRect.Height + 2);
                }
            }

            DrawMonthLabels(g, area, start, end, x0, cell, gap);
            DrawHeatLegend(g, new Rectangle(rect.Right - 188, rect.Bottom - 28, 170, 18), max);
        }

        private int AddChartHit(RectangleF bounds, string tooltip)
        {
            _chartHits.Add(new ChartHit(bounds, tooltip));
            return _chartHits.Count - 1;
        }

        private int HitTestChart(Point point)
        {
            for (var i = _chartHits.Count - 1; i >= 0; i--)
            {
                if (_chartHits[i].Bounds.Contains(point))
                {
                    return i;
                }
            }

            return -1;
        }

        private void HideChartTooltip()
        {
            _visibleChartTip = null;
            _chartToolTip.Hide(this);
        }

        private static void DrawWeekdayLabels(Graphics g, Rectangle area)
        {
            using var font = FluentTheme.TextFontPx(10);
            var labels = new[] { "一", "", "三", "", "五", "", "日" };
            var availableHeight = area.Height;
            var cellStep = availableHeight / 7f;
            for (var i = 0; i < labels.Length; i++)
            {
                if (string.IsNullOrEmpty(labels[i]))
                {
                    continue;
                }

                TextRenderer.DrawText(
                    g,
                    labels[i],
                    font,
                    new Rectangle(area.Left - 28, (int)Math.Round(area.Top + i * cellStep - 1), 20, 16),
                    FluentTheme.TextTertiary,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawHeatLegend(Graphics g, Rectangle rect, long max)
        {
            using var font = FluentTheme.TextFontPx(10);
            TextRenderer.DrawText(g, "少", font, new Rectangle(rect.Left, rect.Top, 22, rect.Height), FluentTheme.TextTertiary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            var x = rect.Left + 26;
            var colors = new[]
            {
                Color.FromArgb(235, 237, 240),
                HeatColor(Math.Max(1, max / 5), max),
                HeatColor(Math.Max(1, max / 3), max),
                HeatColor(Math.Max(1, max * 2 / 3), max),
                HeatColor(max, max),
            };
            for (var i = 0; i < colors.Length; i++)
            {
                using var brush = new SolidBrush(colors[i]);
                FillRound(g, brush, new RectangleF(x + i * 16, rect.Top + 4, 11, 11), 3);
            }

            TextRenderer.DrawText(g, "多", font, new Rectangle(x + 82, rect.Top, 30, rect.Height), FluentTheme.TextTertiary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private static void DrawMonthLabels(Graphics g, Rectangle area, DateTime start, DateTime end, int x0, int cell, int gap)
        {
            using var font = FluentTheme.TextFontPx(10);
            var currentMonth = new DateTime(start.Year, start.Month, 1);
            while (currentMonth <= end)
            {
                var delta = Math.Max(0, (currentMonth - start).Days);
                var col = delta / 7;
                var x = x0 + col * (cell + gap);
                if (x >= area.Left && x < area.Right - 28)
                {
                    TextRenderer.DrawText(g, $"{currentMonth.Month}月", font, new Rectangle(x, area.Bottom + 8, 36, 18), FluentTheme.TextTertiary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }

                currentMonth = currentMonth.AddMonths(1);
            }
        }

        private static Color HeatColor(long tokens, long max)
        {
            if (tokens <= 0) return Color.FromArgb(235, 237, 240);
            var ratio = Math.Clamp(tokens / (double)max, 0, 1);
            if (ratio >= 0.75) return Color.FromArgb(37, 99, 235);
            if (ratio >= 0.5) return Color.FromArgb(96, 165, 250);
            if (ratio >= 0.25) return Color.FromArgb(147, 197, 253);
            return Color.FromArgb(191, 219, 254);
        }

        private string FormatTokenCount(long tokens)
        {
            return AccountUsageHelpers.FormatTokenCount(tokens, TokenUnitDisplayMode);
        }

        private string FormatCost(TokenCostBreakdown tokens)
        {
            var preset = OpenAISettings.GetOrCreateTokenPricePreset();
            var usd = preset.EstimateUsd(tokens);
            var cny = usd * Math.Max(0.1, OpenAISettings.UsdToCnyRate);
            return $"${FormatMoney(usd)} / ¥{FormatMoney(cny)}";
        }

        private static string FormatMoney(double value)
        {
            if (value >= 1000) return value.ToString("N0", CultureInfo.InvariantCulture);
            if (value >= 10) return value.ToString("N2", CultureInfo.InvariantCulture);
            if (value >= 1) return value.ToString("N3", CultureInfo.InvariantCulture);
            return value.ToString("N4", CultureInfo.InvariantCulture);
        }

        private static void FillRound(Graphics g, Brush brush, RectangleF rect, float radius)
        {
            using var path = FluentTheme.RoundedRectanglePath(rect, radius);
            g.FillPath(brush, path);
        }
    }
}
