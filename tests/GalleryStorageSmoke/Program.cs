using CodexBarWin.WinUI;

var temp = Path.Combine(Path.GetTempPath(), "wcb-gallery-qa-" + Guid.NewGuid().ToString("N"));
var first = Path.Combine(temp, "first");
var second = Path.Combine(temp, "second");
try
{
    var media = Path.Combine(first, GalleryFileName.Create("test") + ".png");
    GalleryStorage.SaveMedia(first, media, [1, 2, 3], new TestItem(media, "original"));
    if (GalleryStorage.Load<TestItem>(first, ".png", x => x.Path).Count != 1) throw new Exception("Save/load failed");
    if (!File.Exists(GalleryStorage.IndexPath(first))) throw new Exception("Index missing");
    GalleryStorage.CopyLibrary<TestItem>(first, second, ".png", x => x.Path, (x, path) => x with { Path = path });
    if (GalleryStorage.Load<TestItem>(second, ".png", x => x.Path).Count != 1) throw new Exception("Copy failed");
    File.WriteAllBytes(Path.Combine(second, Path.GetFileName(media)), [4, 5, 6]);
    GalleryStorage.CopyLibrary<TestItem>(first, second, ".png", x => x.Path, (x, path) => x with { Path = path });
    if (GalleryStorage.Load<TestItem>(second, ".png", x => x.Path).Count != 2) throw new Exception("Name collision lost an image");
    var orphan = Path.Combine(first, "orphan.png");
    File.WriteAllBytes(orphan, [7, 8, 9]);
    File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddMinutes(-3));
    GalleryStorage.Load<TestItem>(first, ".png", x => x.Path);
    if (File.Exists(orphan) || !Directory.EnumerateFiles(GalleryStorage.TrashPath(first), "*orphan.png").Any())
        throw new Exception("Orphan was not moved to trash");
    GalleryStorage.TrashMedia(first, media);
    if (File.Exists(media) || GalleryStorage.Load<TestItem>(first, ".png", x => x.Path).Count != 0)
        throw new Exception("Delete did not move both files to trash");
    var cleaned = GalleryStorage.EmptyTrash(first, 0);
    if (cleaned < 3) throw new Exception("Trash cleanup failed");
    Console.WriteLine("Gallery storage, index, migration collision, orphan recovery and trash cleanup passed");
}
finally
{
    if (Directory.Exists(temp)) Directory.Delete(temp, true);
}

internal sealed record TestItem(string Path, string Label);
