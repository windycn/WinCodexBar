using CodexBarWin.Models;
using CodexBarWin.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexBarWin.UI;

public sealed class TrayPopupForm : Form
{
    private enum HitStyle { None, IconButton, IconButtonFlat, AccentButton, NeutralButton, AccountRow, RefreshGlyph, DeleteGlyph, CloseGlyph, SegmentTab }

    private const float PopupLogicalWidth = 440f;
    private const float AccountListTop = 250f;
    private const float AccountListLeft = 16f;
    private const float AccountListWidth = 408f;
    private const float AccountRowHeight = 60f;
    private const float AccountRowGap = 8f;

    private sealed class Hit
    {
        public RectangleF Bounds;
        public Action Action = () => { };
        public string? Tooltip;
        public HitStyle Style;
        public int Id;
        public bool CloseAfter;
    }

    private IReadOnlyList<TokenAccount> _accounts;
    private string? _activeAccountId;
    private readonly Func<bool> _isKeepAwakeEnabled;
    private TokenUsageSummary _tokenSummary;
    private CodexRadarPrediction _radarPrediction = CodexRadarPrediction.Unavailable;
    private CodexBarConfig _config;
    private readonly Action<TokenAccount> _activateAccount;
    private readonly Action _openDashboard;
    private readonly Action _showUsage;
    private readonly Action _addAccount;
    private readonly Action _importAccounts;
    private readonly Action _exportAccounts;
    private readonly Action _openConfig;
    private readonly Action<AccountUsageMode> _setAccountUsageMode;
    private readonly Action _toggleKeepAwake;
    private readonly Action<TokenAccount> _refreshAccount;
    private readonly Action<TokenAccount> _deleteAccount;
    private readonly Action _exit;
    private readonly List<Hit> _hits = new();
    private readonly ToolTip _toolTip = new() { InitialDelay = 320, ReshowDelay = 120, AutoPopDelay = 4000, ShowAlways = true };
    private readonly System.Windows.Forms.Timer _refreshAnimationTimer = new() { Interval = 24 };
    private string? _visibleTooltip;
    private float _scale = 1f;
    private float _scrollOffset;
    private float _maxScroll;
    private float _refreshRotation;
    private string? _refreshingAccountId;
    private DateTime _refreshAnimationStartedAt;
    private bool _refreshStopRequested;
    private bool _keepOpenForAction;
    private int _hoveredId = -1;
    private int _pressedId = -1;
    private int _nextHitId;
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private LowLevelMouseProc? _mouseHookProc;
    private bool _closingFromHook;

    public TrayPopupForm(
        IReadOnlyList<TokenAccount> accounts,
        string? activeAccountId,
        Func<bool> isKeepAwakeEnabled,
        TokenUsageSummary tokenSummary,
        CodexRadarPrediction radarPrediction,
        CodexBarConfig config,
        Action<TokenAccount> activateAccount,
        Action openDashboard,
        Action showUsage,
        Action addAccount,
        Action importAccounts,
        Action exportAccounts,
        Action openConfig,
        Action<AccountUsageMode> setAccountUsageMode,
        Action toggleKeepAwake,
        Action<TokenAccount> refreshAccount,
        Action<TokenAccount> deleteAccount,
        Action exit)
    {
        _accounts = accounts;
        _activeAccountId = activeAccountId;
        _isKeepAwakeEnabled = isKeepAwakeEnabled;
        _tokenSummary = tokenSummary;
        _radarPrediction = radarPrediction;
        _config = config ?? new CodexBarConfig();
        _activateAccount = activateAccount;
        _openDashboard = openDashboard;
        _showUsage = showUsage;
        _addAccount = addAccount;
        _importAccounts = importAccounts;
        _exportAccounts = exportAccounts;
        _openConfig = openConfig;
        _setAccountUsageMode = setAccountUsageMode;
        _toggleKeepAwake = toggleKeepAwake;
        _refreshAccount = refreshAccount;
        _deleteAccount = deleteAccount;
        _exit = exit;

        Text = "WinCodexBar";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = FluentTheme.LayerBackground;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _refreshAnimationTimer.Tick += (_, _) => AdvanceRefreshAnimation();
    }

    public void ShowNearCursor()
    {
        _scale = Math.Max(1f, DeviceDpi / 96f);
        Size = DpiSize(PopupLogicalWidth, PopupLogicalHeight());

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Min(Math.Max(Cursor.Position.X - Width + Dpi(40), screen.Left + Dpi(8)), screen.Right - Width - Dpi(8));
        var y = Math.Min(Math.Max(Cursor.Position.Y - Height - Dpi(8), screen.Top + Dpi(8)), screen.Bottom - Height - Dpi(8));
        Location = new Point(x, y);
        Show();
        Activate();
        InstallMouseHook();
    }

