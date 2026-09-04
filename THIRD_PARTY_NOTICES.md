# Third-party components

Reactor V's original code and starter examples are MIT licensed; see LICENSE.
Third-party components remain under their own licenses, not Reactor's license.
This project is not affiliated with Rockstar Games or Take-Two Interactive.
The Reactor license does not grant rights to GTA, other mods, or their assets.

Runtime packages include a `plugins/ReactorV/legal` directory containing the
project license, upstream notices, and a hash-indexed component manifest.
The build collects notices from the exact restored dependencies, not a latest
version downloaded at packaging time. Missing notices block packaging.
`Chromium-CREDITS.txt` is exported from the packaged CEF binary's own
`chrome://credits/` page, including collapsed license blocks. It is not fetched
from a different Chromium version online.

| Component | Upstream / notice source |
| --- | --- |
| CefSharp | https://github.com/cefsharp/CefSharp — restored NuGet LICENSE |
| Chromium Embedded Framework | https://github.com/chromiumembedded/cef — restored x64 runtime LICENSE.txt; Chromium and its included components retain their respective terms |
| Microsoft WebView2 SDK | https://www.nuget.org/packages/Microsoft.Web.WebView2 — restored LICENSE.txt and NOTICE.txt |
| Newtonsoft.Json | https://github.com/JamesNK/Newtonsoft.Json — restored LICENSE.md |
| SharpDX | https://github.com/sharpdx/SharpDX/blob/8e5df9f17b1d328c595a9df5851dbb2537b55621/License.txt — retained in legal/SharpDX-LICENSE.txt |
| MinHook, including its bundled disassembler notices | https://github.com/TsudaKageyu/minhook — native build's LICENSE.txt |
| React / React DOM / Scheduler | https://github.com/facebook/react — installed package LICENSE files |
| Bebas Neue / Oswald | SIL Open Font License notices alongside fonts in UI payload |

Script Hook V and ScriptHookVDotNet are external prerequisites and are not
redistributed. The WebView2 browser runtime is installed separately; the SDK
loader and managed SDK assemblies are included. Build/test tooling is not part
of the player runtime. Starter kit payloads contain only the two example mod
DLLs, with Reactor Core included separately as a compile-only reference.
