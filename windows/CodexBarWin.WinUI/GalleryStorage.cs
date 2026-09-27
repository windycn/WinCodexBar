using System.Collections.Concurrent;
using System.Text.Json;

namespace CodexBarWin.WinUI;

public sealed record GalleryIndexEntry(string FileName, string MetadataFile, DateTimeOffset IndexedAt);

/// <summary>Keeps each gallery's media, metadata, index and recoverable trash in separate folders.</summary>
public static class GalleryStorage
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static string MetadataPath(string root) => Path.Combine(root, "metadata");
    public static string TrashPath(string root) => Path.Combine(root, "trash");
    public static string IndexPath(string root) => Path.Combine(MetadataPath(root), "index.json");

    public static IReadOnlyList<T> Load<T>(string root, string extension, Func<T, string> getPath)
    {
        root = Path.GetFullPath(root);
        lock (Gate(root))
        {
            Ensure(root);
            MigrateLegacyMetadata(root);
            var items = new List<T>();
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var metadata in Directory.EnumerateFiles(MetadataPath(root), "*.json"))
            {
                if (Path.GetFileName(metadata).Equals("index.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<T>(File.ReadAllText(metadata));
                    if (item is null) { MoveToTrash(root, metadata); continue; }
                    var path = Path.GetFullPath(getPath(item));
                    if (!IsDirectChild(root, path) || !File.Exists(path))
                    {
                        MoveToTrash(root, metadata);
                        continue;
                    }
                    items.Add(item);
                    referenced.Add(path);
                }
                catch { MoveToTrash(root, metadata); }
            }
            foreach (var path in Directory.EnumerateFiles(root, "*" + extension))
            {
                if (referenced.Contains(Path.GetFullPath(path))) continue;
                // Concurrent generation may have written the media before its metadata.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= TimeSpan.FromMinutes(2))
                    MoveToTrash(root, path);
            }
            var entries = items.Select(item => new GalleryIndexEntry(Path.GetFileName(getPath(item)),
                Path.GetFileNameWithoutExtension(getPath(item)) + ".json", DateTimeOffset.Now)).ToArray();
            WriteAtomic(IndexPath(root), JsonSerializer.Serialize(entries));
            return items;
        }
    }

    public static void SaveMedia<T>(string root, string mediaPath, byte[] bytes, T metadata)
    {
        root = Path.GetFullPath(root);
        if (!IsDirectChild(root, mediaPath)) throw new InvalidOperationException("图库文件必须保存在图库根目录。 ");
        lock (Gate(root))
        {
            Ensure(root);
            var pending = mediaPath + ".pending";
            File.WriteAllBytes(pending, bytes);
            File.Move(pending, mediaPath, true);
            WriteAtomic(Path.Combine(MetadataPath(root), Path.GetFileNameWithoutExtension(mediaPath) + ".json"),
                JsonSerializer.Serialize(metadata));
        }
    }

    public static void SaveMetadata<T>(string root, string mediaPath, T metadata)
    {
        root = Path.GetFullPath(root);
        if (!IsDirectChild(root, mediaPath)) throw new InvalidOperationException("图库文件必须保存在图库根目录。");
        lock (Gate(root))
        {
            Ensure(root);
            WriteAtomic(Path.Combine(MetadataPath(root), Path.GetFileNameWithoutExtension(mediaPath) + ".json"),
                JsonSerializer.Serialize(metadata));
        }
    }

    public static void TrashMedia(string root, string mediaPath)
    {
        root = Path.GetFullPath(root);
        if (!IsDirectChild(root, mediaPath)) return;
        lock (Gate(root))
        {
            if (File.Exists(mediaPath)) MoveToTrash(root, mediaPath);
            var metadata = Path.Combine(MetadataPath(root), Path.GetFileNameWithoutExtension(mediaPath) + ".json");
            if (File.Exists(metadata)) MoveToTrash(root, metadata);
        }
    }

    public static int EmptyTrash(string root, int olderThanDays)
    {
        if (olderThanDays < 0) throw new ArgumentOutOfRangeException(nameof(olderThanDays));
        root = Path.GetFullPath(root);
        lock (Gate(root))
        {
            var trash = TrashPath(root);
            if (!Directory.Exists(trash)) return 0;
            var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(trash))
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                File.Delete(file);
                count++;
            }
            return count;
        }
    }

    public static void CopyLibrary<T>(string oldRoot, string newRoot, string extension,
        Func<T, string> getPath, Func<T, string, T> withPath)
    {
        oldRoot = Path.GetFullPath(oldRoot);
        newRoot = Path.GetFullPath(newRoot);
        if (oldRoot.Equals(newRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (newRoot.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            oldRoot.StartsWith(newRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新图库目录不能与旧目录互相嵌套。");
        var items = Load(oldRoot, extension, getPath);
        Ensure(newRoot);
        foreach (var item in items)
        {
            var oldPath = getPath(item);
            var newPath = Path.Combine(newRoot, Path.GetFileName(oldPath));
            if (File.Exists(newPath))
                newPath = Path.Combine(newRoot, GalleryFileName.Create("migrated") + extension);
            if (!File.Exists(newPath)) File.Copy(oldPath, newPath);
            SaveMetadata(newRoot, newPath, withPath(item, newPath));
        }
        var oldTrash = TrashPath(oldRoot);
        if (Directory.Exists(oldTrash))
            foreach (var file in Directory.EnumerateFiles(oldTrash))
            {
                var target = Path.Combine(TrashPath(newRoot), Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
        Load(newRoot, extension, getPath);
    }

    private static object Gate(string root) => Gates.GetOrAdd(root, _ => new object());


    private static void Ensure(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(MetadataPath(root));
        Directory.CreateDirectory(TrashPath(root));
    }

    private static bool IsDirectChild(string root, string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path))?.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) == true;

    private static void MigrateLegacyMetadata(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var destination = Path.Combine(MetadataPath(root), Path.GetFileName(file));
            if (!File.Exists(destination)) File.Move(file, destination);
            else MoveToTrash(root, file);
        }
    }

    private static void MoveToTrash(string root, string path)
    {
        if (!File.Exists(path)) return;
        Ensure(root);
        var name = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}_{Guid.NewGuid().ToString("N")[..6]}_{Path.GetFileName(path)}";
        var destination = Path.Combine(TrashPath(root), name);
        File.Move(path, destination);
        File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
    }

    private static void WriteAtomic(string path, string contents)
    {
        var pending = path + ".pending";
        File.WriteAllText(pending, contents);
        File.Move(pending, path, true);
    }
}
