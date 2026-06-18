using CodexBarWin.Models;
using CodexBarWin.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexBarWin.UI;

public sealed class SettingsForm : Form
{
    private enum Page
    {
        Accounts,
        Usage,
        Windows,
        Models,
    }

    private readonly CodexBarConfigStore _configStore;
    private readonly KeepAwakeService _keepAwakeService;
    private readonly Action _openConfigFolder;
    private readonly Action _onSettingsChanged;
    private readonly FlowLayoutPanel _sidebar = new();
    private readonly Panel _scrollHost = new();
    private readonly Panel _content = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _saveStatusLabel = new();
    private readonly Button _saveButton = new FluentButton();
    private readonly Button _cancelButton = new FluentButton();
    private readonly Button _configButton = new FluentButton();
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<Page, Control[]> _pageCache = new();
    private CodexBarConfig _draft;
    private Page _selectedPage = Page.Accounts;

    public SettingsForm(
        CodexBarConfigStore configStore,
        KeepAwakeService keepAwakeService,
        Action openConfigFolder,
        Action onSettingsChanged)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _keepAwakeService = keepAwakeService ?? throw new ArgumentNullException(nameof(keepAwakeService));
        _openConfigFolder = openConfigFolder ?? throw new ArgumentNullException(nameof(openConfigFolder));
        _onSettingsChanged = onSettingsChanged ?? throw new ArgumentNullException(nameof(onSettingsChanged));
        _draft = _configStore.Config.Clone();
        _draft.KeepAwakeEnabled = _keepAwakeService.IsEnabled;
        _draft.AdvancedKeepAwakeEnabled = _keepAwakeService.IsAdvancedEnabled;
        _draft.StartWithWindows = StartupService.IsEnabled();

