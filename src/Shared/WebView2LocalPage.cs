using Microsoft.Web.WebView2.Core;

namespace ReactorV.WebView2Host
{
    /// <summary>
    /// Opens the trusted bundled document without the roughly two-second
    /// top-level HTTPS virtual-host navigation penalty. Subresources remain on
    /// the mapped local host and all subsequent navigation stays allowlisted.
    /// </summary>
    internal static class WebView2LocalPage
    {
        public static void Navigate(CoreWebView2 core, string uiDirectory)
        {
            core.SetVirtualHostNameToFolderMapping(
                WebView2LocalPagePolicy.HostName,
                uiDirectory,
                CoreWebView2HostResourceAccessKind.Allow);
            core.NavigateToString(WebView2LocalPagePolicy.InlineIndexHtml(uiDirectory));
        }

        public static bool IsAllowedNavigation(
            string value,
            ref bool initialInlineNavigationPending)
        {
            return WebView2LocalPagePolicy.IsAllowedNavigation(
                value,
                ref initialInlineNavigationPending);
        }

        public static bool IsTrustedMessageSource(string value)
        {
            return WebView2LocalPagePolicy.IsTrustedMessageSource(value);
        }
    }
}
