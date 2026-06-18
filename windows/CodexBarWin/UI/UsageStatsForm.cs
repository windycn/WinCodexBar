using CodexBarWin.Models;
using CodexBarWin.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CodexBarWin.UI;

public sealed class UsageStatsForm : Form
{
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 16f, FontStyle.Bold);
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
    private static readonly Font StrongFont = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
    private static readonly Font SmallFont = new("Microsoft YaHei UI", 9f, FontStyle.Regular);
    private readonly IReadOnlyList<TokenAccount> _accounts;
    private readonly string? _activeAccountId;
    private readonly CodexBarConfig _config;

    public UsageStatsForm(IReadOnlyList<TokenAccount> accounts, string? activeAccountId, CodexBarConfig config)
    {
        _accounts = accounts;
        _activeAccountId = activeAccountId;
        _config = config;

        Text = "WinCodexBar 用量";
        Font = BodyFont;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(680, 520);
        MinimumSize = new Size(560, 360);
        BackColor = Color.FromArgb(246, 248, 251);
        Padding = new Padding(1);
        ShowInTaskbar = false;

        Build();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MicaSupport.TryApply(this);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        MicaSupport.ApplyRoundedRegion(this);
    }

    private void Build()
    {
        var root = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            BackColor = Color.FromArgb(246, 248, 251),
        };

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 56,
            ColumnCount = 2,
            RowCount = 1,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));

        top.Controls.Add(new Label
        {
            Text = "用量统计",
            Dock = DockStyle.Fill,
            Font = TitleFont,
            ForeColor = Color.FromArgb(31, 42, 58),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var close = new Button
        {
            Text = "×",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(55, 65, 81),
            Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Regular),
            TabStop = false,
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        top.Controls.Add(close, 1, 0);

        var label = _config.OpenAI.UsageDisplayMode == UsageDisplayMode.Remaining ? "剩余" : "已用";
        var summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            Font = BodyFont,
            ForeColor = Color.FromArgb(82, 95, 112),
            Text = $"账号 {_accounts.Count} · 显示模式：{label} · {BuildAverageText()}",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 8, 4, 0),
        };

        foreach (var account in _accounts.OrderBy(a => !string.Equals(a.AccountId, _activeAccountId, StringComparison.Ordinal))
                                          .ThenBy(a => AccountUsageHelpers.DisplayName(a)))
        {
            list.Controls.Add(BuildUsageRow(account));
        }

        root.Controls.Add(list);
        root.Controls.Add(summary);
        root.Controls.Add(top);
        Controls.Add(root);
    }

    private Control BuildUsageRow(TokenAccount account)
    {
        var status = AccountUsageHelpers.Health(
            account,
            _config.OpenAI.WarningThresholdPercent,
            _config.OpenAI.DangerThresholdPercent);
        var accent = HealthAccent(status);

        var row = new Panel
        {
            Width = 600,
            Height = 110,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.White,
        };
        row.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var border = new Pen(Color.FromArgb(218, 226, 238));
            using var path = Rounded(new Rectangle(0, 0, row.Width - 1, row.Height - 1), 10);
            e.Graphics.DrawPath(border, path);
            using var accentBrush = new SolidBrush(accent);
            e.Graphics.FillRectangle(accentBrush, 0, 0, 4, row.Height);
        };

        var title = new Label
        {
            Text = AccountUsageHelpers.DisplayName(account)
                   + (string.Equals(account.AccountId, _activeAccountId, StringComparison.Ordinal) ? "（已激活）" : string.Empty),
            Location = new Point(16, 8),
            Size = new Size(360, 22),
            Font = StrongFont,
            ForeColor = Color.FromArgb(31, 42, 58),
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        var plan = new Label
        {
            Text = AccountUsageHelpers.PlanLabel(account),
            Location = new Point(row.Width - 124, 8),
            Size = new Size(80, 22),
            Font = StrongFont,
            ForeColor = Color.FromArgb(0, 122, 204),
            TextAlign = ContentAlignment.MiddleRight,
            BackColor = Color.Transparent,
        };
        var statusLabel = new Label
        {
            Text = AccountUsageHelpers.HealthLabel(status),
            Location = new Point(16, 32),
            Size = new Size(120, 20),
            Font = SmallFont,
            ForeColor = accent,
            BackColor = Color.Transparent,
        };

        row.Controls.Add(title);
        row.Controls.Add(plan);
        row.Controls.Add(statusLabel);
        row.Controls.Add(BuildUsageLine("5h 主池", account.PrimaryUsedPercent, account.PrimaryResetAt, new Point(16, 56), row.Width - 32, accent));
        row.Controls.Add(BuildUsageLine("7d 次池", account.SecondaryUsedPercent, account.SecondaryResetAt, new Point(16, 80), row.Width - 32, accent));
        return row;
    }

    private Control BuildUsageLine(string name, double usedPercent, DateTimeOffset? resetAt, Point location, int width, Color accent)
    {
        var panel = new Panel
        {
            Location = location,
            Size = new Size(width, 22),
            BackColor = Color.Transparent,
        };

        var leftLabel = new Label
        {
            Text = $"{name}  {AccountUsageHelpers.FormatDisplayPercent(usedPercent, _config.OpenAI.UsageDisplayMode)}",
            Location = new Point(0, 0),
            Size = new Size(180, 22),
            Font = SmallFont,
            ForeColor = Color.FromArgb(72, 84, 101),
            BackColor = Color.Transparent,
        };
        var resetLabel = new Label
        {
            Text = "重置 " + AccountUsageHelpers.FormatResetCountdown(resetAt),
            Location = new Point(width - 160, 0),
            Size = new Size(160, 22),
            Font = SmallFont,
            ForeColor = Color.FromArgb(112, 124, 140),
            TextAlign = ContentAlignment.MiddleRight,
            BackColor = Color.Transparent,
        };
        var bar = new Panel
        {
            Location = new Point(180, 8),
            Size = new Size(Math.Max(60, width - 350), 6),
            BackColor = Color.Transparent,
        };
        bar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(229, 236, 244));
            using var fill = new SolidBrush(accent);
            using var backPath = Rounded(new Rectangle(0, 0, bar.Width - 1, bar.Height - 1), 3);
            e.Graphics.FillPath(back, backPath);
            var used = AccountUsageHelpers.Clamp(usedPercent);
            var fillWidth = Math.Clamp((int)Math.Round(bar.Width * used / 100d), 0, bar.Width);
            if (fillWidth > 0)
            {
                using var fillPath = Rounded(new Rectangle(0, 0, fillWidth, bar.Height - 1), 3);
                e.Graphics.FillPath(fill, fillPath);
            }
        };

        panel.Controls.Add(leftLabel);
        panel.Controls.Add(bar);
        panel.Controls.Add(resetLabel);
        return panel;
    }

    private string BuildAverageText()
    {
        if (_accounts.Count == 0)
        {
            return "暂无数据";
        }

        var primary = _accounts.Select(a => AccountUsageHelpers.Clamp(a.PrimaryUsedPercent)).Average();
        var secondary = _accounts.Select(a => AccountUsageHelpers.Clamp(a.SecondaryUsedPercent)).Average();
        var primaryDisplay = AccountUsageHelpers.DisplayPercent(primary, _config.OpenAI.UsageDisplayMode);
        var secondaryDisplay = AccountUsageHelpers.DisplayPercent(secondary, _config.OpenAI.UsageDisplayMode);
        return $"5h {primaryDisplay:F1}%  ·  7d {secondaryDisplay:F1}%";
    }

    private static Color HealthAccent(AccountHealthStatus status)
    {
        return status switch
        {
            AccountHealthStatus.Healthy => Color.FromArgb(33, 168, 96),
            AccountHealthStatus.Warning => Color.FromArgb(245, 158, 11),
            AccountHealthStatus.Exhausted => Color.FromArgb(220, 38, 38),
            AccountHealthStatus.Suspended => Color.FromArgb(99, 102, 241),
            AccountHealthStatus.TokenExpired => Color.FromArgb(220, 38, 38),
            _ => Color.FromArgb(140, 148, 160),
        };
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
