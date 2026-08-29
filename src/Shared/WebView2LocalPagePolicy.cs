using System;
using System.IO;

namespace ReactorV.WebView2Host
{
    /// <summary>
    /// Pure navigation and document policy shared by the WebView2 hosts and
    /// their headless regression tests.
    /// </summary>
    internal static class WebView2LocalPagePolicy
    {
        internal const string HostName = "reactorv.local";

        public static bool IsAllowedNavigation(
            string value,
            ref bool initialInlineNavigationPending)
        {
            if (string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return false;
            }
            if (string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                if (!initialInlineNavigationPending)
                {
                    return false;
                }
                initialInlineNavigationPending = false;
                return true;
            }
            return string.Equals(
                       uri.Scheme,
                       Uri.UriSchemeHttps,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(uri.Host, HostName, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTrustedMessageSource(string value)
        {
            if (string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return false;
            }
            return string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(uri.Host, HostName, StringComparison.OrdinalIgnoreCase));
        }

        internal static string InlineIndexHtml(string uiDirectory)
        {
            var path = Path.Combine(uiDirectory, "index.html");
            var html = File.ReadAllText(path);
            const string head = "<head>";
            var headIndex = html.IndexOf(head, StringComparison.OrdinalIgnoreCase);
            if (headIndex < 0)
            {
                throw new InvalidOperationException(
                    "The local ReactorV UI index has no head element.");
            }
            return html.Insert(
                headIndex + head.Length,
                "<meta http-equiv=\"Content-Security-Policy\" " +
                "content=\"default-src 'none'; " +
                "script-src https://reactorv.local; " +
                "style-src 'unsafe-inline' https://reactorv.local; " +
                "img-src data: https://reactorv.local; " +
                "font-src https://reactorv.local; " +
                "connect-src 'none'; media-src 'none'; object-src 'none'; " +
                "frame-src 'none'; worker-src 'none'; " +
                "base-uri https://reactorv.local; form-action 'none'\">" +
                "<base href=\"https://reactorv.local/\">");
        }
    }
}