        Text = "WinCodexBar 设置";
        Font = FluentTheme.TextFontPx(14);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 720);
        Size = new Size(1180, 780);
        BackColor = FluentTheme.LayerBackground;
        AppIconProvider.Apply(this);

        BuildLayout();
        SelectPage(Page.Accounts);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MicaSupport.TryApply(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            foreach (var controls in _pageCache.Values)
            {
                foreach (var control in controls)
                {
                    if (!control.IsDisposed)
                    {
                        control.Dispose();
                    }
                }
            }

            _pageCache.Clear();
        }

        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var navCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = FluentTheme.SubtleBackground,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 16, 0),
        };
        navCard.Paint += (_, e) => DrawRoundedPanel(e.Graphics, navCard.ClientRectangle, FluentTheme.SubtleBackground, FluentTheme.StrokeDefault, 8);
        root.Controls.Add(navCard, 0, 0);

        _sidebar.Dock = DockStyle.Fill;
        _sidebar.BackColor = Color.Transparent;
        _sidebar.FlowDirection = FlowDirection.TopDown;
        _sidebar.WrapContents = false;
        navCard.Controls.Add(_sidebar);
        _sidebar.Controls.Add(MakeNavButton(Page.Accounts, FluentIcons.Account, "账号设置"));
        _sidebar.Controls.Add(MakeNavButton(Page.Usage, FluentIcons.Chart, "用量设置"));
        _sidebar.Controls.Add(MakeNavButton(Page.Windows, FluentIcons.Power, "唤醒策略"));
        _sidebar.Controls.Add(MakeNavButton(Page.Models, FluentIcons.Settings, "模型参数"));

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 3,
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.Controls.Add(main, 1, 0);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            RowCount = 2,
            ColumnCount = 1,
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        main.Controls.Add(header, 0, 0);

        _title.Dock = DockStyle.Fill;
        _title.Font = FluentTheme.TextFontPx(28, FontStyle.Bold);
        _title.ForeColor = FluentTheme.TextPrimary;
        _title.BackColor = Color.Transparent;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(_title, 0, 0);

        _subtitle.Dock = DockStyle.Fill;
        _subtitle.Font = FluentTheme.TextFontPx(15);
        _subtitle.ForeColor = FluentTheme.TextSecondary;
        _subtitle.BackColor = Color.Transparent;
        _subtitle.TextAlign = ContentAlignment.TopLeft;
        _subtitle.Padding = new Padding(0, 3, 0, 0);
        header.Controls.Add(_subtitle, 0, 1);

        _scrollHost.Dock = DockStyle.Fill;
        _scrollHost.AutoScroll = true;
        _scrollHost.BackColor = Color.Transparent;
        _scrollHost.Padding = new Padding(0, 4, 8, 8);
        _scrollHost.Resize += (_, _) => ResizeCards();
        main.Controls.Add(_scrollHost, 0, 1);

        _content.BackColor = Color.Transparent;
        _content.Location = new Point(0, 0);
        _content.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _scrollHost.Controls.Add(_content);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(0, 12, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        main.Controls.Add(footer, 0, 2);

        _saveStatusLabel.Dock = DockStyle.Fill;
        _saveStatusLabel.BackColor = Color.Transparent;
        _saveStatusLabel.ForeColor = FluentTheme.Success;
        _saveStatusLabel.Font = FluentTheme.TextFontPx(13, FontStyle.Bold);
        _saveStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _saveStatusLabel.AutoEllipsis = true;
        footer.Controls.Add(_saveStatusLabel, 0, 0);

        ConfigureButton(_configButton, "配置目录", false);
        _configButton.Click += (_, _) => _openConfigFolder();
        footer.Controls.Add(_configButton, 1, 0);

        ConfigureButton(_cancelButton, "取消", false);
        _cancelButton.Click += (_, _) => Close();
        footer.Controls.Add(_cancelButton, 2, 0);

        ConfigureButton(_saveButton, "保存", true);
        _saveButton.Click += (_, _) => SaveSettings();
        footer.Controls.Add(_saveButton, 3, 0);
    }

    private Button MakeNavButton(Page page, string icon, string text)
    {
        var button = new FluentButton
        {
            Width = 152,
            Height = 42,
            Margin = new Padding(0, 0, 0, 6),
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = FluentTheme.TextFontPx(16, FontStyle.Regular),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => SelectPage(page);
        _toolTip.SetToolTip(button, text);
        return button;
    }

    private void SelectPage(Page page)
    {
        _selectedPage = page;
        foreach (Button button in _sidebar.Controls.OfType<Button>())
        {
            button.BackColor = FluentTheme.SubtleBackground;
            button.ForeColor = FluentTheme.TextSecondary;
        }

        if ((int)page < _sidebar.Controls.Count && _sidebar.Controls[(int)page] is Button selected)
        {
            selected.BackColor = Color.FromArgb(229, 240, 252);
            selected.ForeColor = FluentTheme.Accent;
        }

        _scrollHost.SuspendLayout();
        _content.SuspendLayout();
        _scrollHost.AutoScrollPosition = Point.Empty;
        _scrollHost.AutoScrollMinSize = Size.Empty;
        _content.Controls.Clear();
        _content.Location = new Point(0, 0);
        var useCachedPage = _pageCache.TryGetValue(page, out var cachedPageControls);

        switch (page)
        {
            case Page.Accounts:
                _title.Text = "账号设置";
                _subtitle.Text = "选择账号切换与路由方式。";
                if (!useCachedPage) BuildAccountsPage();
                break;
            case Page.Usage:
                _title.Text = "用量设置";
                _subtitle.Text = "调整额度、Token 单位和刷新。";
                if (!useCachedPage) BuildUsagePage();
                break;
            case Page.Windows:
                _title.Text = "唤醒策略";
                _subtitle.Text = "控制防休眠和空闲保护。";
                if (!useCachedPage) BuildWindowsPage();
                break;
            case Page.Models:
                _title.Text = "模型参数";
                _subtitle.Text = "配置默认模型与请求参数。";
                if (!useCachedPage) BuildModelsPage();
                break;
        }

        if (useCachedPage && cachedPageControls is not null)
        {
            _content.Controls.AddRange(cachedPageControls);
        }
        else
        {
            _pageCache[page] = _content.Controls.Cast<Control>().ToArray();
        }

        _content.ResumeLayout(true);
        ResizeCards();
        _scrollHost.AutoScrollPosition = Point.Empty;
        _scrollHost.ResumeLayout(true);
        _content.PerformLayout();
        _content.Invalidate();
        _scrollHost.Invalidate();
    }

    private void BuildAccountsPage()
    {
        _content.Controls.Add(MakeRadioCard(
            FluentIcons.Switch,
            "账号使用模式",
            new[]
            {
                ("手动", "点击账号后直接切换。", AccountUsageMode.Switch, true),
                ("聚合", "本地账号池自动路由。", AccountUsageMode.AggregateGateway, true),
            },
            _draft.OpenAI.AccountUsageMode,
            value => _draft.OpenAI.AccountUsageMode = value));

        _content.Controls.Add(MakeInfoCard(
            FluentIcons.Info,
            "切换行为",
            "切换后同步 Codex，并刷新当前账号额度。"));
    }

    private void BuildUsagePage()
    {
        _content.Controls.Add(MakeRadioCard(
            FluentIcons.Eye,
            "用量显示方式",
            new[]
            {
                ("已用额度", "显示已消耗比例。", UsageDisplayMode.Used, true),
                ("剩余额度", "显示剩余可用比例。", UsageDisplayMode.Remaining, true),
            },
            _draft.OpenAI.UsageDisplayMode,
            value => _draft.OpenAI.UsageDisplayMode = value));

        _content.Controls.Add(MakeRadioCard(
            FluentIcons.Chart,
            "Token 数字单位",
            new[]
            {
                ("中文单位", "显示为万、亿。", TokenUnitDisplayMode.Chinese, true),
                ("英文单位", "显示为 K/M/B。", TokenUnitDisplayMode.Compact, true),
            },
            _draft.OpenAI.TokenUnitDisplayMode,
            value => _draft.OpenAI.TokenUnitDisplayMode = value));

        _content.Controls.Add(MakePricingCard());

        _content.Controls.Add(MakeCheckNumberCard(
            FluentIcons.Refresh,
            "自动刷新",
            "后台刷新当前账号，低频补齐其他账号。",
            "启用自动刷新",
            _draft.OpenAI.AutoRefreshEnabled,
            enabled => _draft.OpenAI.AutoRefreshEnabled = enabled,
            "间隔秒",
            _draft.OpenAI.AutoRefreshIntervalSeconds,
            60,
            7200,
            value => _draft.OpenAI.AutoRefreshIntervalSeconds = value));

        _content.Controls.Add(MakeTwoNumberCard(
            FluentIcons.Warning,
            "健康阈值",
            "按 5h/7d 较高占用判断。",
            "警戒 %",
            (int)Math.Round(_draft.OpenAI.WarningThresholdPercent),
            1,
            99,
            value => _draft.OpenAI.WarningThresholdPercent = value,
            "高负载 %",
            (int)Math.Round(_draft.OpenAI.DangerThresholdPercent),
            1,
            100,
            value => _draft.OpenAI.DangerThresholdPercent = value));
    }

    private void BuildWindowsPage()
    {
        _content.Controls.Add(MakeCheckCard(
            FluentIcons.Power,
            "保持唤醒",
            "阻止系统休眠和息屏。",
            "开启保持唤醒",
            _draft.KeepAwakeEnabled,
            value => _draft.KeepAwakeEnabled = value));

        _content.Controls.Add(MakeCheckCard(
            FluentIcons.Activity,
            "高级防休眠",
            "空闲后模拟轻微鼠标移动。",
            "开启高级防休眠",
            _draft.AdvancedKeepAwakeEnabled,
            value => _draft.AdvancedKeepAwakeEnabled = value));

        _content.Controls.Add(MakeTwoNumberCard(
            FluentIcons.Activity,
            "高级防休眠时间",
            "设置空闲阈值和触发间隔。",
            "空闲秒",
            Math.Max(5, _draft.AdvancedKeepAwakeIdleThresholdMs / 1000),
            5,
            3600,
            value => _draft.AdvancedKeepAwakeIdleThresholdMs = value * 1000,
            "间隔秒",
            Math.Max(1, _draft.AdvancedKeepAwakeIntervalMs / 1000),
            1,
            600,
            value => _draft.AdvancedKeepAwakeIntervalMs = value * 1000));

        _content.Controls.Add(MakeCheckNumberCard(
            FluentIcons.Warning,
            "高级防休眠保护",
            "全屏暂停，并加入随机抖动。",
            "全屏时暂停",
            _draft.AdvancedKeepAwakePauseOnFullscreen,
            value => _draft.AdvancedKeepAwakePauseOnFullscreen = value,
            "抖动秒",
            Math.Max(0, _draft.AdvancedKeepAwakeJitterMs / 1000),
            0,
            120,
            value => _draft.AdvancedKeepAwakeJitterMs = value * 1000));

        _content.Controls.Add(MakeCheckCard(
            FluentIcons.Pin,
            "随开机启动",
            "登录 Windows 后自动启动 WinCodexBar。",
            "启用开机启动",
            _draft.StartWithWindows,
            value => _draft.StartWithWindows = value));

        _content.Controls.Add(MakeComboCard(
            FluentIcons.Switch,
            "高级移动策略",
            "选择鼠标微移动方式。",
            new[] { "轻量往返", "细微随机", "小范围游走" },
            MovePatternLabel(_draft.AdvancedKeepAwakeMovePattern),
            value => _draft.AdvancedKeepAwakeMovePattern = MovePatternValue(value)));

        _content.Controls.Add(MakeButtonCard(
            FluentIcons.FolderOpen,
            "配置目录",
            "打开本机配置目录。",
            "打开",
            _openConfigFolder));
    }

    private void BuildModelsPage()
    {
        _content.Controls.Add(MakeComboCard(
            FluentIcons.Settings,
            "默认模型",
            "Codex 默认使用的模型。",
            CodexBarConfig.AvailableModels,
            _draft.Global.DefaultModel,
            value => _draft.Global.DefaultModel = value));

        _content.Controls.Add(MakeComboCard(
            FluentIcons.Chart,
            "Review 模型",
            "代码评审使用的模型。",
            CodexBarConfig.AvailableModels,
            _draft.Global.ReviewModel,
            value => _draft.Global.ReviewModel = value));

        _content.Controls.Add(MakeComboCard(
            FluentIcons.Activity,
            "推理强度",
            "控制思考投入强度。",
            CodexBarConfig.AvailableReasoningEfforts,
            _draft.Global.ReasoningEffort,
            value => _draft.Global.ReasoningEffort = value));

        _content.Controls.Add(MakeComboCard(
            FluentIcons.Cloud,
            "服务等级",
            "选择请求服务等级。",
            CodexBarConfig.AvailableServiceTiers,
            _draft.Global.ServiceTier,
            value => _draft.Global.ServiceTier = value));
    }

    private Control MakeInfoCard(string glyph, string title, string description)
    {
        return MakeCard(glyph, title, description, new Label
        {
            Text = "已启用",
            AutoSize = true,
            ForeColor = FluentTheme.Success,
            Font = FluentTheme.TextFontPx(14, FontStyle.Bold),
            BackColor = Color.Transparent,
        });
    }

    private Control MakeButtonCard(string glyph, string title, string description, string buttonText, Action action)
    {
        var button = new FluentButton { Width = 96, Height = 32 };
        ConfigureButton(button, buttonText, false);
        button.Click += (_, _) => action();
        return MakeCard(glyph, title, description, button);
    }

    private Control MakeCheckCard(string glyph, string title, string description, string label, bool value, Action<bool> setter)
    {
        var check = new FluentToggleSwitch
        {
            Text = label,
            Checked = value,
            Width = 190,
            Height = 28,
            Font = FluentTheme.TextFontPx(14),
            Cursor = Cursors.Hand,
        };
        check.CheckedChanged += (_, _) => setter(check.Checked);
        return MakeCard(glyph, title, description, check, height: 118);
    }

    private Control MakeCheckNumberCard(
        string glyph,
        string title,
        string description,
        string checkText,
        bool checkedValue,
        Action<bool> checkedSetter,
        string numberLabel,
        int value,
        int min,
        int max,
        Action<int> valueSetter)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = 310,
            Height = 34,
            BackColor = Color.Transparent,
        };
        var check = new FluentToggleSwitch
        {
            Text = checkText,
            Checked = checkedValue,
            Width = 130,
            Height = 28,
            Font = FluentTheme.TextFontPx(14),
            Cursor = Cursors.Hand,
        };
        var label = new Label
        {
            Text = numberLabel,
            Width = 58,
            Height = 28,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            Font = FluentTheme.TextFontPx(13),
        };
        var input = new FluentNumberBox
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Increment = Math.Max(1, Math.Min(60, max - min)),
            Width = 86,
            Height = 28,
            Font = FluentTheme.TextFontPx(13),
        };
        check.CheckedChanged += (_, _) => checkedSetter(check.Checked);
        input.ValueChanged += (_, _) => valueSetter((int)input.Value);
        panel.Controls.Add(check);
        panel.Controls.Add(label);
        panel.Controls.Add(input);
        return MakeCard(glyph, title, description, panel, height: 122);
    }

    private Control MakeTwoNumberCard(
        string glyph,
        string title,
        string description,
        string firstLabel,
        int firstValue,
        int firstMin,
        int firstMax,
        Action<int> firstSetter,
        string secondLabel,
        int secondValue,
        int secondMin,
        int secondMax,
        Action<int> secondSetter)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = 330,
            Height = 34,
            BackColor = Color.Transparent,
        };
        panel.Controls.Add(MakeNumericInline(firstLabel, firstValue, firstMin, firstMax, firstSetter));
        panel.Controls.Add(MakeNumericInline(secondLabel, secondValue, secondMin, secondMax, secondSetter));
        return MakeCard(glyph, title, description, panel, height: 122);
    }

    private Control MakeNumericInline(string labelText, int value, int min, int max, Action<int> setter)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = 158,
            Height = 30,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 8, 0),
        };
        var label = new Label
        {
            Text = labelText,
            Width = 70,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            Font = FluentTheme.TextFontPx(13),
        };
        var input = new FluentNumberBox
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 70,
            Height = 28,
            Font = FluentTheme.TextFontPx(13),
        };
        input.ValueChanged += (_, _) => setter((int)input.Value);
        panel.Controls.Add(label);
        panel.Controls.Add(input);
        return panel;
    }

    private Control MakePricingCard()
    {
        _draft.OpenAI.EnsurePricingDefaults();
        var panel = new TableLayoutPanel
        {
            Width = 500,
            Height = 100,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Color.Transparent,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        var top = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
        };
        var bottom = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
        };

        var combo = new FluentComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 174,
            Height = 30,
            Font = FluentTheme.TextFontPx(13),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 12, 0),
        };
        combo.Items.AddRange(PricingModelOptions().Cast<object>().ToArray());
        combo.Text = _draft.OpenAI.TokenPricingModel;

        var rateInput = MakeDecimalInput(_draft.OpenAI.UsdToCnyRate, 0.1, 50, 3, 0.01m, value => _draft.OpenAI.UsdToCnyRate = value);
        rateInput.Margin = new Padding(8, 2, 0, 0);
        top.Controls.Add(MakeInlineLabel("换算模型", 70));
        top.Controls.Add(combo);
        top.Controls.Add(MakeInlineLabel("美元兑人民币", 86));
        top.Controls.Add(rateInput);

        var inputPrice = MakeDecimalInput(0, 0, 10_000, 3, 0.05m, _ => { });
        var cachedPrice = MakeDecimalInput(0, 0, 10_000, 3, 0.05m, _ => { });
        var outputPrice = MakeDecimalInput(0, 0, 10_000, 3, 0.05m, _ => { });
        bottom.Controls.Add(MakeDecimalInline("Input / 1M", inputPrice));
        bottom.Controls.Add(MakeDecimalInline("Cached / 1M", cachedPrice));
        bottom.Controls.Add(MakeDecimalInline("Output / 1M", outputPrice));

        var loading = false;
        void LoadPreset()
        {
            loading = true;
            _draft.OpenAI.TokenPricingModel = string.IsNullOrWhiteSpace(combo.Text) ? "gpt-5.5" : combo.Text.Trim();
            var preset = _draft.OpenAI.GetOrCreateTokenPricePreset(_draft.OpenAI.TokenPricingModel);
            SetDecimalValue(inputPrice, preset.InputUsdPerMillion);
            SetDecimalValue(cachedPrice, preset.CachedInputUsdPerMillion);
            SetDecimalValue(outputPrice, preset.OutputUsdPerMillion);
            loading = false;
        }

        inputPrice.ValueChanged += (_, _) =>
        {
            if (!loading) _draft.OpenAI.GetOrCreateTokenPricePreset().InputUsdPerMillion = (double)inputPrice.Value;
        };
        cachedPrice.ValueChanged += (_, _) =>
        {
            if (!loading) _draft.OpenAI.GetOrCreateTokenPricePreset().CachedInputUsdPerMillion = (double)cachedPrice.Value;
        };
        outputPrice.ValueChanged += (_, _) =>
        {
            if (!loading) _draft.OpenAI.GetOrCreateTokenPricePreset().OutputUsdPerMillion = (double)outputPrice.Value;
        };
        combo.SelectedIndexChanged += (_, _) => LoadPreset();
        combo.TextChanged += (_, _) => LoadPreset();
        LoadPreset();

        panel.Controls.Add(top, 0, 0);
        panel.Controls.Add(bottom, 0, 1);
        return MakeCard(
            FluentIcons.Insights,
            "Token 换算预设",
            "按所选模型的 input、cached input、output 单价估算美元和人民币价值；数值单位为美元 / 100 万 tokens。",
            panel,
            height: 224);
    }

    private static string[] PricingModelOptions()
    {
        return TokenPricePreset.CreateDefaults()
            .Keys
            .Concat(CodexBarConfig.AvailableModels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Label MakeInlineLabel(string text, int width)
    {
        return new Label
        {
            Text = text,
            Width = width,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            Font = FluentTheme.TextFontPx(12),
            Margin = new Padding(0, 2, 4, 0),
        };
    }

    private static Control MakeDecimalInline(string label, FluentNumberBox input)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = 160,
            Height = 36,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 8, 0),
        };
        panel.Controls.Add(MakeInlineLabel(label, 72));
        input.Margin = new Padding(0, 2, 0, 0);
        panel.Controls.Add(input);
        return panel;
    }

    private static FluentNumberBox MakeDecimalInput(double value, double min, double max, int decimals, decimal increment, Action<double> setter)
    {
        var input = new FluentNumberBox
        {
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            DecimalPlaces = decimals,
            Increment = increment,
            Width = 84,
            Height = 28,
            Font = FluentTheme.TextFontPx(12),
            Value = ClampDecimal(value, min, max),
        };
        input.ValueChanged += (_, _) => setter((double)input.Value);
        return input;
    }

    private static void SetDecimalValue(FluentNumberBox input, double value)
    {
        input.Value = ClampDecimal(value, (double)input.Minimum, (double)input.Maximum);
    }

    private static decimal ClampDecimal(double value, double min, double max)
    {
        if (!double.IsFinite(value))
        {
            value = min;
        }

        return (decimal)Math.Clamp(value, min, max);
    }

    private Control MakeComboCard(string glyph, string title, string description, string[] values, string selected, Action<string> setter)
    {
        var combo = new FluentComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 220,
            Height = 30,
            Font = FluentTheme.TextFontPx(14),
            Cursor = Cursors.Hand,
        };
        combo.Items.AddRange(values.Cast<object>().ToArray());
        combo.Text = selected;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(combo.Text))
            {
                setter(combo.Text.Trim());
            }
        };
        combo.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(combo.Text))
            {
                setter(combo.Text.Trim());
            }
        };
        return MakeCard(glyph, title, description, combo, height: 118);
    }

    private Control MakeRadioCard<T>(
        string glyph,
        string title,
        (string Label, string Description, T Value, bool Enabled)[] options,
        T current,
        Action<T> setter)
        where T : notnull
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = 420,
            Height = 34,
            BackColor = Color.Transparent,
        };

        var buttons = new System.Collections.Generic.List<FluentButton>();
        var selectedValue = current;
        void RefreshButtons()
        {
            foreach (var button in buttons)
            {
                var option = ((string Label, string Description, T Value, bool Enabled))button.Tag!;
                var selected = Equals(option.Value, selectedValue);
                button.Primary = selected;
                button.BackColor = selected ? FluentTheme.Accent : FluentTheme.ControlBackground;
                button.ForeColor = selected ? FluentTheme.TextOnAccent : FluentTheme.TextPrimary;
                button.Enabled = option.Enabled;
                button.Invalidate();
            }
        }

        foreach (var option in options)
        {
            var optionWidth = Math.Max(106, TextRenderer.MeasureText(option.Label, FluentTheme.TextFontPx(14)).Width + 34);
            var button = new FluentButton
            {
                Text = option.Label,
                Enabled = option.Enabled,
                Width = optionWidth,
                Height = 28,
                BackColor = FluentTheme.ControlBackground,
                ForeColor = FluentTheme.TextPrimary,
                Font = FluentTheme.TextFontPx(14),
                Margin = new Padding(0, 0, 10, 0),
                Cursor = option.Enabled ? Cursors.Hand : Cursors.Default,
                Tag = option,
            };
            button.Click += (_, _) =>
            {
                if (button.Enabled)
                {
                    selectedValue = option.Value;
                    setter(option.Value);
                    RefreshButtons();
                }
            };
            _toolTip.SetToolTip(button, option.Description);
            buttons.Add(button);
            panel.Controls.Add(button);
        }

        RefreshButtons();

        var hint = string.Join("  |  ", options.Select(option => $"{option.Label}: {option.Description}"));
        return MakeCard(glyph, title, hint, panel, height: 132);
    }

    private Control MakeCard(string glyph, string title, string description, Control action, int height = 108)
    {
        var card = new SettingCard(glyph, title, description)
        {
            Height = height,
        };
        action.Margin = new Padding(0);
        card.Action = action;
        return card;
    }

    private void ResizeCards()
    {
        var width = Math.Max(520, _scrollHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10);
        _content.SuspendLayout();
        _content.Width = width;
        var top = 0;
        foreach (Control control in _content.Controls)
        {
            control.SetBounds(0, top, width, control.Height);
            top += control.Height + 12;
        }
        _content.Height = Math.Max(1, top + 8);
        _content.ResumeLayout(true);
        _scrollHost.AutoScrollMinSize = new Size(width, _content.Height);
    }

    private void SaveSettings()
    {
        if (_draft.OpenAI.DangerThresholdPercent < _draft.OpenAI.WarningThresholdPercent)
        {
            _draft.OpenAI.DangerThresholdPercent = Math.Min(100, _draft.OpenAI.WarningThresholdPercent + 10);
        }

        _draft.OpenAI.EnsurePricingDefaults();
        _configStore.Update(config =>
        {
            config.Global.DefaultModel = _draft.Global.DefaultModel;
            config.Global.ReviewModel = _draft.Global.ReviewModel;
            config.Global.ReasoningEffort = _draft.Global.ReasoningEffort;
            config.Global.ServiceTier = _draft.Global.ServiceTier;
            config.OpenAI.UsageDisplayMode = _draft.OpenAI.UsageDisplayMode;
            config.OpenAI.TokenUnitDisplayMode = _draft.OpenAI.TokenUnitDisplayMode;
            config.OpenAI.AccountUsageMode = _draft.OpenAI.AccountUsageMode;
            config.OpenAI.AutoRefreshEnabled = _draft.OpenAI.AutoRefreshEnabled;
            config.OpenAI.AutoRefreshIntervalSeconds = Math.Max(60, _draft.OpenAI.AutoRefreshIntervalSeconds);
            config.OpenAI.WarningThresholdPercent = _draft.OpenAI.WarningThresholdPercent;
            config.OpenAI.DangerThresholdPercent = _draft.OpenAI.DangerThresholdPercent;
            config.OpenAI.TokenPricingModel = _draft.OpenAI.TokenPricingModel;
            config.OpenAI.UsdToCnyRate = _draft.OpenAI.UsdToCnyRate;
            config.OpenAI.TokenPricePresets = _draft.OpenAI.CloneTokenPricePresets();
            config.OpenAI.EnsurePricingDefaults();
            config.KeepAwakeEnabled = _draft.KeepAwakeEnabled;
            config.AdvancedKeepAwakeEnabled = _draft.AdvancedKeepAwakeEnabled;
            config.AdvancedKeepAwakeIdleThresholdMs = Math.Clamp(_draft.AdvancedKeepAwakeIdleThresholdMs, 5_000, 3_600_000);
            config.AdvancedKeepAwakeIntervalMs = Math.Clamp(_draft.AdvancedKeepAwakeIntervalMs, 1_000, 600_000);
            config.AdvancedKeepAwakeJitterMs = Math.Clamp(_draft.AdvancedKeepAwakeJitterMs, 0, 120_000);
            config.AdvancedKeepAwakeMovePattern = NormalizeMovePattern(_draft.AdvancedKeepAwakeMovePattern);
            config.AdvancedKeepAwakePauseOnFullscreen = _draft.AdvancedKeepAwakePauseOnFullscreen;
            config.StartWithWindows = _draft.StartWithWindows;
        });

        _keepAwakeService.SetEnabled(_draft.KeepAwakeEnabled);
        _keepAwakeService.SetAdvancedEnabled(_draft.AdvancedKeepAwakeEnabled);
        StartupService.SetEnabled(_draft.StartWithWindows);
        _onSettingsChanged();
        _saveStatusLabel.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
        _saveButton.Text = "已保存";
        var resetTimer = new System.Windows.Forms.Timer { Interval = 1600 };
        resetTimer.Tick += (_, _) =>
        {
            resetTimer.Stop();
            resetTimer.Dispose();
            if (!IsDisposed)
            {
                _saveButton.Text = "保存";
            }
        };
        resetTimer.Start();
    }

    private static void ConfigureButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.Height = 32;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(6, 0, 0, 0);
        button.Font = FluentTheme.TextFontPx(15, primary ? FontStyle.Bold : FontStyle.Regular);
        FluentTheme.ApplyButton(button, primary);
    }

    private static string NormalizeMovePattern(string? pattern)
    {
        return (pattern ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "micro_jitter" => "micro_jitter",
            "random_walk_box" => "random_walk_box",
            _ => "ping_pong",
        };
    }

    private static string MovePatternLabel(string? pattern)
    {
        return NormalizeMovePattern(pattern) switch
        {
            "micro_jitter" => "细微随机",
            "random_walk_box" => "小范围游走",
            _ => "轻量往返",
        };
    }

    private static string MovePatternValue(string label)
    {
        return label switch
        {
            "细微随机" => "micro_jitter",
            "小范围游走" => "random_walk_box",
            _ => "ping_pong",
        };
    }

    private static void DrawRoundedPanel(Graphics graphics, Rectangle bounds, Color fill, Color stroke, int radius)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1);
        using var path = FluentTheme.RoundedRectanglePath(rect, radius);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        using var pen = new Pen(stroke);
        graphics.DrawPath(pen, path);
    }
}
