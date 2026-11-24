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

    // A currency sign before the digits counts even with a space between ("$ 1500"), and a text that was cut ("code 1234…") has no
    // code in it: the rest of the digits are missing.
    // 4-8 ASCII digits, or two groups split by one space or dash. Not part of a longer run of digits or letters: not a phone number
    // (210-555-1234), a date (23/11/2026, 2026.11.23), a time or an amount (1500.00, 50,00), and not a number that is
    // labelled as something else: an amount (a currency sign before it or a currency after it), the end of a card (ending
    // 4321, *4321), a reference, order, invoice or phone number.
    [GeneratedRegex(@"(?<![\p{L}\p{N}*#]|[€$£¥₹]\s*|[0-9][ \-./,:]|\b(?:ending|ends|ref|reference|order|invoice|id|no|tel|phone|call|τηλ)\.?:? )(?:[0-9]{4,8}|[0-9]{2,4}[ \-][0-9]{2,4})(?![\p{L}\p{N}…]|[ \-./,:][0-9]|\s?(?:eur|usd|gbp|euro|euros|ευρω|€|\$|£))")]
    private static partial Regex Candidates();

    // run on text folded to lower case with accents removed and ς as σ, so ΚΩΔΙΚΟΣ, κωδικός and κωδικοσ are one word.
    // "zip code", "promo code", "tracking code" and the like are codes of another kind, not a login code.
    [GeneratedRegex(@"(?<!\p{L})(?<!(?:zip|postal|post|area|country|promo|coupon|voucher|discount|tracking|error|dial) )(?<!ταχυδρομικοσ )(?<!ταχυδρομικου )(?:(?:code|otp|pin|verification|verify|password)s?(?!\p{L})|κωδικ\p{L}*|επαληθευσ\p{L}*)")]
    private static partial Regex Keywords();

    /// <summary>What a search found: the code, or null with <see cref="Ambiguous"/> telling "two or more candidates" from "none".</summary>
    public readonly record struct CodeSearch(string? Code, bool Ambiguous);

    /// <summary>The code's digits (separators removed) when there is exactly one, else null.</summary>
    public static string? Find(string text) => Search(text).Code;

    public static CodeSearch Search(string text)
    {
        if (string.IsNullOrEmpty(text)) return new(null, false);
        var folded = Fold(text);
        var keywords = Keywords().Matches(folded).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        if (keywords.Count == 0) return new(null, false);
        var found = new HashSet<string>();
        foreach (Match c in Candidates().Matches(folded))
            if (keywords.Any(k => Near(folded, k.Start, k.End, c.Index, c.Index + c.Length)))
            {
                var digits = new string(c.Value.Where(char.IsAsciiDigit).ToArray());
                if (digits.Length >= 4) found.Add(digits); // never a short or empty copy
            }
        return found.Count == 1 ? new(found.First(), false) : new(null, found.Count > 1);
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
