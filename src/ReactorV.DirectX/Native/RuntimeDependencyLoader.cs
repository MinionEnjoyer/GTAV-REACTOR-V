using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RageWebUI.DirectX.Native
{
    internal static class RuntimeDependencyLoader
    {
        private const uint LoadWithAlteredSearchPath = 0x00000008;
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, IntPtr> Loaded =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Assembly> Managed =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static string? _managedRoot;
        private static bool _resolverInstalled;

        public static void Prepare(string runtimeDirectory)
        {
            var root = Path.GetFullPath(runtimeDirectory);
            lock (Sync)
            {
                // SHVDN shadow-copies managed assemblies, but native imports are
                // not copied with them. Load the physical runtime dependencies
                // by absolute path before CefSharp or the compositor P/Invokes
                // are touched.
                Load(root, "chrome_elf.dll");
                Load(root, "libcef.dll");
                Load(root, "RageWebUI.Native.dll");

                // SHVDN's script AppDomain uses shadow copying with the scripts
                // directory as its application base. Dependencies stored in the
                // nested RageWebUI directory therefore need an explicit resolver.
                // Register it only after the native CEF imports above are loaded,
                // then eagerly bind the mixed-mode CefSharp runtime so failures
                // identify the physical file instead of the shadow-copy cache.
                InstallManagedResolver(root);
                LoadManaged(root, "CefSharp.dll", usePhysicalLoadFile: false);
                LoadManaged(root, "CefSharp.Core.Runtime.dll", usePhysicalLoadFile: true);
                LoadManaged(root, "CefSharp.Core.dll", usePhysicalLoadFile: false);
                LoadManaged(root, "CefSharp.OffScreen.dll", usePhysicalLoadFile: false);
            }
        }

        private static void InstallManagedResolver(string root)
        {
            if (_resolverInstalled)
            {
                if (!string.Equals(_managedRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"RageWebUI dependencies are already bound to '{_managedRoot}', not '{root}'.");
                }
                return;
            }

            _managedRoot = root;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveManagedAssembly;
            _resolverInstalled = true;
        }

        private static Assembly? ResolveManagedAssembly(object sender, ResolveEventArgs args)
        {
            var root = _managedRoot;
            if (root == null || root.Trim().Length == 0)
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

            var fileName = simpleName + ".dll";
            var candidate = Path.Combine(root, fileName);
            return File.Exists(candidate)
                ? LoadManaged(
                    root,
                    fileName,
                    usePhysicalLoadFile: string.Equals(
                        simpleName,
                        "CefSharp.Core.Runtime",
                        StringComparison.OrdinalIgnoreCase))
                : null;
        }

        private static Assembly LoadManaged(string root, string fileName, bool usePhysicalLoadFile)
        {
            var path = Path.Combine(root, fileName);
            if (Managed.TryGetValue(path, out var loaded))
            {
                return loaded;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"RageWebUI managed dependency was not found at '{path}'.",
                    path);
            }

            var requested = AssemblyName.GetAssemblyName(path);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), requested))
                {
                    Managed.Add(path, assembly);
                    return assembly;
                }
            }

            // Only the mixed-mode runtime needs LoadFile's exact physical
            // binding. Pure managed callback assemblies must use LoadFrom so
            // CEF-created threads can resolve them by display name.
            var result = usePhysicalLoadFile
                ? Assembly.LoadFile(path)
                : Assembly.LoadFrom(path);
            Managed.Add(path, result);
            return result;
        }

        private static void Load(string root, string fileName)
        {
            var path = Path.Combine(root, fileName);
            if (Loaded.ContainsKey(path))
            {
                return;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"RageWebUI native dependency was not found at '{path}'.",
                    path);
            }

            var handle = LoadLibraryEx(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"RageWebUI could not load native dependency '{path}'.");
            }

            Loaded.Add(path, handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(
            string fileName,
            IntPtr file,
            uint flags);
    }
}
