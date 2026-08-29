using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using RageWebUI.Core;

namespace RageWebUI.Script.Browser
{
    /// <summary>
    /// Loads the renderer from the GTA root instead of the scripts tree.
    /// ScriptHookVDotNet recursively inspects managed assemblies beneath
    /// scripts before constructing scripts; keeping Chromium outside that tree
    /// prevents its mixed-mode assemblies from being bound in the wrong order.
    /// </summary>
    internal static class ExternalRuntimeLoader
    {
        private const uint LoadWithAlteredSearchPath = 0x00000008;
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, IntPtr> NativeLibraries =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Assembly> ManagedAssemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static string? _runtimeDirectory;
        private static string? _logDirectory;
        private static bool _resolverInstalled;

        public static IOverlayRuntime Create(
            string renderer,
            IntPtr gtaWindow,
            string bootstrapDirectory,
            string localDataDirectory,
            BridgeBroker broker,
            int width,
            int height,
            int frameRate,
            bool enableDevTools,
            bool startVisible)
        {
            var runtimeDirectory = RuntimeDirectoryLocator.ResolveRenderer(
                bootstrapDirectory,
                Environment.CurrentDirectory,
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            var uiDirectory = Path.Combine(runtimeDirectory, "ui");
            Prepare(runtimeDirectory, localDataDirectory, renderer);

            Trace(
                "renderer_create_begin",
                $"renderer={renderer} width={width} height={height} runtime={runtimeDirectory}");
            var runtimeAssembly = LoadManaged(runtimeDirectory, "RageWebUI.Runtime.dll", usePhysicalLoadFile: true);
            var runtimeType = runtimeAssembly.GetType("RageWebUI.Runtime.OverlayRuntime", throwOnError: true);
            var instance = Activator.CreateInstance(
                runtimeType!,
                renderer,
                gtaWindow,
                uiDirectory,
                runtimeDirectory,
                localDataDirectory,
                broker,
                width,
                height,
                frameRate,
                enableDevTools,
                startVisible);
            if (!(instance is IOverlayRuntime runtime))
            {
                throw new InvalidCastException(
                    "RageWebUI.Runtime.OverlayRuntime does not implement the shared IOverlayRuntime contract.");
            }

            Trace("renderer_contract_created");
            return runtime;
        }

        private static void Prepare(string runtimeDirectory, string localDataDirectory, string renderer)
        {
            var root = Path.GetFullPath(runtimeDirectory);
            lock (Sync)
            {
                Directory.CreateDirectory(localDataDirectory);
                _logDirectory = localDataDirectory;
                Trace(
                    "bootstrap_prepare",
                    $"assembly={Assembly.GetExecutingAssembly().Location} runtime={root} " +
                    $"domain={AppDomain.CurrentDomain.FriendlyName}");

                if (_runtimeDirectory != null &&
                    !string.Equals(_runtimeDirectory, root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"REACTOR V is already bound to '{_runtimeDirectory}', not '{root}'.");
                }

                _runtimeDirectory = root;
                InstallManagedResolver();

                // WebView2 is the safe overlay host when a managed plugin system
                // runs us outside the CLR default AppDomain. Preload its native
                // loader from the external runtime directory so P/Invoke can find
                // it without copying renderer binaries into the scripts tree.
                LoadNative(root, "WebView2Loader.dll");

                if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                {
                    Trace(
                        "cefsharp_deferred",
                        $"reason=non_default_appdomain " +
                        $"domain={AppDomain.CurrentDomain.FriendlyName} requestedRenderer={renderer}");
                    return;
                }

                // CEF must be loaded from its physical directory before any
                // managed CefSharp type is resolved in the CLR default AppDomain.
                LoadNative(root, "chrome_elf.dll");
                LoadNative(root, "libcef.dll");
                LoadNative(root, "RageWebUI.Native.dll");
                // CefSharp.dll contains the managed callback targets used by
                // CefSharp.Core.Runtime on CEF-created threads. It must live in
                // the normal load-from context; LoadFile makes the module
                // visible but leaves those callbacks unable to bind it.
                LoadManaged(root, "CefSharp.dll", usePhysicalLoadFile: false);
                LoadManaged(root, "CefSharp.Core.Runtime.dll", usePhysicalLoadFile: true);
                LoadManaged(root, "CefSharp.Core.dll", usePhysicalLoadFile: false);
                LoadManaged(root, "CefSharp.OffScreen.dll", usePhysicalLoadFile: false);
                Trace("renderer_dependencies_ready");
            }
        }

        private static void InstallManagedResolver()
        {
            if (_resolverInstalled)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += ResolveManagedAssembly;
            _resolverInstalled = true;
            Trace("managed_resolver_installed");
        }

        private static Assembly? ResolveManagedAssembly(object sender, ResolveEventArgs args)
        {
            var root = _runtimeDirectory;
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            string? simpleName;
            try
            {
                simpleName = new AssemblyName(args.Name).Name;
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(simpleName) ||
                simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var requested = new AssemblyName(args.Name);
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), requested));
            if (loaded != null)
            {
                return loaded;
            }

            var candidatePath = Path.Combine(root!, simpleName + ".dll");
            if (!File.Exists(candidatePath))
            {
                Trace(
                    "managed_resolve_miss",
                    $"name={simpleName} requestedBy={args.RequestingAssembly?.GetName().Name ?? "unknown"}");
                return null;
            }

            try
            {
                var result = LoadManaged(
                    root!,
                    simpleName + ".dll",
                    usePhysicalLoadFile: string.Equals(
                        simpleName,
                        "CefSharp.Core.Runtime",
                        StringComparison.OrdinalIgnoreCase));
                Trace("managed_resolved", $"name={simpleName} path={candidatePath}");
                return result;
            }
            catch (Exception error)
            {
                Trace(
                    "managed_resolve_failed",
                    $"name={simpleName} type={error.GetType().FullName} message={error.Message}");
                throw;
            }
        }

        private static Assembly LoadManaged(string root, string fileName, bool usePhysicalLoadFile)
        {
            var path = Path.GetFullPath(Path.Combine(root, fileName));
            if (ManagedAssemblies.TryGetValue(path, out var cached))
            {
                return cached;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"REACTOR V managed dependency was not found at '{path}'.",
                    path);
            }

            var requested = AssemblyName.GetAssemblyName(path);
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), requested));
            if (loaded == null)
            {
                loaded = usePhysicalLoadFile
                    ? Assembly.LoadFile(path)
                    : Assembly.LoadFrom(path);
            }

            ManagedAssemblies[path] = loaded;
            Trace(
                "managed_ready",
                $"name={requested.Name} version={requested.Version} " +
                $"mode={(usePhysicalLoadFile ? "loadfile" : "loadfrom")} path={path}");
            return loaded;
        }

        private static void LoadNative(string root, string fileName)
        {
            var path = Path.GetFullPath(Path.Combine(root, fileName));
            if (NativeLibraries.ContainsKey(path))
            {
                return;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"REACTOR V native dependency was not found at '{path}'.",
                    path);
            }

            var handle = LoadLibraryEx(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"REACTOR V could not load native dependency '{path}'.");
            }

            NativeLibraries[path] = handle;
            Trace("native_ready", $"name={fileName} path={path}");
        }

        private static void Trace(string stage, string? detail = null)
        {
            var directory = _logDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            StartupTrace.Write(
                directory!,
                "reactorv-bootstrap.log",
                "bootstrap",
                stage,
                detail);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);
    }
}
