using CodexBarWin.Models;
using CodexBarWin.Services;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexBarWin.UI;

public sealed class OpenAIOAuthLoginForm : Form
{
    private readonly OpenAIOAuthLoginService _loginService;
    private readonly TextBox _authUrlBox = new();
    private readonly TextBox _callbackBox = new();
    private readonly Label _statusLabel = new();
    private readonly Button _openBrowserButton = new();
    private readonly Button _copyButton = new();
    private readonly Button _completeButton = new();
    private readonly Button _cancelButton = new();
    private StartedOpenAIOAuthFlow? _flow;
    private LocalhostOAuthCallbackServer? _callbackServer;
    private bool _isCompleting;

    public TokenAccount? CompletedAccount { get; private set; }

    public OpenAIOAuthLoginForm(OpenAIOAuthLoginService loginService)
    {
        _loginService = loginService ?? throw new ArgumentNullException(nameof(loginService));

        Text = "添加 OpenAI 账号";
        Font = FluentTheme.TextFontPx(13);
        BackColor = FluentTheme.LayerBackground;
        Width = 640;
        Height = 500;
        MinimumSize = new Size(560, 450);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AppIconProvider.Apply(this);

        BuildLayout();
        Shown += (_, _) => StartOAuth();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MicaSupport.TryApply(this, transient: true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _callbackServer?.Dispose();
        if (_flow is not null && CompletedAccount is null)
        {
            _loginService.CancelFlow(_flow.FlowId);
        }

        base.OnFormClosed(e);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 8,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "添加 OpenAI OAuth 账号",
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(24, FontStyle.Bold),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        root.Controls.Add(new Label
        {
            Text = "会打开浏览器完成授权；本窗口会自动捕获 localhost 回调。若未捕获，可手动粘贴浏览器地址栏的回调链接或 code。",
            Dock = DockStyle.Fill,
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);

        _authUrlBox.Dock = DockStyle.Fill;
        _authUrlBox.Multiline = true;
        _authUrlBox.ReadOnly = true;
        _authUrlBox.ScrollBars = ScrollBars.Vertical;
        _authUrlBox.Font = new Font("Consolas", 9f);
        _authUrlBox.BackColor = Color.White;
        root.Controls.Add(_authUrlBox, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
        };
        ConfigureButton(_openBrowserButton, "打开授权链接", true, 132);
        _openBrowserButton.Click += (_, _) => OpenBrowser();
        buttons.Controls.Add(_openBrowserButton);

        ConfigureButton(_copyButton, "复制链接", false, 104);
        _copyButton.Click += (_, _) => CopyAuthUrl();
        buttons.Controls.Add(_copyButton);
        root.Controls.Add(buttons, 0, 3);

        root.Controls.Add(new Label
        {
            Text = "回调链接 / code",
            Dock = DockStyle.Fill,
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
            Font = FluentTheme.TextFontPx(13, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
        }, 0, 4);

        _callbackBox.Dock = DockStyle.Fill;
        _callbackBox.Multiline = true;
        _callbackBox.ScrollBars = ScrollBars.Vertical;
        _callbackBox.Font = new Font("Consolas", 9f);
        _callbackBox.BackColor = Color.White;
        root.Controls.Add(_callbackBox, 0, 5);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = FluentTheme.TextSecondary;
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        root.Controls.Add(_statusLabel, 0, 6);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 3,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        ConfigureButton(_cancelButton, "取消", false, 84);
        _cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        footer.Controls.Add(_cancelButton, 1, 0);

        ConfigureButton(_completeButton, "完成添加", true, 108);
        _completeButton.Click += async (_, _) => await CompleteAsync(_callbackBox.Text).ConfigureAwait(false);
        footer.Controls.Add(_completeButton, 2, 0);
        root.Controls.Add(footer, 0, 7);

        AcceptButton = _completeButton;
        CancelButton = _cancelButton;
    }

    private void StartOAuth()
    {
        try
        {
            _flow = _loginService.StartFlow();
            _authUrlBox.Text = _flow.AuthUrl;
            StartCallbackServer();
            OpenBrowser();
            SetStatus("已打开浏览器，等待授权回调。", FluentTheme.TextSecondary);
        }
        catch (Exception ex)
        {
            SetStatus("无法创建授权链接：" + ex.Message, FluentTheme.Critical);
        }
    }

    private void StartCallbackServer()
    {
        try
        {
            _callbackServer = _loginService.CreateCallbackServer(callbackUrl =>
            {
                if (IsDisposed || !IsHandleCreated)
                {
                    return;
                }

                BeginInvoke((Action)(async () =>
                {
                    _callbackBox.Text = callbackUrl;
                    await CompleteAsync(callbackUrl, autoCaptured: true).ConfigureAwait(false);
                }));
            });
            _callbackServer.Start();
        }
        catch (Exception ex)
        {
            SetStatus("本地回调监听未启动，可手动粘贴回调链接：" + ex.Message, FluentTheme.Warning);
        }
    }

    private async Task CompleteAsync(string input, bool autoCaptured = false)
    {
        if (_isCompleting)
        {
            return;
        }

        if (_flow is null)
        {
            SetStatus("授权流程还没有准备好。", FluentTheme.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            SetStatus("请粘贴回调链接或 code。", FluentTheme.Warning);
            return;
        }

        _isCompleting = true;
        SetBusy(true);
        SetStatus(autoCaptured ? "已捕获回调，正在保存账号..." : "正在完成授权并保存账号...", FluentTheme.TextSecondary);
        try
        {
            CompletedAccount = await _loginService.CompleteFlowAsync(_flow.FlowId, input).ConfigureAwait(true);
            _callbackServer?.Dispose();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("添加失败：" + ex.Message, FluentTheme.Critical);
            SetBusy(false);
            _isCompleting = false;
        }
    }

    private void OpenBrowser()
    {
        if (_flow is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_flow.AuthUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("浏览器打开失败，可复制链接手动打开：" + ex.Message, FluentTheme.Warning);
        }
    }

    private void CopyAuthUrl()
    {
        if (_flow is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_flow.AuthUrl);
            SetStatus("授权链接已复制。", FluentTheme.Success);
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, FluentTheme.Warning);
        }
    }

    private void SetBusy(bool busy)
    {
        _completeButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        _openBrowserButton.Enabled = !busy;
        _copyButton.Enabled = !busy;
        _completeButton.Text = busy ? "保存中..." : "完成添加";
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private static void ConfigureButton(Button button, string text, bool primary, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 34;
        button.Margin = new Padding(0, 0, 10, 0);
        button.Font = FluentTheme.TextFontPx(13, primary ? FontStyle.Bold : FontStyle.Regular);
        FluentTheme.ApplyButton(button, primary);
    }
}
