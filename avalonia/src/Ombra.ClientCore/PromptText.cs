using System.Buffers;
using System.Globalization;
using System.Text;

namespace Ombra.ClientCore;

/// <summary>Visible user input only; never rewrite attached source files or tool commands.</summary>
public static class PromptText
{
    public readonly record struct Cleaned(string Text, int Removed);

    public static Cleaned Clean(string text)
    {
        StringBuilder? output = null;
        var removed = 0;
        for (var offset = 0; offset < text.Length;)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var length);
            if (status != OperationStatus.Done) length = 1;
            var value = status == OperationStatus.Done ? rune.Value : -1;
            var preserve = value is '\n' or '\t' or 0x200C or 0x200D
                or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF;
            if (value == '\r')
            {
                output ??= new StringBuilder(text.Length).Append(text.AsSpan(0, offset));
                output.Append('\n');
                if (offset + 1 < text.Length && text[offset + 1] == '\n') length++;
            }
            else if (!preserve && status == OperationStatus.Done && ApprovalDisplay.Hidden(rune))
            {
                output ??= new StringBuilder(text.Length).Append(text.AsSpan(0, offset));
                removed++;
                // Preserve word/line boundaries instead of joining words when normalizing separators.
                if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator) output.Append(' ');
                else if (value is 0x2028 or 0x2029) output.Append('\n');
            }
            else output?.Append(text.AsSpan(offset, length));
            offset += length;
        }
        return new Cleaned(output?.ToString() ?? text, removed);
    }
}
