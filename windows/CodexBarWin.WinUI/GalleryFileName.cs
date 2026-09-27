namespace CodexBarWin.WinUI;

public static class GalleryFileName
{
    public static string Create(string model)
    {
        var safeModel = new string(model.ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .Take(48).ToArray()).Trim('-');
        if (safeModel.Length == 0) safeModel = "image";
        return $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}_{safeModel}_{Guid.NewGuid().ToString("N")[..6]}";
    }
}
