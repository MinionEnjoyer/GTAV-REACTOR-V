using System;
using CefSharp;
using CefSharp.Handler;

namespace RageWebUI.DirectX.Browser
{
    internal sealed class LocalOnlyRequestHandler : RequestHandler
    {
        protected override bool OnBeforeBrowse(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            bool userGesture,
            bool isRedirect)
        {
            return frame.IsMain && !IsAllowed(request.Url);
        }

        protected override bool OnOpenUrlFromTab(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            string targetUrl,
            WindowOpenDisposition targetDisposition,
            bool userGesture) => true;

        protected override IResourceRequestHandler GetResourceRequestHandler(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            bool isNavigation,
            bool isDownload,
            string requestInitiator,
            ref bool disableDefaultHandling) => LocalOnlyResourceRequestHandler.Instance;

        private static bool IsAllowed(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(uri.Scheme, "blob", StringComparison.OrdinalIgnoreCase))
            {
                return url.StartsWith("blob:https://ragewebui.local/", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(uri.Host, "ragewebui.local", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class LocalOnlyResourceRequestHandler : ResourceRequestHandler
        {
            public static readonly LocalOnlyResourceRequestHandler Instance =
                new LocalOnlyResourceRequestHandler();

            protected override CefReturnValue OnBeforeResourceLoad(
                IWebBrowser chromiumWebBrowser,
                IBrowser browser,
                IFrame frame,
                IRequest request,
                IRequestCallback callback) => IsAllowed(request.Url)
                    ? CefReturnValue.Continue
                    : CefReturnValue.Cancel;
        }
    }
}
