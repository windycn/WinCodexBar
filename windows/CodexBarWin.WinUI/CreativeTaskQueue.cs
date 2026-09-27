namespace CodexBarWin.WinUI;

public sealed record CreativeTaskSnapshot(string Id, string Kind, string Label, string Status,
    DateTimeOffset CreatedAt)
{
    public string Display => $"{Kind} · {Label} · {Status}";
}

/// <summary>One local queue shared by image generation and SVG conversion.</summary>
public sealed class CreativeTaskQueue
{
    private readonly SemaphoreSlim _slots = new(6, 6);
    private readonly object _gate = new();
    private readonly List<CreativeTaskSnapshot> _items = [];
    public static CreativeTaskQueue Shared { get; } = new();
    public event Action? Changed;
    public bool IsBusy
    {
        get { lock (_gate) return _items.Any(item => item.Status is "排队中" or "运行中"); }
    }

    public IReadOnlyList<CreativeTaskSnapshot> Snapshot()
    {
        lock (_gate) return _items.OrderByDescending(item => item.CreatedAt).Take(30).ToArray();
    }

    public async Task<T> EnqueueAsync<T>(string kind, string label, Func<CancellationToken, Task<T>> run,
        CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        Change(id, kind, label, "排队中");
        var acquired = false;
        try
        {
            await _slots.WaitAsync(token);
            acquired = true;
            Change(id, kind, label, "运行中");
            var result = await run(token);
            Change(id, kind, label, "已完成");
            return result;
        }
        catch (OperationCanceledException)
        {
            Change(id, kind, label, "已取消");
            throw;
        }
        catch
        {
            Change(id, kind, label, "失败");
            throw;
        }
        finally { if (acquired) _slots.Release(); }
    }

    private void Change(string id, string kind, string label, string status)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == id);
            if (index < 0) _items.Add(new CreativeTaskSnapshot(id, kind, label, status, DateTimeOffset.Now));
            else _items[index] = _items[index] with { Status = status };
            if (_items.Count > 100)
                _items.RemoveAll(item => item.Status is "已完成" or "失败" or "已取消"
                    && item.CreatedAt < DateTimeOffset.Now.AddHours(-1));
        }
        Changed?.Invoke();
    }
}
