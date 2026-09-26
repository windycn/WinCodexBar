using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexBarWin.UI;

/// <summary>
/// Shows a temporary black overlay on each display. This is a visual away screen only:
/// it does not lock Windows, change power settings, or pause background applications.
/// </summary>
public sealed class AwayModeController : IDisposable
{
    private readonly List<AwayModeOverlayForm> _overlays = new();
    private System.Windows.Forms.Timer? _countdownTimer;
    private AwayModeCountdownForm? _countdownForm;
    private DateTimeOffset _startDeadline;
    private bool _isActive;
    private bool _cursorHidden;

    public bool IsActive => _isActive;
    public bool IsCountdownPending => _countdownForm is { IsDisposed: false };

    public void Start()
    {
        CancelPendingStart();
        StartImmediately();
    }

    public void Start(int delaySeconds)
    {
        if (delaySeconds <= 0)
        {
            Start();
            return;
        }

        if (_isActive || IsCountdownPending) return;

        _startDeadline = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
        var countdown = new AwayModeCountdownForm(delaySeconds, CancelPendingStart);
        _countdownForm = countdown;
        countdown.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_countdownForm, countdown)) CancelPendingStart();
        };

        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        _countdownTimer = timer;
        timer.Tick += (_, _) =>
        {
            var remaining = (int)Math.Ceiling((_startDeadline - DateTimeOffset.UtcNow).TotalSeconds);
            if (remaining <= 0)
            {
                _countdownTimer?.Stop();
                _countdownTimer?.Dispose();
                _countdownTimer = null;
                _countdownForm = null;
                countdown.Close();
                countdown.Dispose();
                StartImmediately();
                return;
            }

            countdown.SetRemainingSeconds(remaining);
        };

        countdown.Show();
        countdown.Activate();
        timer.Start();
    }

    public void CancelPendingStart()
    {
        _countdownTimer?.Stop();
        _countdownTimer?.Dispose();
        _countdownTimer = null;

        var countdown = _countdownForm;
        _countdownForm = null;
        if (countdown is not null && !countdown.IsDisposed)
        {
            countdown.Close();
            countdown.Dispose();
        }
    }

    private void StartImmediately()
    {
        if (_isActive) return;

        var origin = Cursor.Position;
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;

        _isActive = true;
        try
        {
            foreach (var screen in screens)
            {
                var overlay = new AwayModeOverlayForm(screen.Bounds, origin, Stop);
                _overlays.Add(overlay);
                overlay.FormClosed += (_, _) => { if (_isActive) Stop(); };
            }

            Cursor.Hide();
            _cursorHidden = true;
            foreach (var overlay in _overlays) overlay.Show();
            _overlays.LastOrDefault(overlay => Screen.PrimaryScreen?.Bounds == overlay.Bounds)?.Activate();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        if (!_isActive && _overlays.Count == 0) return;
        _isActive = false;

        foreach (var overlay in _overlays.ToArray())
        {
            if (!overlay.IsDisposed)
            {
                overlay.Close();
                overlay.Dispose();
            }
        }
        _overlays.Clear();

        if (_cursorHidden)
        {
            Cursor.Show();
            _cursorHidden = false;
        }
    }

    public void Dispose()
    {
        CancelPendingStart();
        Stop();
    }

    private sealed class AwayModeCountdownForm : Form
    {
        private readonly Label _remainingLabel;

        public AwayModeCountdownForm(int delaySeconds, Action cancel)
        {
            Text = "黑屏离开模式";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            ShowIcon = false;
            ControlBox = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = FluentTheme.LayerBackground;
            ClientSize = new Size(408, 188);
            Font = FluentTheme.TextFontPx(14);

            var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(workArea.Left + (workArea.Width - Width) / 2,
                workArea.Top + (workArea.Height - Height) / 2);

            Controls.Add(new Label
            {
                Text = "正在准备黑屏离开模式",
                Location = new Point(20, 16),
                Size = new Size(368, 28),
                Font = FluentTheme.TextFontPx(17, FontStyle.Bold),
                ForeColor = FluentTheme.TextPrimary,
            });

            _remainingLabel = new Label
            {
                Location = new Point(20, 48),
                Size = new Size(368, 38),
                Font = FluentTheme.TextFontPx(22, FontStyle.Bold),
                ForeColor = FluentTheme.Accent,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            Controls.Add(_remainingLabel);
            SetRemainingSeconds(delaySeconds);

            Controls.Add(new Label
            {
                Text = "黑屏后移动鼠标或按任意键唤醒；不会锁屏，也不会中断 Codex。",
                Location = new Point(20, 91),
                Size = new Size(368, 34),
                Font = FluentTheme.TextFontPx(11),
                ForeColor = FluentTheme.TextSecondary,
                TextAlign = ContentAlignment.MiddleCenter,
            });

            var cancelButton = new FluentButton
            {
                Text = "取消（Esc）",
                Location = new Point(146, 138),
                Size = new Size(116, 34),
                Font = FluentTheme.TextFontPx(13, FontStyle.Bold),
            };
            FluentTheme.ApplyButton(cancelButton);
            cancelButton.Click += (_, _) => cancel();
            Controls.Add(cancelButton);
            AcceptButton = cancelButton;
            CancelButton = cancelButton;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    cancel();
                }
            };
        }

        public void SetRemainingSeconds(int seconds)
        {
            _remainingLabel.Text = $"{seconds} 秒后进入黑屏";
        }
    }

    private sealed class AwayModeOverlayForm : Form
    {
        private readonly Point _origin;
        private readonly Action _wake;

        public AwayModeOverlayForm(Rectangle bounds, Point origin, Action wake)
        {
            _origin = origin;
            _wake = wake;
            Bounds = bounds;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Color.Black;

            MouseMove += OnPointerMoved;
            MouseDown += (_, _) => _wake();
            KeyDown += OnKeyPressed;
        }

        private void OnPointerMoved(object? sender, MouseEventArgs e)
        {
            var position = PointToScreen(e.Location);
            if (Math.Abs(position.X - _origin.X) >= 2 || Math.Abs(position.Y - _origin.Y) >= 2)
            {
                _wake();
            }
        }

        private void OnKeyPressed(object? sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            _wake();
        }
    }
}