    public void UpdateSnapshot(
        string? activeAccountId,
        TokenUsageSummary? tokenSummary,
        CodexBarConfig config,
        IReadOnlyList<TokenAccount>? accounts = null)
    {
        if (accounts is not null)
        {
            _accounts = accounts;
        }
        _activeAccountId = activeAccountId;
        if (tokenSummary is not null)
        {
            _tokenSummary = tokenSummary.Value;
        }
        _config = config ?? new CodexBarConfig();
        if (_refreshingAccountId is not null)
        {
            _refreshStopRequested = true;
        }
        Invalidate();
    }

    public void UpdateRadarPrediction(CodexRadarPrediction prediction)
    {
        _radarPrediction = prediction;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UninstallMouseHook();
            _toolTip.Dispose();
            _refreshAnimationTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MicaSupport.TryApply(this, transient: true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        MicaSupport.ApplyRoundedRegion(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UninstallMouseHook();
        base.OnFormClosing(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var hit = HitAt(e.Location);
        _pressedId = hit?.Id ?? -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var hit = HitAt(e.Location);
        var pressedId = _pressedId;
        _pressedId = -1;
        Invalidate();
        if (hit is not null && hit.Id == pressedId)
        {
            _keepOpenForAction = !hit.CloseAfter;
            try
            {
                hit.Action();
            }
            catch (Exception ex)
            {
                AppLogService.LogException(ex, $"Tray popup action: {hit.Tooltip ?? hit.Style.ToString()}");
            }
            finally
            {
                if (hit.CloseAfter)
                {
                    Close();
                }
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitAt(e.Location);
        var newId = hit?.Id ?? -1;
        Cursor = hit is not null ? Cursors.Hand : Cursors.Default;
        if (newId != _hoveredId)
        {
            _hoveredId = newId;
            Invalidate();
        }

        var tooltip = hit?.Tooltip;
        if (tooltip == _visibleTooltip)
        {
            return;
        }

        _visibleTooltip = tooltip;
        _toolTip.Hide(this);
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            _toolTip.Show(tooltip, this, e.Location.X + Dpi(12), e.Location.Y + Dpi(18));
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _visibleTooltip = null;
        _toolTip.Hide(this);
        Cursor = Cursors.Default;
        if (_hoveredId != -1 || _pressedId != -1)
        {
            _hoveredId = -1;
            _pressedId = -1;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_maxScroll <= 0)
        {
            return;
        }

        _scrollOffset = Math.Clamp(_scrollOffset - Math.Sign(e.Delta) * 42, 0, _maxScroll);
        Invalidate();
    }

    private Hit? HitAt(PointF point)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Bounds.Contains(point))
            {
                return _hits[i];
            }
        }
        return null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _hits.Clear();
        _nextHitId = 0;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawShell(g);
        var active = ActiveAccount();
        DrawHeader(g, active);
        DrawUsageOverview(g, active);
        DrawAccountSection(g);
        DrawActions(g);
    }

    private void DrawShell(Graphics g)
    {
        var logicalHeight = PopupLogicalHeight();
        var bounds = Rect(0, 0, PopupLogicalWidth, logicalHeight);
        using var bg = new LinearGradientBrush(
            bounds,
            Color.FromArgb(246, 252, 252, 252),
            Color.FromArgb(232, 238, 242, 248),
            LinearGradientMode.Vertical);
        FillRound(g, bg, bounds, DpiF(8));

        using (var bloom = new SolidBrush(Color.FromArgb(92, 255, 255, 255)))
        {
            FillRound(g, bloom, Rect(10, 10, 420, 116), DpiF(7));
        }

        using (var shade = new LinearGradientBrush(
            Rect(0, logicalHeight - 128, PopupLogicalWidth, 128),
            Color.FromArgb(0, 255, 255, 255),
            Color.FromArgb(42, 214, 222, 232),
            LinearGradientMode.Vertical))
        {
            FillRound(g, shade, Rect(0, logicalHeight - 128, PopupLogicalWidth, 128), DpiF(8));
        }

        using var outer = new Pen(Color.FromArgb(160, 134, 144, 156), Dpi(1));
        DrawRound(g, outer, Rect(0.5f, 0.5f, PopupLogicalWidth - 1, logicalHeight - 1), DpiF(8));
        using var inner = new Pen(Color.FromArgb(210, 255, 255, 255), Dpi(1));
        DrawRound(g, inner, Rect(1.5f, 1.5f, PopupLogicalWidth - 3, logicalHeight - 3), DpiF(7));
    }

    private void DrawHeader(Graphics g, TokenAccount? active)
    {
        using var titleFont = FontPx(20, FontStyle.Bold);
        using var metaFont = FontPx(12, FontStyle.Regular);

        DrawText(g, "WinCodexBar", titleFont, FluentTheme.TextPrimary, Rect(20, 18, 260, 32));

        var close = Rect(396, 18, 28, 28);
        var closeId = AddHit(close, Close, "关闭", HitStyle.CloseGlyph, closeAfter: true);
        DrawCloseGlyph(g, close, closeId);

        var accountText = active is null ? "当前账号：未激活" : $"当前账号：{TrimMiddle(BuildAccountLabel(active), 28)}";
        DrawText(g, accountText, metaFont, FluentTheme.TextSecondary, Rect(20, 54, 270, 20));
        DrawRadarPrediction(g, Rect(20, 73, 270, 18));
        DrawSegmented(g, Rect(294, 54, 130, 26));
    }

    private void DrawRadarPrediction(Graphics g, RectangleF rect)
    {
        if (!_radarPrediction.IsAvailable || string.IsNullOrWhiteSpace(_radarPrediction.DisplayText))
        {
            return;
        }

        var hitId = AddHit(
            rect,
            () => OpenUrl("https://codexradar.com/"),
            "打开 Codex Radar",
            HitStyle.IconButtonFlat,
            closeAfter: false);
        using var font = FontPx(9, FontStyle.Regular);
        var color = _hoveredId == hitId ? FluentTheme.Accent : FluentTheme.TextTertiary;
        DrawText(g, _radarPrediction.DisplayText, font, color, rect);
    }

    private void DrawCloseGlyph(Graphics g, RectangleF rect, int hitId)
    {
        var hovered = _hoveredId == hitId;
        var pressed = _pressedId == hitId;
        if (hovered || pressed)
        {
            using var bg = new SolidBrush(pressed ? Color.FromArgb(196, 49, 75) : Color.FromArgb(232, 17, 35));
            FillRound(g, bg, rect, DpiF(4));
            using var pen = new Pen(Color.White, Dpi(2));
            g.DrawLine(pen, rect.Left + DpiF(9), rect.Top + DpiF(9), rect.Right - DpiF(9), rect.Bottom - DpiF(9));
            g.DrawLine(pen, rect.Right - DpiF(9), rect.Top + DpiF(9), rect.Left + DpiF(9), rect.Bottom - DpiF(9));
        }
        else
        {
            using var pen = new Pen(FluentTheme.TextSecondary, Dpi(1.5f));
            g.DrawLine(pen, rect.Left + DpiF(9), rect.Top + DpiF(9), rect.Right - DpiF(9), rect.Bottom - DpiF(9));
            g.DrawLine(pen, rect.Right - DpiF(9), rect.Top + DpiF(9), rect.Left + DpiF(9), rect.Bottom - DpiF(9));
        }
    }

    private void DrawSegmented(Graphics g, RectangleF rect)
    {
        using var bg = new SolidBrush(Color.FromArgb(238, 238, 238));
        FillRound(g, bg, rect, DpiF(6));
        using var edge = new Pen(FluentTheme.StrokeDefault, Dpi(1));
        DrawRound(g, edge, rect, DpiF(6));

        var leftRect = new RectangleF(rect.Left + DpiF(2), rect.Top + DpiF(2), rect.Width / 2 - DpiF(2), rect.Height - DpiF(4));
        var rightRect = new RectangleF(rect.Left + rect.Width / 2, rect.Top + DpiF(2), rect.Width / 2 - DpiF(2), rect.Height - DpiF(4));

        var manualSelected = _config.OpenAI.AccountUsageMode != AccountUsageMode.AggregateGateway;
        DrawSegmentTab(g, leftRect, "手动", manualSelected, () => ChangeAccountUsageMode(AccountUsageMode.Switch));
        DrawSegmentTab(g, rightRect, "聚合", !manualSelected, () => ChangeAccountUsageMode(AccountUsageMode.AggregateGateway));
    }

    private void DrawSegmentTab(Graphics g, RectangleF rect, string label, bool selected, Action onClick)
    {
        var id = AddHit(rect, onClick, "切换账号使用模式", HitStyle.SegmentTab);
        var hovered = _hoveredId == id;
        if (selected)
        {
            using var bg = new SolidBrush(FluentTheme.Accent);
            FillRound(g, bg, rect, DpiF(4));
        }
        else if (hovered)
        {
            using var bg = new SolidBrush(Color.FromArgb(229, 229, 229));
            FillRound(g, bg, rect, DpiF(4));
        }

        using var font = FontPx(11, FontStyle.Bold);
        DrawText(g, label, font, selected ? Color.White : FluentTheme.TextSecondary, rect, center: true);
    }

    private void ChangeAccountUsageMode(AccountUsageMode mode)
    {
        if (_config.OpenAI.AccountUsageMode == mode)
        {
            return;
        }

        _config.OpenAI.AccountUsageMode = mode;
        _setAccountUsageMode(mode);
        Invalidate();
    }

    private void ToggleKeepAwakeInPlace()
    {
        _toggleKeepAwake();
        _toolTip.Hide(this);
        _visibleTooltip = _isKeepAwakeEnabled() ? "解除保持唤醒" : "锁定保持唤醒";
        Invalidate();
        var point = PointToClient(Cursor.Position);
        if (ClientRectangle.Contains(point))
        {
            _toolTip.Show(_visibleTooltip, this, point.X + Dpi(12), point.Y + Dpi(18));
        }
    }

    private void DrawUsageOverview(Graphics g, TokenAccount? active)
    {
        var card = Rect(16, 100, 408, 112);
        using var cardBg = new SolidBrush(FluentTheme.CardBackground);
        using var cardEdge = new Pen(FluentTheme.StrokeDefault, Dpi(1));
        FillRound(g, cardBg, card, DpiF(6));
        DrawRound(g, cardEdge, card, DpiF(6));

        using var strong = FontPx(15, FontStyle.Bold);
        using var body = FontPx(11, FontStyle.Regular);
        using var small = FontPx(10, FontStyle.Regular);

        if (active is null)
        {
            DrawText(g, "暂无可用账号", strong, FluentTheme.TextPrimary, Rect(28, 116, 360, 26));
            DrawText(g, "添加或导入 OpenAI 账号后显示套餐、额度与健康度。", body, FluentTheme.TextSecondary, Rect(28, 144, 360, 22));
            return;
        }

        var fiveHour = Clamp(active.PrimaryUsedPercent);
        var sevenDay = Clamp(active.SecondaryUsedPercent);
        var modeLabel = _config.OpenAI.UsageDisplayMode == UsageDisplayMode.Remaining ? "剩余" : "已用";
        var fiveDisplay = AccountUsageHelpers.DisplayPercent(fiveHour, _config.OpenAI.UsageDisplayMode);
        var sevenDisplay = AccountUsageHelpers.DisplayPercent(sevenDay, _config.OpenAI.UsageDisplayMode);
        var planLabel = AccountUsageHelpers.PlanLabel(active);
        DrawPill(g, "OpenAI", Rect(28, 111, 58, 22), FluentTheme.Accent, Color.FromArgb(229, 240, 252), small);
        DrawText(g, $"{planLabel} 订阅", strong, FluentTheme.TextPrimary, Rect(94, 110, 110, 24));
        DrawText(g, $"5小时 {modeLabel} {fiveDisplay:F1}%   ·   7天 {modeLabel} {sevenDisplay:F1}%", body, FluentTheme.TextSecondary, Rect(190, 112, 206, 22), right: true);
        DrawText(g, $"5h {ResetDetail(active.PrimaryResetAt)}", small, FluentTheme.TextTertiary, Rect(28, 136, 180, 18));
        DrawText(g, $"7d {ResetDetail(active.SecondaryResetAt)}", small, FluentTheme.TextTertiary, Rect(216, 136, 180, 18));
        DrawTokenUsageStrip(g, Rect(28, 156, 368, 22));

        DrawUsageBar(g, Rect(28, 188, 176, 6), fiveHour);
        DrawUsageBar(g, Rect(220, 188, 176, 6), sevenDay);
        DrawText(g, "5小时", small, FluentTheme.TextTertiary, Rect(28, 196, 60, 14));
        DrawText(g, "7天", small, FluentTheme.TextTertiary, Rect(220, 196, 60, 14));
    }

    private void DrawTokenUsageStrip(Graphics g, RectangleF rect)
    {
        var colWidth = rect.Width / 3f;
        DrawTokenMetric(g, "今日", _tokenSummary.TodayTokens, new RectangleF(rect.Left, rect.Top, colWidth - DpiF(6), rect.Height));
        DrawTokenMetric(g, "本周", _tokenSummary.ThisWeekTokens, new RectangleF(rect.Left + colWidth, rect.Top, colWidth - DpiF(6), rect.Height));
        DrawTokenMetric(g, "本月", _tokenSummary.ThisMonthTokens, new RectangleF(rect.Left + colWidth * 2, rect.Top, colWidth, rect.Height));
    }

    private void DrawTokenMetric(Graphics g, string label, long tokens, RectangleF rect)
    {
        using var labelFont = FontPx(9, FontStyle.Regular);
        using var valueFont = FontPx(10, FontStyle.Bold);
        using var bg = new SolidBrush(Color.FromArgb(245, 248, 252));
        FillRound(g, bg, rect, DpiF(4));
        DrawText(g, label, labelFont, FluentTheme.TextTertiary, new RectangleF(rect.Left + DpiF(8), rect.Top, DpiF(34), rect.Height));
        DrawText(g, FormatTokenCount(tokens), valueFont, FluentTheme.TextPrimary, new RectangleF(rect.Left + DpiF(40), rect.Top, rect.Width - DpiF(48), rect.Height), right: true);
    }

    private void DrawAccountSection(Graphics g)
    {
        using var heading = FontPx(13, FontStyle.Bold);
        using var note = FontPx(11, FontStyle.Regular);
        var healthy = _accounts.Count(a => HealthValue(a) < 70);
        var warning = _accounts.Count(a => HealthValue(a) >= 70 && HealthValue(a) < 90);
        var danger = _accounts.Count(a => HealthValue(a) >= 90);

        DrawText(g, "账号信息", heading, FluentTheme.TextPrimary, Rect(20, 222, 92, 22));
        DrawText(g, $"健康 {healthy} · 警戒 {warning} · 高负载 {danger}", note, FluentTheme.TextSecondary, Rect(112, 223, 220, 22));
        using var pillFont = FontPx(10, FontStyle.Bold);
        DrawPill(g, $"{_accounts.Count} 个", Rect(370, 223, 50, 22), FluentTheme.Accent, Color.FromArgb(229, 240, 252), pillFont);

        if (_accounts.Count == 0)
        {
            DrawEmptyAccounts(g, AccountListTop, AccountViewportHeight());
            return;
        }

        DrawScrollableAccounts(g, AccountListLeft, AccountListTop, AccountListWidth, AccountViewportHeight());
    }

    private void DrawScrollableAccounts(Graphics g, float x, float y, float width, float height)
    {
        var viewport = Rect(x, y, width, height);
        var ordered = SortedAccounts().ToArray();
        var totalHeight = ordered.Length * AccountRowHeight + Math.Max(0, ordered.Length - 1) * AccountRowGap;
        _maxScroll = Math.Max(0, totalHeight - height);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, _maxScroll);

        var state = g.Save();
        g.SetClip(viewport);
        var rowY = y - _scrollOffset;
        foreach (var account in ordered)
        {
            if (rowY + AccountRowHeight >= y && rowY <= y + height)
            {
                DrawAccountRow(g, account, rowY);
            }
            rowY += AccountRowHeight + AccountRowGap;
        }
        g.Restore(state);

        if (_maxScroll > 0)
        {
            DrawScrollBar(g, x + width - 4, y + 4, height - 8, height, totalHeight);
        }
    }

    private void DrawScrollBar(Graphics g, float x, float y, float height, float logicalViewportHeight, float totalHeight)
    {
        var track = Rect(x, y, 3, height);
        using var trackBrush = new SolidBrush(FluentTheme.StrokeDefault);
        FillRound(g, trackBrush, track, DpiF(2));

        var thumbHeight = Math.Max(34, height * logicalViewportHeight / totalHeight);
        var thumbTop = y + (height - thumbHeight) * (_scrollOffset / _maxScroll);
        using var thumbBrush = new SolidBrush(FluentTheme.TextTertiary);
        FillRound(g, thumbBrush, Rect(x, thumbTop, 3, thumbHeight), DpiF(2));
    }

    private void DrawEmptyAccounts(Graphics g, float y, float height)
    {
        var box = Rect(20, y + 6, 400, height - 12);
        using var bg = new SolidBrush(FluentTheme.CardBackground);
        using var edge = new Pen(FluentTheme.StrokeDefault, Dpi(1));
        FillRound(g, bg, box, DpiF(6));
        DrawRound(g, edge, box, DpiF(6));

        using var title = FontPx(14, FontStyle.Bold);
        using var body = FontPx(11, FontStyle.Regular);
        DrawText(g, "还没有导入账号", title, FluentTheme.TextPrimary, Rect(40, y + 18, 320, 24));
        DrawText(g, "可以添加 OAuth 账号，也可以导入兼容的 JSON / CSV 文件。", body, FluentTheme.TextSecondary, Rect(40, y + 46, 340, 22));

        var importRect = Rect(40, y + 76, 96, 30);
        var hitId = AddHit(importRect, () => { _addAccount(); }, "添加账号", HitStyle.AccentButton, closeAfter: true);
        DrawAccentButton(g, importRect, "添加账号", hitId);
    }

    private void DrawAccountRow(Graphics g, TokenAccount account, float y)
    {
        var active = string.Equals(account.AccountId, _activeAccountId, StringComparison.Ordinal);
        var row = Rect(16, y, 400, 60);
        var rowId = AddHit(row, () => { _keepOpenForAction = true; _activateAccount(account); }, active ? "当前账号" : "切换到此账号", HitStyle.AccountRow);
        var hovered = _hoveredId == rowId;
        var pressed = _pressedId == rowId;
        var health = HealthValue(account);
        var fiveHour = Clamp(account.PrimaryUsedPercent);
        var sevenDay = Clamp(account.SecondaryUsedPercent);

        Color bgColor;
        if (active)
        {
            bgColor = Color.FromArgb(229, 240, 252);
        }
        else if (pressed)
        {
            bgColor = Color.FromArgb(235, 235, 235);
        }
        else if (hovered)
        {
            bgColor = Color.FromArgb(245, 245, 245);
        }
        else
        {
            bgColor = FluentTheme.CardBackground;
        }

        using (var bg = new SolidBrush(bgColor))
        {
            FillRound(g, bg, row, DpiF(6));
        }
        using (var edge = new Pen(active ? FluentTheme.Accent : FluentTheme.StrokeDefault, Dpi(1)))
        {
            DrawRound(g, edge, row, DpiF(6));
        }

        if (active)
        {
            using var accent = new SolidBrush(FluentTheme.Accent);
            FillRound(g, accent, Rect(16, y + 14, 4, 32), DpiF(2));
        }

        using var dot = new SolidBrush(UsageColor(health));
        g.FillEllipse(dot, Rect(34, y + 25, 10, 10));

        using var name = FontPx(12, FontStyle.Bold);
        using var meta = FontPx(10, FontStyle.Regular);
        using var chip = FontPx(9, FontStyle.Bold);

        DrawText(g, TrimMiddle(BuildAccountLabel(account), 28), name, FluentTheme.TextPrimary, Rect(54, y + 8, 220, 20));
        DrawPill(g, AccountUsageHelpers.PlanLabel(account), Rect(280, y + 9, 56, 18), PlanColor(account), Color.FromArgb(238, 238, 238), chip);
        DrawText(g, AccountUsageHelpers.HealthLabel(AccountUsageHelpers.Health(account, _config.OpenAI.WarningThresholdPercent, _config.OpenAI.DangerThresholdPercent)), meta, UsageColor(health), Rect(54, y + 32, 80, 18));
        var fiveText = AccountUsageHelpers.DisplayPercent(fiveHour, _config.OpenAI.UsageDisplayMode);
        var sevenText = AccountUsageHelpers.DisplayPercent(sevenDay, _config.OpenAI.UsageDisplayMode);
        var prefix = _config.OpenAI.UsageDisplayMode == UsageDisplayMode.Remaining ? "剩" : "用";
        DrawText(g, $"5h{prefix} {fiveText:F0}%", meta, UsageColor(fiveHour), Rect(140, y + 32, 70, 18));
        DrawText(g, $"7d{prefix} {sevenText:F0}%", meta, UsageColor(sevenDay), Rect(212, y + 32, 70, 18));
        DrawText(g, ResetHint(account.PrimaryResetAt), meta, FluentTheme.TextTertiary, Rect(286, y + 32, 50, 18));

        var refresh = Rect(342, y + 14, 26, 30);
        var refreshId = AddHit(refresh, () => { _keepOpenForAction = true; StartRefreshAnimation(account); _refreshAccount(account); }, "刷新此账号用量", HitStyle.RefreshGlyph);
        DrawRefreshGlyph(g, refresh, refreshId, account);

        var delete = Rect(374, y + 14, 26, 30);
        var deleteId = AddHit(delete, () => { _keepOpenForAction = true; _deleteAccount(account); }, "删除此账号", HitStyle.DeleteGlyph);
        DrawDeleteGlyph(g, delete, deleteId);
    }

    private void DrawRefreshGlyph(Graphics g, RectangleF rect, int hitId, TokenAccount account)
    {
        var hovered = _hoveredId == hitId;
        var pressed = _pressedId == hitId;
        var spinning = string.Equals(_refreshingAccountId, account.AccountId, StringComparison.Ordinal);
        Color bg;
        if (pressed) bg = FluentTheme.ControlBackgroundPressed;
        else if (hovered) bg = FluentTheme.ControlBackgroundHover;
        else bg = Color.Transparent;
        if (bg.A != 0)
        {
            using var b = new SolidBrush(bg);
            FillRound(g, b, rect, DpiF(4));
        }
        using var font = FluentTheme.IconFontPx(DpiF(14));
        if (!spinning)
        {
            DrawText(g, FluentIcons.Refresh, font, FluentTheme.TextSecondary, rect, center: true);
            return;
        }

        var state = g.Save();
        g.TranslateTransform(rect.Left + rect.Width / 2f, rect.Top + rect.Height / 2f);
        g.RotateTransform(_refreshRotation);
        DrawText(g, FluentIcons.Refresh, font, FluentTheme.Accent, new RectangleF(-rect.Width / 2f, -rect.Height / 2f, rect.Width, rect.Height), center: true);
        g.Restore(state);
    }

    private void StartRefreshAnimation(TokenAccount account)
    {
        _refreshingAccountId = account.AccountId;
        _refreshRotation = 0;
        _refreshStopRequested = false;
        _refreshAnimationStartedAt = DateTime.UtcNow;
        if (!_refreshAnimationTimer.Enabled)
        {
            _refreshAnimationTimer.Start();
        }
        Invalidate();
    }

    private void AdvanceRefreshAnimation()
    {
        _refreshRotation = (_refreshRotation + 18f) % 360f;
        var elapsed = DateTime.UtcNow - _refreshAnimationStartedAt;
        if ((_refreshStopRequested && elapsed.TotalMilliseconds >= 700) || elapsed.TotalSeconds >= 15)
        {
            _refreshAnimationTimer.Stop();
            _refreshingAccountId = null;
            _refreshStopRequested = false;
            _refreshRotation = 0;
        }

        Invalidate();
    }

    private void DrawDeleteGlyph(Graphics g, RectangleF rect, int hitId)
    {
        var hovered = _hoveredId == hitId;
        var pressed = _pressedId == hitId;
        Color bg;
        if (pressed) bg = Color.FromArgb(255, 230, 230);
        else if (hovered) bg = Color.FromArgb(255, 240, 240);
        else bg = Color.Transparent;
        if (bg.A != 0)
        {
            using var b = new SolidBrush(bg);
            FillRound(g, b, rect, DpiF(4));
        }

        using var font = FluentTheme.IconFontPx(DpiF(14));
        DrawText(g, FluentIcons.Delete, font, hovered ? FluentTheme.Critical : FluentTheme.TextSecondary, rect, center: true);
    }

    private void DrawActions(Graphics g)
    {
        var separatorY = ActionsSeparatorY();
        var buttonY = ActionsButtonY();
        using var sep = new Pen(FluentTheme.StrokeDefault, Dpi(1));
        g.DrawLine(sep, DpiF(16), DpiF(separatorY), DpiF(424), DpiF(separatorY));

        using var small = FontPx(11, FontStyle.Regular);
        DrawText(g, RefreshHint(_accounts.Select(a => a.LastChecked).Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Max()), small, FluentTheme.TextTertiary, Rect(20, buttonY + 2, 130, 24));

        var keepAwake = _isKeepAwakeEnabled();
        DrawIconButton(g, FluentIcons.Chart, "会话分析", Rect(126, buttonY, 32, 32), _showUsage, closeAfter: true);
        DrawIconButton(g, keepAwake ? FluentIcons.Unlock : FluentIcons.Lock, keepAwake ? "解除保持唤醒" : "锁定保持唤醒", Rect(164, buttonY, 32, 32), ToggleKeepAwakeInPlace);
        DrawIconButton(g, FluentIcons.Home, "打开主页", Rect(202, buttonY, 32, 32), _openDashboard, closeAfter: true);
        DrawIconButton(g, FluentIcons.Add, "添加账号", Rect(240, buttonY, 32, 32), _addAccount, closeAfter: true);
        DrawIconButton(g, FluentIcons.Import, "导入账号文件", Rect(278, buttonY, 32, 32), _importAccounts, closeAfter: true);
        DrawIconButton(g, FluentIcons.Export, "导出账号", Rect(316, buttonY, 32, 32), _exportAccounts, closeAfter: true);
        DrawIconButton(g, FluentIcons.Settings, "设置", Rect(354, buttonY, 32, 32), _openConfig, closeAfter: true);
        DrawIconButton(g, FluentIcons.SignOut, "退出", Rect(392, buttonY, 32, 32), _exit, closeAfter: true);
    }

    private void DrawIconButton(Graphics g, string icon, string tooltip, RectangleF rect, Action action, bool closeAfter = false)
    {
        var hitId = AddHit(rect, action, tooltip, HitStyle.IconButton, closeAfter: closeAfter);
        var hovered = _hoveredId == hitId;
        var pressed = _pressedId == hitId;
        Color bg;
        if (pressed) bg = FluentTheme.ControlBackgroundPressed;
        else if (hovered) bg = FluentTheme.ControlBackgroundHover;
        else bg = FluentTheme.ControlBackground;
        using (var brush = new SolidBrush(bg))
        {
            FillRound(g, brush, rect, DpiF(4));
        }
        using (var pen = new Pen(FluentTheme.StrokeDefault, Dpi(1)))
        {
            DrawRound(g, pen, rect, DpiF(4));
        }
        using var font = FluentTheme.IconFontPx(DpiF(15));
        DrawText(g, icon, font, FluentTheme.TextPrimary, rect, center: true);
    }

    private void DrawAccentButton(Graphics g, RectangleF rect, string label, int hitId)
    {
        var hovered = _hoveredId == hitId;
        var pressed = _pressedId == hitId;
        Color bg;
        if (pressed) bg = FluentTheme.AccentPressed;
        else if (hovered) bg = FluentTheme.AccentHover;
        else bg = FluentTheme.Accent;
        using (var brush = new SolidBrush(bg))
        {
            FillRound(g, brush, rect, DpiF(4));
        }
        using var font = FontPx(11, FontStyle.Bold);
        DrawText(g, label, font, Color.White, rect, center: true);
    }

    private int AddHit(RectangleF bounds, Action action, string? tooltip, HitStyle style, bool closeAfter = false)
    {
        var id = ++_nextHitId;
        _hits.Add(new Hit
        {
            Bounds = bounds,
            Action = action,
            Tooltip = tooltip,
            Style = style,
            Id = id,
            CloseAfter = closeAfter,
        });
        return id;
    }

    private float PopupLogicalHeight()
    {
        return AccountListTop + AccountViewportHeight() + 76f;
    }

    private float AccountViewportHeight()
    {
        if (_accounts.Count == 0)
        {
            return 110f;
        }

        var visibleRows = Math.Clamp(_accounts.Count, 1, 4);
        return visibleRows * AccountRowHeight + Math.Max(0, visibleRows - 1) * AccountRowGap;
    }

    private float ActionsSeparatorY()
    {
        return AccountListTop + AccountViewportHeight() + 16f;
    }

    private float ActionsButtonY()
    {
        return ActionsSeparatorY() + 16f;
    }

    private IEnumerable<TokenAccount> SortedAccounts()
    {
        return _accounts
            .OrderBy(a => !string.Equals(a.AccountId, _activeAccountId, StringComparison.Ordinal))
            .ThenByDescending(a => HealthValue(a))
            .ThenBy(a => BuildAccountLabel(a), StringComparer.CurrentCultureIgnoreCase);
    }

    private TokenAccount? ActiveAccount()
    {
        return string.IsNullOrWhiteSpace(_activeAccountId)
            ? null
            : _accounts.FirstOrDefault(a => string.Equals(a.AccountId, _activeAccountId, StringComparison.Ordinal));
    }

    private static double HealthValue(TokenAccount account)
    {
        return Math.Max(Clamp(account.PrimaryUsedPercent), Clamp(account.SecondaryUsedPercent));
    }

    private static string BuildAccountLabel(TokenAccount account)
    {
        return !string.IsNullOrWhiteSpace(account.Email) ? account.Email : account.AccountId;
    }

    private static string ResetHint(DateTimeOffset? reset)
    {
        if (reset is null) return "--";
        var span = reset.Value - DateTimeOffset.Now;
        if (span.TotalMinutes <= 0) return "可刷新";
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}时{span.Minutes:D2}分"
            : $"{Math.Max(1, (int)span.TotalMinutes)}分钟";
    }

