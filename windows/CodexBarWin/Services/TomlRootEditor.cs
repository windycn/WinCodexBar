using System.Text.RegularExpressions;

namespace CodexBarWin.Services;

/// <summary>仅改 TOML 根字段，不把新增字段追加到最后一个表，也不覆盖 profiles 中的同名键。</summary>
public static class TomlRootEditor
{
    public static string Set(string text, string key, string? value)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>();
        var pattern = new Regex("^[ \\t]*(?:" + Regex.Escape(key) + "|\"" + Regex.Escape(key) + "\"|'" + Regex.Escape(key) + "')[ \\t]*=");
        string? multiline = null;
        var depth = 0;
        var written = false;
        var inTable = false;
        foreach (var line in lines)
        {
            if (!inTable && multiline is null && depth == 0 && line.TrimStart().StartsWith('['))
            {
                if (!written && value is not null) { result.Add(key + " = " + value); written = true; }
                inTable = true;
            }
            if (!inTable && multiline is null && depth == 0 && pattern.IsMatch(line))
            {
                if (!written && value is not null) result.Add(key + " = " + value);
                written = true;
                continue;
            }
            result.Add(line);
            if (!inTable) Scan(line, ref multiline, ref depth);
        }
        if (!written && value is not null)
        {
            if (result.Count > 0 && result[^1] == "") result.RemoveAt(result.Count - 1);
            result.Add(key + " = " + value);
            result.Add("");
        }
        return string.Join(newline, result);
    }

    private static void Scan(string line, ref string? multiline, ref int depth)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (multiline is not null)
            {
                if (line.AsSpan(i).StartsWith(multiline) && (i == 0 || line[i-1] != '\\' || multiline == "'''")) { i += 2; multiline = null; }
                continue;
            }
            if (line[i] == '#') break;
            if (line[i] is '\'' or '"')
            {
                var quote = line[i];
                var triple = new string(quote, 3);
                if (line.AsSpan(i).StartsWith(triple)) { multiline = triple; i += 2; continue; }
                for (++i; i < line.Length; i++)
                {
                    if (quote == '"' && line[i] == '\\') { i++; continue; }
                    if (line[i] == quote) break;
                }
                continue;
            }
            if (line[i] is '[' or '{') depth++;
            if (line[i] is ']' or '}') depth--;
        }
    }
}
