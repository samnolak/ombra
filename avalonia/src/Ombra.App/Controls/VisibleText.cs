using System.Text;

namespace Ombra.App.Controls;

/// <summary>Make terminal control characters inert and visible in ordinary UI text.</summary>
internal static class VisibleText
{
    public static string FromUntrustedOutput(string text, bool stripSgr = false)
    {
        StringBuilder? result = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            // Only complete SGR sequences are presentation, never cursor movement, OSC or malformed escapes.
            if (stripSgr && c == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var end = i + 2;
                while (end < text.Length && (text[end] is >= '0' and <= '9' or ';' or ':')) end++;
                if (end < text.Length && text[end] == 'm')
                {
                    result ??= new StringBuilder(text.Length).Append(text, 0, i);
                    i = end;
                    continue;
                }
            }
            if (c is '\n' or '\r' or '\t' || c >= ' ' && c != '\u007f' && (c < '\u0080' || c > '\u009f'))
            {
                result?.Append(c);
                continue;
            }

            result ??= new StringBuilder(text.Length + 8).Append(text, 0, i);
            if (c <= '\u001f') result.Append((char)('\u2400' + c));
            else if (c == '\u007f') result.Append('\u2421');
            else result.Append("⟦U+").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append('⟧');
        }
        return result?.ToString() ?? text;
    }
}