    private static string ResetDetail(DateTimeOffset? reset)
    {
        if (reset is null) return "--";
        return $"{ResetHint(reset)} · {reset.Value.ToLocalTime():M月d日 H:mm}";
    }

    private static string RefreshHint(DateTimeOffset checkedAt)
    {
        if (checkedAt == default) return "未刷新";
        var span = DateTimeOffset.UtcNow - checkedAt.ToUniversalTime();
        if (span.TotalSeconds < 30) return "刚刚刷新";
        if (span.TotalMinutes < 1) return $"{Math.Max(1, (int)span.TotalSeconds)} 秒前";
        if (span.TotalHours < 1) return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        return checkedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private string FormatTokenCount(long tokens)
    {
        return AccountUsageHelpers.FormatTokenCount(tokens, _config.OpenAI.TokenUnitDisplayMode);
    }

    private static Color UsageColor(double used)
    {
        var clamped = Clamp(used);
        return clamped >= 90
            ? Color.FromArgb(220, 38, 38)
            : clamped >= 70
                ? Color.FromArgb(245, 124, 0)
                : Color.FromArgb(0, 166, 90);
    }

    private static Color PlanColor(TokenAccount account)
    {
        var plan = string.IsNullOrWhiteSpace(account.PlanType) ? "free" : account.PlanType.ToLowerInvariant();
        return plan switch
        {
            "plus" => Color.FromArgb(120, 78, 220),
            "pro" or "prolite" => Color.FromArgb(15, 123, 15),
            "team" => FluentTheme.Accent,
            _ => FluentTheme.TextSecondary,
        };
    }

    private static double Clamp(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    }

    private static string TrimMiddle(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= max) return text;
        var left = Math.Max(1, (max - 1) / 2);
        var right = Math.Max(1, max - left - 1);
        return $"{text[..left]}...{text[^right..]}";
    }

