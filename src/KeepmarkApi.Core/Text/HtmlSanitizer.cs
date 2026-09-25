namespace KeepmarkApi.Text;

/// <summary>HTML sanitizing for the service status message.
/// Same behaviour as the Node InfoService: strip only
/// &lt;script&gt;…&lt;/script&gt;, nothing else (parity first; hardening is future work).</summary>
public static class HtmlSanitizer
{
    public static string StripScripts(string? html)
        => string.IsNullOrEmpty(html)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(
                html, @"<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
