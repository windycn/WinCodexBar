using System.Drawing;
using System.Windows.Forms;

namespace CodexBarWin.UI;

/// <summary>以 96 DPI 为设计基准，小屏保留滚动入口，窗口始终位于当前显示器工作区。</summary>
public class AdaptiveForm : Form
{
    private Panel? _viewport;
    private Control? _content;
    private Size _logicalMinimum;
    private float _manualFactor = 1f;

    protected AdaptiveForm()
    {
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected void InstallScrollableContent(Control content, Size logicalMinimum)
    {
        _logicalMinimum = logicalMinimum;
        _content = content;
        _viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = logicalMinimum != Size.Empty, BackColor = Color.Transparent };
        content.Dock = logicalMinimum == Size.Empty ? DockStyle.Fill : DockStyle.None;
        content.Margin = Padding.Empty;
        content.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _viewport.Controls.Add(content);
        Controls.Add(_viewport);
        _viewport.ClientSizeChanged += (_, _) => LayoutContent();
        LayoutContent();
    }

    private void LayoutContent()
    {
        if (_viewport is null || _content is null) return;
        if (_logicalMinimum == Size.Empty) return;
        var scale = AppAppearance.ScaleFor(this);
        var minimum = new Size((int)(_logicalMinimum.Width * scale), (int)(_logicalMinimum.Height * scale));
        var size = new Size(Math.Max(minimum.Width, _viewport.ClientSize.Width), Math.Max(minimum.Height, _viewport.ClientSize.Height));
        if (_content.Size != size) _content.Size = size;
        _viewport.AutoScrollMinSize = minimum;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyAppearance();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyAppearance();
    }

    public virtual void ApplyAppearance()
    {
        var factor = AppAppearance.ScaleFor(this) / (DeviceDpi / 96f);
        var relative = factor / _manualFactor;
        _manualFactor = factor;
        AppAppearance.ScaleTree(this, relative);
        FitToScreen();
        LayoutContent();
        Invalidate(true);
    }

    public void FitToScreen()
    {
        if (WindowState != FormWindowState.Normal) return;
        var area = Screen.FromControl(this).WorkingArea;
        var margin = Math.Min(8, Math.Min(area.Width, area.Height) / 10);
        area.Inflate(-margin, -margin);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        Bounds = new Rectangle(Math.Clamp(Left, area.Left, area.Right - width), Math.Clamp(Top, area.Top, area.Bottom - height), width, height);
    }
}
