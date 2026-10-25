using System.Text;
using System.Text.RegularExpressions;

namespace ScormEditor.Services;

/// <summary>
/// Reads and writes course text files in the encoding they were saved in. Most courses are UTF-8,
/// but older ones can be UTF-16, or a legacy code page their pages declare (Shift_JIS or EUC-JP
/// for Japanese, Windows-1252 or ISO-8859-1 for French and German). Reading those as UTF-8 would
/// turn their accents and kana into replacement characters, and saving would make it permanent.
/// </summary>
public static class TextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex Declared = new(@"(?:charset|encoding)\s*=\s*[""']?([A-Za-z0-9_.:-]+)", RegexOptions.IgnoreCase);

    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The encoding of the bytes and the length of their byte order mark.</summary>
    public static (Encoding Encoding, int Preamble) Detect(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8, 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode, 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode, 2);
        try
        {
            StrictUtf8.GetCharCount(b);
            return (Encoding.UTF8, 0);
        }
        catch (DecoderFallbackException) { }
        // Not UTF-8: use what the file says it is (an HTML meta charset or an XML declaration),
        // read from its first bytes, which are plain ASCII in every one of these encodings.
        var head = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 4096));
        if (Declared.Match(head) is { Success: true } m)
        {
            try
            {
                var e = Encoding.GetEncoding(m.Groups[1].Value);
                if (e.CodePage != Encoding.UTF8.CodePage) return (e, 0);
            }
            catch (ArgumentException) { }
        }
        return (Encoding.GetEncoding(1252), 0);
    }

    public static string Decode(byte[] b)
    {
        var (e, skip) = Detect(b);
        return e.GetString(b, skip, b.Length - skip);
    }

    /// <summary>
    /// Text encoded like the file's current bytes (null for a new file: UTF-8). When the text has
    /// characters the file's old code page can't hold, the file becomes UTF-8 and its declared
    /// charset is changed to match, so nothing typed is lost.
    /// </summary>
    public static byte[] EncodeLike(byte[]? current, string text)
    {
        if (current is null) return Encoding.UTF8.GetBytes(text);
        var (e, skip) = Detect(current);
        if (e is UTF8Encoding || e.CodePage == Encoding.Unicode.CodePage || e.CodePage == Encoding.BigEndianUnicode.CodePage)
            return [.. current.AsSpan(0, skip), .. e.GetBytes(text)];
        try
        {
            var strict = Encoding.GetEncoding(e.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            return strict.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            var utf8 = Declared.Replace(text, m => m.Value[..(m.Groups[1].Index - m.Index)] + "utf-8", 1);
            return Encoding.UTF8.GetBytes(utf8);
        }
    }
}
