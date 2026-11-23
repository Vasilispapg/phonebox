using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Finds the login code in a message ("Your code is 482913") so it can be put on the clipboard. It answers only when it is
/// sure: exactly one plausible code, with a keyword close by on the same line. Two codes, no keyword, a phone number or a
/// date all give null, because a wrong copy would overwrite what the owner had copied.
/// </summary>
public static partial class OneTimeCode
{
    /// <summary>The most characters allowed between a code and its keyword.</summary>
    private const int MaxGap = 30;

    // 4-8 digits, or two groups split by one space or dash; not touching other letters or digits, and not part of a longer
    // run like a phone number (210-555-1234) or a date (2026-11-23)
    [GeneratedRegex(@"(?<![\p{L}\p{N}]|\d[ -])(?:\d{4,8}|\d{2,4}[ -]\d{2,4})(?![\p{L}\p{N}]|[ -]\d)")]
    private static partial Regex Candidates();

    // run on text folded to lower case with accents removed and ς as σ, so ΚΩΔΙΚΟΣ, κωδικός and κωδικοσ are one word
    [GeneratedRegex(@"(?<!\p{L})(?:(?:code|otp|pin|verification|verify|password)s?(?!\p{L})|κωδικ\p{L}*|επαληθευσ\p{L}*)")]
    private static partial Regex Keywords();

    /// <summary>The code's digits (separators removed) when there is exactly one, else null.</summary>
    public static string? Find(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var folded = Fold(text);
        var keywords = Keywords().Matches(folded).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        if (keywords.Count == 0) return null;
        var found = new HashSet<string>();
        foreach (Match c in Candidates().Matches(folded))
            if (keywords.Any(k => Near(folded, k.Start, k.End, c.Index, c.Index + c.Length)))
                found.Add(new string(c.Value.Where(char.IsAsciiDigit).ToArray()));
        return found.Count == 1 ? found.First() : null;
    }

    /// <summary>One output character per input character, so positions in the result are positions in the text.</summary>
    private static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsSurrogate(ch)) { sb.Append(ch); continue; } // a lone surrogate cannot be normalized
            var decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
            var letter = char.ToLowerInvariant(decomposed.Length > 0 ? decomposed[0] : ch);
            sb.Append(letter == 'ς' ? 'σ' : letter);
        }
        return sb.ToString();
    }

    private static bool Near(string s, int aStart, int aEnd, int bStart, int bEnd)
    {
        var (from, to) = aEnd <= bStart ? (aEnd, bStart) : bEnd <= aStart ? (bEnd, aStart) : (0, 0);
        if (to - from > MaxGap) return false;
        for (var i = from; i < to; i++)
            if (s[i] is '\n' or '\r') return false;
        return true;
    }
}
