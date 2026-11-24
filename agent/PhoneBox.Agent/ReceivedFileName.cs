using System.Globalization;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// File names from the other device are data: only the last path component is used, nothing that could be a path or a control
/// character survives, and the result is short enough for any file system. The name is never part of a command.
/// </summary>
internal static class ReceivedFileName
{
    public const int MaxBytes = 200;
    public const string Fallback = "file";

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return Fallback;
        var cut = raw.LastIndexOfAny(['/', '\\']);
        var name = cut >= 0 ? raw[(cut + 1)..] : raw;
        var clean = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse) continue;
            clean.Append(rune.ToString());
        }
        var text = clean.ToString().Trim().TrimStart('.').Trim();
        if (text.Length == 0 || text.All(c => c == '.')) return Fallback;
        return Shorten(text);
    }

    /// <summary>Cuts to <see cref="MaxBytes"/> UTF-8 bytes on a code-point boundary, keeping a short extension.</summary>
    private static string Shorten(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxBytes) return name;
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && Encoding.UTF8.GetByteCount(name[dot..]) <= 20 ? name[dot..] : "";
        var stem = extension.Length > 0 ? name[..dot] : name;
        var room = MaxBytes - Encoding.UTF8.GetByteCount(extension);
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in stem.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (used + size > room) break;
            builder.Append(rune.ToString());
            used += size;
        }
        return builder.Length == 0 ? Fallback + extension : builder + extension;
    }

    /// <summary>A name that does not exist in <paramref name="folder"/> yet: <c>a.txt</c>, then <c>a (1).txt</c>, <c>a (2).txt</c>.</summary>
    public static string Unique(string folder, string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var n = 1; ; n++)
        {
            var candidate = $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){extension}";
            if (!exists(candidate)) return candidate;
        }
    }
}
