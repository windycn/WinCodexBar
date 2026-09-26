using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.UI;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    private static int _passed;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); _passed++; }
    private static object? Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static void Paint(Form form, string? file = null)
    {
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        if (file is not null) bitmap.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", file));
    }
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var home = Path.Combine(Path.GetTempPath(), "WinCodexBar UI ' " + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEXBAR_HOME", home);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"screenshots"));
        try
        {
            var store = new CodexBarConfigStore();
            var wake = new KeepAwakeService(store); // 不 Load，不改变机器唤醒状态。
            foreach (var scale in new[] {100,150,200,250})
            {
                AppAppearance.ScalePercent = scale;
                using var settings = new SettingsForm(store, wake, () => {}, () => {});
                settings.Show();
                Check(Math.Abs(settings.Font.Size - 14f * scale / 100f) < 0.5f, $"initial DPI scale applied before first frame {scale}%");
                if (scale == 100) ProbeFont(settings.Font);
                Console.WriteLine($"Text font: {settings.Font.Name}; family: {settings.Font.FontFamily.Name}; style: {settings.Font.Style}; size: {settings.Font.Size}");
                foreach (var size in new[] {new Size(800,600),new Size(1366,768),new Size(1920,1080)})
                {
                    settings.Size = size; settings.ApplyAppearance();
                    var pageType = typeof(SettingsForm).GetNestedType("Page", BindingFlags.NonPublic)!;
                    foreach (var page in Enum.GetValues(pageType))
                    {
                        Call(settings,"SelectPage",page!); Paint(settings,$"settings-{scale}-{page}.png");
                        Check(!((ScrollableControl)settings.Controls[0]).HorizontalScroll.Visible, $"settings has no horizontal overflow {scale}% {page}");
                        foreach (var button in Descendants(settings).OfType<Button>().Where(b => b.Text is "保存" or "取消" or "配置目录"))
                        {
                            var visible = settings.RectangleToScreen(settings.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle));
                            if (!visible)
                            {
                                Console.WriteLine($"FORM {settings.Bounds} client={settings.ClientRectangle} font={settings.Font.Size} BUTTON {button.Bounds}");
                                for (Control? ancestor = button.Parent; ancestor is not null; ancestor = ancestor.Parent)
                                {
                                    Console.WriteLine($"ANCESTOR {ancestor.GetType().Name} bounds={ancestor.Bounds} min={ancestor.MinimumSize} font={ancestor.Font.Size}");
                                    if (ancestor is TableLayoutPanel table) Console.WriteLine("COLUMNS " + string.Join(",", table.GetColumnWidths()) + " ROWS " + string.Join(",",table.GetRowHeights()) + " STYLES " + string.Join(",",table.ColumnStyles.Cast<ColumnStyle>().Select(c => c.SizeType + "=" + c.Width)));
                                }
                            }
                            Check(visible, $"settings footer visible {scale}% {button.Text}");
                        }
                        Check(Screen.FromControl(settings).WorkingArea.Contains(settings.Bounds), $"settings fit {scale}% {size} {page}");
                        foreach (var card in Descendants(settings).OfType<SettingCard>())
                        {
                            if (card.Action is not { } action) continue;
                            Check(card.ClientRectangle.Contains(action.Bounds), $"action within card {scale}% {page}");
                            foreach (var label in card.Controls.OfType<Label>().Where(label => !ReferenceEquals(label, action))) Check(!label.Bounds.IntersectsWith(action.Bounds), $"text does not cover action {scale}% {page}");
                        }
                    }
                }
                Paint(settings,$"settings-{scale}.png");
                settings.Close();
            }
            AppAppearance.ScalePercent = 100;
            var registry = new AccountRegistry();
            for (var i=0;i<6;i++) registry.UpsertAccount(new TokenAccount { AccountId="fake-"+i, Email=$"person-{i}@example.test", PrimaryWindowAvailable=i!=0, SecondaryWindowAvailable=true, PrimaryUsedPercent=12, SecondaryUsedPercent=42, SecondaryLimitWindowSeconds=604800, SecondaryResetAt=DateTimeOffset.Parse("2026-10-04T08:01:02Z"), ResetCreditsAvailable=3, ResetCreditsCheckedAt=DateTimeOffset.UtcNow, ResetCreditDetails=new() { new() { ExpirationKnown=true, ExpiresAt=DateTimeOffset.Parse("2026-10-04T08:00:00Z") }, new() { ExpirationKnown=true, ExpiresAt=DateTimeOffset.Parse("2026-10-05T08:00:00Z") } } },i==0);
            using (var dashboard = new CodexBarDashboardForm(registry,store,new UsageRefreshCoordinator(new OpenAIUsageService(),new OpenAIOAuthRefreshService(),registry),_=>{},()=>{},()=>{},()=>{},()=>{},()=>false,_=>{},_=>{}))
            {
                dashboard.Show(); dashboard.Size=new Size(960,680); dashboard.ApplyAppearance(); Paint(dashboard,"dashboard.png");
                var accountList = (Control)Field(dashboard,"_accountList")!;
                var accountRows = accountList.Controls.Cast<Control>().ToArray();
                var unchangedRowPaints = 0;
                accountRows[0].Paint += (_, _) => unchangedRowPaints++;
                dashboard.RefreshData();
                Application.DoEvents();
                Check(accountRows.SequenceEqual(accountList.Controls.Cast<Control>()),"dashboard refresh reuses account row controls");
                Check(unchangedRowPaints==0,"unchanged dashboard refresh does not repaint account rows");
                Check((bool)typeof(Control).GetProperty("DoubleBuffered",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dashboard)!,"dashboard form uses double-buffered painting");
                var createParams=(CreateParams)typeof(Control).GetProperty("CreateParams",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dashboard)!;
                Check((createParams.ExStyle&0x02000000)==0,"dashboard avoids slow whole-window child compositing");
                Check((bool)typeof(Control).GetProperty("DoubleBuffered",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(accountList)!,"account list buffers its own scrolling paint");
                foreach(var button in Descendants(dashboard).OfType<Button>().Where(b=>b.Text is "设置" or "删除" or "导出账号"))
                    Check(button.Parent!.ClientRectangle.Contains(button.Bounds),"dashboard button reachable: "+button.Text);
                dashboard.Close(); Check(!dashboard.Visible && !dashboard.IsDisposed,"dashboard close hides for reuse");
                dashboard.Show(); Check(dashboard.Visible,"dashboard reopens");
            }
            foreach (var detailScale in new[] { 100, 200 })
            {
                AppAppearance.ScalePercent = detailScale;
                using var details = new AccountDetailsForm(registry.Accounts[0], UsageDisplayMode.Used);
                details.Show(); Paint(details, $"account-details-{detailScale}.png");
                Check(Screen.FromControl(details).WorkingArea.Contains(details.Bounds), "account details fits " + detailScale);
                foreach (var button in Descendants(details).OfType<Button>())
                    Check(details.RectangleToScreen(details.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)), "details close button visible " + detailScale);
                Check(Descendants(details).OfType<Button>().All(b => b.Text == "关闭"), "account details has no credit redemption action");
                Check(Descendants(details).OfType<TextBox>().Single().Text.Contains("2026-10-04"), "account details displays full expiry date");
                details.Close();
            }
            AppAppearance.ScalePercent = 100;
            TestPopup(registry);
            TestAwayMode();
            TestInstaller(home);
            Console.WriteLine($"{_passed} Windows checks passed");
        }
        finally
        {
            for (var attempt=0; ; attempt++)
            {
                try { Directory.Delete(home,true); break; }
                catch (Exception ex) when (attempt<20 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
            }
        }
    }
    private static void ProbeFont(Font font)
    {
        using var probe = new Bitmap(700, 220);
        using var g = Graphics.FromImage(probe);
        g.Clear(Color.White);
        using var large = new Font(font.FontFamily, 28, FontStyle.Regular, GraphicsUnit.Pixel);
        g.DrawString("账号设置 Codex 123 (GDI+)", large, Brushes.Black, 10, 10);
        TextRenderer.DrawText(g, "账号设置 Codex 123 (GDI)", large, new Point(10, 65), Color.Black);
        using var named = new Font(font.Name, 28, FontStyle.Regular, GraphicsUnit.Pixel);
        g.DrawString("账号设置 Codex 123 (named GDI+)", named, Brushes.Black, 10, 120);
        probe.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "font-probe.png"));
        var dc = g.GetHdc(); var handle = large.ToHfont(); var old = SelectObject(dc, handle);
        var name = new System.Text.StringBuilder(128); GetTextFace(dc, name.Capacity, name);
        Check(name.ToString() == "WinCodexBar Sans", "Windows selects bundled sans font");
        SelectObject(dc, old); DeleteObject(handle); g.ReleaseHdc(dc);
    }
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] private static extern int GetTextFace(IntPtr dc, int count, System.Text.StringBuilder face);
    private static void TestPopup(AccountRegistry registry)
    {
        var deleted=false;
        var popupClosedBeforeDialog=false;
        var selectedAwayDelay=0;
        var startedAwayDelay=0;
        TrayPopupForm? popup=null;
        popup=new TrayPopupForm(registry.Accounts,registry.ActiveAccountId,()=>false,default,CodexRadarPrediction.Unavailable,new CodexBarConfig(),_=>{},()=>{},()=>{},()=>{},()=>{},()=>{},()=>{},_=>{},()=>{},_=>{},account=>
        {
            popupClosedBeforeDialog = !popup!.Visible || popup.IsDisposed;
            using var dialog=new ConfirmActionDialog("删除账号","只删除测试账号，不影响会话。","删除");
            using var timer=new System.Windows.Forms.Timer {Interval=100};
            timer.Tick+=(_,_)=>
            {
                timer.Stop();
                var confirm=Descendants(dialog).OfType<Button>().Single(b=>b.Text=="删除");
                Check(popupClosedBeforeDialog,"topmost tray popup closes before delete confirmation");
                confirm.PerformClick();
            };
            timer.Start();
            deleted=dialog.ShowDialog()==DialogResult.OK;
        },()=>{},delay=>startedAwayDelay=delay,delay=>selectedAwayDelay=delay,5);
        using(popup)
        {
            var watch=Stopwatch.StartNew();
            popup.ShowNearCursor(); watch.Stop(); Console.WriteLine($"Popup show time: {watch.ElapsedMilliseconds} ms");
            Paint(popup,"popup-unavailable.png");
            popup.UpdateRadarPrediction(CodexRadarPrediction.Unavailable with { IsAvailable = true, WindowOpen = false });
            Paint(popup,"popup.png");
            Check(Screen.FromControl(popup).WorkingArea.Contains(popup.Bounds),"popup fits work area");
            var accountHeight=popup.Height;
            popup.UpdateSnapshot(null,null,new CodexBarConfig(),Array.Empty<TokenAccount>()); Paint(popup);
            Check(popup.Height<accountHeight,"popup resizes after last account removed");
            popup.UpdateSnapshot(registry.ActiveAccountId,null,new CodexBarConfig(),registry.Accounts); Paint(popup);
            var hits=((IEnumerable)Field(popup,"_hits")!).Cast<object>().ToArray();
            var tooltips=hits.Select(h=>(string?)h.GetType().GetField("Tooltip")!.GetValue(h)).ToArray();
            Check(tooltips.Contains("黑屏离开；5 秒后开始；倒计时按 Esc 取消"),"blackout action names its behavior and countdown");
            Check(tooltips.Contains("当前未保持唤醒；点击开启"),"keep-awake action names its current state and effect");
            Check(tooltips.Contains("黑屏前等待 5 秒") && tooltips.Contains("黑屏前等待 15 秒") && tooltips.Contains("黑屏前等待 30 秒"),"quick popup exposes all supported blackout delays");
            Check(new[] {"打开主页","添加 OpenAI 账号","导入账号文件","导出账号","设置","退出 WinCodexBar"}.All(tooltips.Contains),"secondary actions are labeled instead of icon-only");
            var awayHit=hits.First(h=>(string?)h.GetType().GetField("Tooltip")!.GetValue(h)=="黑屏离开；5 秒后开始；倒计时按 Esc 取消");
            var keepAwakeHit=hits.First(h=>(string?)h.GetType().GetField("Tooltip")!.GetValue(h)=="当前未保持唤醒；点击开启");
            var awayBounds=(RectangleF)awayHit.GetType().GetField("Bounds")!.GetValue(awayHit)!;
            var keepAwakeBounds=(RectangleF)keepAwakeHit.GetType().GetField("Bounds")!.GetValue(keepAwakeHit)!;
            Check(Math.Abs(awayBounds.Top-keepAwakeBounds.Top)<0.1f && awayBounds.Width>=keepAwakeBounds.Width,"primary quick actions have consistent, readable button sizing");
            Call(popup,"SetAwayModeDelay",15);
            Check(selectedAwayDelay==15 && (int)Field(popup,"_awayModeDelaySeconds")! == 15,"blackout delay selection updates and persists through the callback");
            ((Action)awayHit.GetType().GetField("Action")!.GetValue(awayHit)!)();
            Check(startedAwayDelay==15,"blackout starts with the selected delay");
            Paint(popup,"popup.png");
            hits=((IEnumerable)Field(popup,"_hits")!).Cast<object>().ToArray();
            Check(hits.Any(h=>(string?)h.GetType().GetField("Tooltip")!.GetValue(h)=="黑屏前等待 15 秒"),"selected blackout delay remains available in the quick popup");
            var hit=hits.First(h=>(string?)h.GetType().GetField("Tooltip")!.GetValue(h)=="删除此账号");
            var rect=(RectangleF)hit.GetType().GetField("Bounds")!.GetValue(hit)!;
            var point=new Point((int)(rect.Left+rect.Width/2),(int)(rect.Top+rect.Height/2));
            Call(popup,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,point.X,point.Y,0));
            Call(popup,"OnMouseUp",new MouseEventArgs(MouseButtons.Right,1,point.X,point.Y,0));
            Check(!deleted,"right click does not delete");
            Call(popup,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));
            Paint(popup);
            Call(popup,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));
            Check(deleted && popupClosedBeforeDialog && (popup.IsDisposed || !popup.Visible),"delete confirmation accepts after closing the topmost popup");
        }
    }

    private static void TestAwayMode()
    {
        using var awayMode = new AwayModeController();
        awayMode.Start(5);
        var countdown = Application.OpenForms.Cast<Form>().Single(form => form.GetType().Name == "AwayModeCountdownForm");
        Check(awayMode.IsCountdownPending && !awayMode.IsActive && countdown.TopMost,"away mode shows a non-blocking countdown before blacking out");
        Check(Descendants(countdown).OfType<Label>().Any(label => label.Text.Contains("5 秒后进入黑屏"))
              && Descendants(countdown).OfType<Label>().Any(label => label.Text.Contains("不会锁屏") && label.Text.Contains("不会中断 Codex")),
            "countdown explains the delay, wake behavior, and no-lock behavior");
        awayMode.CancelPendingStart();
        Check(!awayMode.IsCountdownPending && !awayMode.IsActive,"countdown can be cancelled without activating blackout");
        awayMode.Start();
        Check(awayMode.IsActive,"away mode starts without changing Windows lock state");
        var overlays = Application.OpenForms.Cast<Form>().Where(form => form.GetType().Name == "AwayModeOverlayForm").ToArray();
        Check(overlays.Length == Screen.AllScreens.Length,"away mode covers every display");
        Check(overlays.All(form => form.TopMost && form.FormBorderStyle == FormBorderStyle.None && form.BackColor == Color.Black && !form.ShowInTaskbar),"away mode uses black topmost overlays");

        var origin = Cursor.Position;
        var primary = overlays.Single(form => form.Bounds.Contains(origin));
        Call(primary,"OnKeyPressed",primary,new KeyEventArgs(Keys.Space));
        Check(!awayMode.IsActive,"pressing a key wakes away mode");

        awayMode.Start();
        overlays = Application.OpenForms.Cast<Form>().Where(form => form.GetType().Name == "AwayModeOverlayForm").ToArray();
        primary = overlays.Single(form => form.Bounds.Contains(Cursor.Position));
        Call(primary,"OnPointerMoved",primary,new MouseEventArgs(MouseButtons.None,0,origin.X + 3 - primary.Bounds.Left,origin.Y - primary.Bounds.Top,0));
        Check(!awayMode.IsActive,"moving the mouse wakes away mode");
    }
    private static void TestInstaller(string home)
    {
        var source=Path.Combine(home,"package");var target=Path.Combine(home,"installed");var data=Path.Combine(home,"data");
        Directory.CreateDirectory(source);Directory.CreateDirectory(target);Directory.CreateDirectory(data);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"where.exe"),Path.Combine(source,"WinCodexBar.exe"));
        File.WriteAllText(Path.Combine(target,"WinCodexBar.exe"),"old-program");
        File.WriteAllText(Path.Combine(target,"unrelated.txt"),"preserve");
        File.WriteAllText(Path.Combine(data,"windows_accounts.json"),"fake-account-data");
        var script=Path.Combine(home,"install.ps1");
        File.WriteAllText(script,AppUpdateService.BuildInstallScript(source,target,data,int.MaxValue).Replace("-WorkingDirectory $target", "-WorkingDirectory $target -Wait"),new System.Text.UTF8Encoding(true));
        var start=new ProcessStartInfo("powershell.exe") {UseShellExecute=false};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script}) start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;Check(process.WaitForExit(30000)&&process.ExitCode==0,"installer completes on Windows");
        Check(File.ReadAllText(Path.Combine(home,"result.txt")).Contains("更新完成"),"installer reports success");
        Check(File.ReadAllText(Path.Combine(home,"previous-program","WinCodexBar.exe"))=="old-program","old program backed up");
        Check(File.ReadAllText(Path.Combine(target,"unrelated.txt"))=="preserve","unrelated installation files preserved");
        Check(Directory.GetFiles(Path.Combine(data,"backups"),"windows_accounts.json",SearchOption.AllDirectories).Length==1,"account data backed up");
        Check(File.ReadAllText(Path.Combine(data,"windows_accounts.json"))=="fake-account-data","account data preserved");
        File.WriteAllText(Path.Combine(target,"WinCodexBar.exe"),"rollback-original");
        var failingScript=AppUpdateService.BuildInstallScript(source,target,data,int.MaxValue)
            .Replace("Copy-Item -LiteralPath $file.FullName -Destination $dest -Force", "Copy-Item -LiteralPath $file.FullName -Destination $dest -Force; throw 'injected replacement failure'")
            .Replace("[System.Windows.Forms.MessageBox]::Show($message, 'WinCodexBar 更新') | Out-Null", "'failure recorded' | Out-Null");
        File.WriteAllText(script,failingScript,new System.Text.UTF8Encoding(true));
        using var failedProcess=Process.Start(start)!;
        Check(failedProcess.WaitForExit(30000),"rollback script completes");
        Check(File.ReadAllText(Path.Combine(home,"result.txt")).Contains("更新失败"),"replacement failure reported");
        Check(File.ReadAllText(Path.Combine(target,"WinCodexBar.exe"))=="rollback-original","replacement failure restores old program");
        Check(File.ReadAllText(Path.Combine(data,"windows_accounts.json"))=="fake-account-data","rollback preserves account data");
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public int X,Y; public uint MouseData,Flags,Time; public IntPtr Extra; }
}