    private void DrawUsageBar(Graphics g, RectangleF rect, double value)
    {
        using var bg = new SolidBrush(Color.FromArgb(229, 229, 229));
        using var fg = new SolidBrush(UsageColor(value));
        FillRound(g, bg, rect, DpiF(3));
        var fill = rect;
        fill.Width = (float)(rect.Width * Clamp(value) / 100d);
        if (fill.Width > 0)
        {
            FillRound(g, fg, fill, DpiF(3));
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogService.LogException(ex, "open codex radar");
        }
    }

    private void DrawPill(Graphics g, string text, RectangleF rect, Color fore, Color back, Font font)
    {
        using var bg = new SolidBrush(back);
        FillRound(g, bg, rect, DpiF(7));
        DrawText(g, text, font, fore, rect, center: true);
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, bool center = false, bool right = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            Alignment = center ? StringAlignment.Center : right ? StringAlignment.Far : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, brush, rect, format);
    }

    private Font FontPx(float size, FontStyle style)
    {
        return new Font(FluentTheme.TextFontFamily, DpiF(size), style, GraphicsUnit.Pixel);
    }

    private int Dpi(float value) => (int)Math.Round(value * _scale);
    private float DpiF(float value) => value * _scale;
    private Size DpiSize(float w, float h) => new Size(Dpi(w), Dpi(h));
    private RectangleF Rect(float x, float y, float w, float h) => new RectangleF(DpiF(x), DpiF(y), DpiF(w), DpiF(h));

    private static void FillRound(Graphics g, Brush brush, RectangleF rect, float radius)
    {
        using var path = RoundPath(rect, radius);
        g.FillPath(brush, path);
    }

    private static void DrawRound(Graphics g, Pen pen, RectangleF rect, float radius)
    {
        using var path = RoundPath(rect, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundPath(RectangleF rect, float radius)
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

    // —— Mouse hook: 点击 popup 之外任何位置都应该关闭。
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_NCLBUTTONDOWN = 0x00A1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private void InstallMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero) return;
        _mouseHookProc = MouseHookCallback;
        _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        _mouseHookProc = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_closingFromHook)
        {
            var message = (int)wParam;
            if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN || message == WM_NCLBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var screenBounds = RectangleToScreen(ClientRectangle);
                if (!screenBounds.Contains(data.pt.x, data.pt.y))
                {
                    _closingFromHook = true;
                    BeginInvoke(() =>
                    {
                        if (!IsDisposed) Close();
                    });
                }
            }
        }
        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }
}
