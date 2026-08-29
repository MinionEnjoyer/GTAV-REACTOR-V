using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace ReactorV.WebView2Host
{
    /// <summary>
    /// The preloader and in-game host must present byte-for-byte equivalent
    /// environment options when they share a WebView2 user-data folder.
    /// Keeping construction in one linked source file prevents contract drift.
    /// </summary>
    internal static class WebView2EnvironmentFactory
    {
        private const string Language = "en-US";

        public static string NormalizeUserDataDirectory(string path) =>
            Path.GetFullPath(path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));

        public static CoreWebView2EnvironmentOptions CreateOptions()
        {
            var options = new CoreWebView2EnvironmentOptions(
                string.Empty,
                Language,
                null,
                false,
                null)
            {
                ExclusiveUserDataFolderAccess = false,
                AreBrowserExtensionsEnabled = false,
                EnableTrackingPrevention = false,
            };
            return options;
        }

        public static Task<CoreWebView2Environment> CreateAsync(string userDataDirectory)
        {
            var directory = NormalizeUserDataDirectory(userDataDirectory);
            Directory.CreateDirectory(directory);
            return CoreWebView2Environment.CreateAsync(
                null,
                directory,
                CreateOptions());
        }

        public static string Describe(string userDataDirectory)
        {
            var overrideNames = new[]
            {
                "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
                "WEBVIEW2_USER_DATA_FOLDER",
                "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE",
                "WEBVIEW2_RELEASE_CHANNELS",
                "WEBVIEW2_CHANNEL_SEARCH_KIND",
            };
            var activeOverrides = string.Empty;
            foreach (var name in overrideNames)
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                {
                    continue;
                }
                activeOverrides += activeOverrides.Length == 0 ? name : "," + name;
            }
            if (activeOverrides.Length == 0)
            {
                activeOverrides = "none";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "udf={0} language={1} sso=False exclusive=False extensions=False tracking=False overrides={2}",
                NormalizeUserDataDirectory(userDataDirectory),
                Language,
                activeOverrides);
        }
    }
}
