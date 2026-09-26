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
            for (var i=0;i<6;i++) registry.UpsertAccount(new TokenAccount { AccountId="fake-"+i, Email=$"person-{i}@example.test" },i==0);
            using (var dashboard = new CodexBarDashboardForm(registry,store,new UsageRefreshCoordinator(new OpenAIUsageService(),new OpenAIOAuthRefreshService(),registry),_=>{},()=>{},()=>{},()=>{},()=>{},()=>false,_=>{},_=>{}))
            {
                dashboard.Show(); dashboard.Size=new Size(960,680); dashboard.ApplyAppearance(); Paint(dashboard,"dashboard.png");
                foreach(var button in Descendants(dashboard).OfType<Button>().Where(b=>b.Text is "设置" or "删除" or "导出账号"))
                    Check(button.Parent!.ClientRectangle.Contains(button.Bounds),"dashboard button reachable: "+button.Text);
                dashboard.Close(); Check(!dashboard.Visible && !dashboard.IsDisposed,"dashboard close hides for reuse");
                dashboard.Show(); Check(dashboard.Visible,"dashboard reopens");
            }
            TestPopup(registry);
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
        Console.WriteLine("GDI actual font: " + name);
        SelectObject(dc, old); DeleteObject(handle); g.ReleaseHdc(dc);
    }
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] private static extern int GetTextFace(IntPtr dc, int count, System.Text.StringBuilder face);
    private static void TestPopup(AccountRegistry registry)
    {
        var deleted=false;
        TrayPopupForm? popup=null;
        popup=new TrayPopupForm(registry.Accounts,registry.ActiveAccountId,()=>false,default,CodexRadarPrediction.Unavailable,new CodexBarConfig(),_=>{},()=>{},()=>{},()=>{},()=>{},()=>{},()=>{},_=>{},()=>{},_=>{},account=>
        {
            using var dialog=new ConfirmActionDialog("删除账号","只删除测试账号，不影响会话。","删除");
            using var timer=new System.Windows.Forms.Timer {Interval=100};
            timer.Tick+=(_,_)=>
            {
                timer.Stop();
                var confirm=Descendants(dialog).OfType<Button>().Single(b=>b.Text=="删除");
                var point=confirm.PointToScreen(new Point(confirm.Width/2,confirm.Height/2));
                var data=new MouseHookData { X=point.X,Y=point.Y };
                var pointer=Marshal.AllocHGlobal(Marshal.SizeOf<MouseHookData>());
                try { Marshal.StructureToPtr(data,pointer,false); Call(popup!,"MouseHookCallback",0,new IntPtr(0x0201),pointer); Application.DoEvents(); }
                finally {Marshal.FreeHGlobal(pointer);}
                Check(!popup!.IsDisposed,"outside-click hook preserves owned confirmation");
                confirm.PerformClick();
            };
            timer.Start();
            deleted=dialog.ShowDialog(popup)==DialogResult.OK;
        },()=>{});
        using(popup)
        {
            var watch=Stopwatch.StartNew();
            popup.ShowNearCursor(); watch.Stop(); Console.WriteLine($"Popup show time: {watch.ElapsedMilliseconds} ms");
            Paint(popup,"popup.png");
            Check(Screen.FromControl(popup).WorkingArea.Contains(popup.Bounds),"popup fits work area");
            var hits=((IEnumerable)Field(popup,"_hits")!).Cast<object>().ToArray();
            var hit=hits.First(h=>(string?)h.GetType().GetField("Tooltip")!.GetValue(h)=="删除此账号");
            var rect=(RectangleF)hit.GetType().GetField("Bounds")!.GetValue(hit)!;
            var point=new Point((int)(rect.Left+rect.Width/2),(int)(rect.Top+rect.Height/2));
            Call(popup,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,point.X,point.Y,0));
            Call(popup,"OnMouseUp",new MouseEventArgs(MouseButtons.Right,1,point.X,point.Y,0));
            Check(!deleted,"right click does not delete");
            Call(popup,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));
            Paint(popup);
            Call(popup,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));
            Check(deleted && !popup.IsDisposed,"delete confirmation accepts without closing parent");
            popup.UpdateSnapshot(null,null,new CodexBarConfig(),Array.Empty<TokenAccount>()); Paint(popup);
            Check(popup.Height<550,"popup resizes after last account removed");
            popup.Close();
        }
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
