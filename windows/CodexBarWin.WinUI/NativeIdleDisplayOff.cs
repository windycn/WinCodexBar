using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexBarWin.WinUI;

/// <summary>
/// Uses Windows' ordinary idle display-off path. The legacy SC_MONITORPOWER
/// broadcast can enter a wake loop on some Modern Standby / hybrid GPU PCs.
/// The temporary power-plan edit is journaled and restored after a few seconds.
/// </summary>
internal sealed class NativeIdleDisplayOff
{
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid DisplayTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private readonly string _journalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinCodexBar", "display-timeout-recovery.json");

    public string? RecoveryError { get; }

    public NativeIdleDisplayOff()
    {
        try { Recover(); }
        catch (Exception error) { RecoveryError = error.Message; }
    }

    public Task<bool> BlankAsync(uint inputBefore, CancellationToken token) =>
        Task.Run(() => Blank(inputBefore, token), CancellationToken.None);

    private bool Blank(uint inputBefore, CancellationToken token)
    {
        using var mutex = new Mutex(false, "Local\\WinCodexBarNativeDisplayBlank");
        if (!Acquire(mutex)) return false;
        try
        {
            RecoverWithoutMutex();
            token.ThrowIfCancellationRequested();
            var saved = Read();
            SaveJournal(saved);
            try
            {
                Write(saved.Scheme, 1, 1, applyIfActive: true);
                // An early input restores the original timeout immediately;
                // otherwise the OS turns the panel off after its idle second.
                for (var i = 0; i < 50 && !token.IsCancellationRequested; i++)
                {
                    if (GetLastInputInfo(out var now) && now.Time != inputBefore) break;
                    Thread.Sleep(100);
                }
            }
            finally { Restore(saved); }
        }
        finally { mutex.ReleaseMutex(); }
        return true;
    }

    private void Recover()
    {
        using var mutex = new Mutex(false, "Local\\WinCodexBarNativeDisplayBlank");
        if (!Acquire(mutex)) return;
        try { RecoverWithoutMutex(); }
        finally { mutex.ReleaseMutex(); }
    }

    private void RecoverWithoutMutex()
    {
        if (!File.Exists(_journalPath)) return;
        var saved = JsonSerializer.Deserialize<TimeoutSnapshot>(File.ReadAllText(_journalPath));
        if (saved is null || saved.Scheme == Guid.Empty) return;
        Restore(saved);
    }

    private void Restore(TimeoutSnapshot saved)
    {
        Write(saved.Scheme, saved.AcSeconds, saved.DcSeconds, applyIfActive: true);
        var actual = Read(saved.Scheme);
        if (actual.AcSeconds != saved.AcSeconds || actual.DcSeconds != saved.DcSeconds)
            throw new InvalidOperationException("显示器超时设置未能恢复；下次启动会再次尝试恢复。");
        File.Delete(_journalPath);
    }

    private void SaveJournal(TimeoutSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var temporary = _journalPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, snapshot);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _journalPath, overwrite: true);
    }

    private static TimeoutSnapshot Read(Guid? requestedScheme = null)
    {
        var scheme = requestedScheme ?? ActiveScheme();
        var subgroup = VideoSubgroup;
        var setting = DisplayTimeout;
        Check(PowerReadACValueIndex(0, ref scheme, ref subgroup, ref setting, out var ac));
        Check(PowerReadDCValueIndex(0, ref scheme, ref subgroup, ref setting, out var dc));
        return new TimeoutSnapshot(scheme, ac, dc);
    }

    private static void Write(Guid scheme, uint ac, uint dc, bool applyIfActive)
    {
        var subgroup = VideoSubgroup;
        var setting = DisplayTimeout;
        Check(PowerWriteACValueIndex(0, ref scheme, ref subgroup, ref setting, ac));
        Check(PowerWriteDCValueIndex(0, ref scheme, ref subgroup, ref setting, dc));
        if (applyIfActive && ActiveScheme() == scheme) Check(PowerSetActiveScheme(0, ref scheme));
    }

    private static Guid ActiveScheme()
    {
        Check(PowerGetActiveScheme(0, out var pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static void Check(uint error)
    {
        if (error != 0) throw new InvalidOperationException($"Windows 电源设置操作失败（{error}）。");
    }

    private sealed record TimeoutSnapshot(Guid Scheme, uint AcSeconds, uint DcSeconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }
    private static bool GetLastInputInfo(out LastInputInfo info)
    {
        info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfoNative(ref info);
    }

    [DllImport("user32.dll", EntryPoint = "GetLastInputInfo")]
    private static extern bool GetLastInputInfoNative(ref LastInputInfo info);
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint rootPowerKey, out nint schemePointer);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(nint rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(nint rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(nint rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(nint rootPowerKey, ref Guid scheme);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint pointer);
}
