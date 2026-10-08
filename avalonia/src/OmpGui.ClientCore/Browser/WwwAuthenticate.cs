namespace OmpGui.ClientCore.Browser;

/// <summary>
/// The scheme and realm of a <c>WWW-Authenticate</c> / <c>Proxy-Authenticate</c> challenge such as
/// <c>Basic realm="Studio", charset="UTF-8"</c>, for engines that pass the raw header (WebView2) instead of parsed parts
/// (WKWebView, WebKitGTK). Only what the sign-in card shows; quoted-pair escapes in the realm are undone.
/// </summary>
public static class WwwAuthenticate
{
    public static (string? Scheme, string? Realm) Parse(string? challenge)
    {
        if (string.IsNullOrWhiteSpace(challenge)) return (null, null);
        var text = challenge.Trim();
        var space = text.IndexOfAny([' ', '\t']);
        var scheme = space < 0 ? text : text[..space];
        var at = FindParameter(text, "realm", space < 0 ? text.Length : space);
        if (at < 0) return (scheme, null);
        var rest = text.AsSpan(at);
        string realm;
        if (rest.StartsWith("\""))
        {
            var value = new System.Text.StringBuilder();
            for (var i = 1; i < rest.Length && rest[i] != '"'; i++)
            {
                if (rest[i] == '\\' && i + 1 < rest.Length) i++;
                value.Append(rest[i]);
            }
            realm = value.ToString();
        }
        else
        {
            var end = rest.IndexOfAny(',', ' ');
            realm = (end < 0 ? rest : rest[..end]).ToString();
        }
        return (scheme, realm.Length == 0 ? null : realm);
    }

    /// <summary>Where the value of parameter <paramref name="name"/> starts (after <c>name=</c>); -1 when absent.</summary>
    private static int FindParameter(string text, string name, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (i > 0 && text[i - 1] is not (' ' or ',' or '\t')) continue;
            if (string.Compare(text, i, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            var j = i + name.Length;
            while (j < text.Length && text[j] == ' ') j++;
            if (j < text.Length && text[j] == '=')
            {
                j++;
                while (j < text.Length && text[j] == ' ') j++;
                return j;
            }
        }
        return -1;
    }
}
