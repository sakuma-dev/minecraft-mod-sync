using System.Runtime.InteropServices;
using System.Text;

namespace ModSync.Platform.Prism;

/// <summary>実在するローカルパスだけを解決する。検査後の置換（TOCTOU）は再照合で緩和する。</summary>
public static class SafePathResolver
{
    public static void ValidateAbsolute(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.StartsWith("\\", StringComparison.Ordinal) || path[2..].Contains(':') || path.Any(char.IsControl))
            throw new IOException("ドライブ指定の絶対ローカルパスが必要です。");
        if (path.Length == 3) return;
        foreach (var segment in path[3..].Replace('/', '\\').TrimEnd('\\').Split('\\')) ValidateSegment(segment);
    }
    public static void ValidateSegment(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        if (string.IsNullOrEmpty(segment) || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) ||
            stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(stem[3]))) throw new IOException("安全に解決できないパス階層です。");
    }
    public static string ResolveExisting(string path)
    {
        ValidateAbsolute(path);
        var full = Path.GetFullPath(path);
        if (full.Length > 3) full = full.TrimEnd('\\');
        var longName = new StringBuilder(32768);
        // 入力の特殊パスは拒否したまま、Win32呼び出しにだけ拡張長の接頭辞を付ける。
        var length = GetLongPathName(ToExtendedPath(full), longName, (uint)longName.Capacity);
        if (length == 0)
            throw new IOException($"実パスを正規化できません。Win32={Marshal.GetLastWin32Error()}, path={full}, exists={Path.Exists(full)}");
        if (length >= longName.Capacity) throw new IOException("実パスが正規化バッファーの上限を超えています。");
        // 台帳・比較・Prismへの引数は従来どおりのローカル絶対パスで統一する。
        full = longName.ToString();
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
        ValidateAbsolute(full);
        var current = full[..3];
        RejectReparse(current);
        foreach (var segment in full[3..].TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var found = Directory.EnumerateFileSystemEntries(ToExtendedPath(current)).SingleOrDefault(p =>
                StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(p), segment));
            if (found == null) throw new FileNotFoundException("パスが存在しません。", full);
            current = found[4..];
            RejectReparse(current);
        }
        return current.Length == 3 ? current : current.TrimEnd('\\');
    }
    public static string ResolveChild(string root, string relative)
    {
        foreach (var part in relative.Replace('/', '\\').Split('\\')) ValidateSegment(part);
        var normalizedRoot = ResolveExisting(root);
        var path = ResolveExisting(Path.Combine(normalizedRoot, relative.Replace('/', '\\')));
        if (!path.StartsWith(normalizedRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("領域外のパスです。");
        return path;
    }
    public static void RejectReparse(string path)
    {
        if ((File.GetAttributes(ToExtendedPath(path)) & FileAttributes.ReparsePoint) != 0) throw new IOException("リパースポイントは扱えません。");
    }
    internal static string ToExtendedPath(string path)
    {
        ValidateAbsolute(path);
        return @"\\?\" + Path.GetFullPath(path);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint length);
}
