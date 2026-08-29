using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using RageWebUI.Core;

namespace ReactorV.Preloader
{
    internal static class Program
    {
        private const string MutexPrefix = @"Local\ReactorV.Preloader.Singleton.";

        [STAThread]
        private static int Main(string[] args)
        {
            var executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var localDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ReactorV");
            var settings = PreloaderSettings.Load(Path.Combine(
                executableDirectory,
                "ReactorV.Preloader.json"));

            if (!PreloaderOptions.TryParse(args, settings, executableDirectory, out var options, out var error))
            {
                Trace(localDataDirectory, "arguments_rejected", error);
                return 2;
            }
            var traceDirectory = options.LogDirectory;

            bool createdNew;
            using (var singleton = new Mutex(
                true,
                MutexPrefix + options.InstanceId,
                out createdNew))
            {
                if (!createdNew)
                {
                    Trace(
                        traceDirectory,
                        "duplicate_instance_skipped",
                        $"instance_id={options.InstanceId}");
                    return 0;
                }

                Trace(
                    traceDirectory,
                    "preloader_start",
                    $"ui={options.UiDirectory} udf={options.UserDataDirectory} " +
                    $"process={options.WaitForProcess ?? "none"} parent_pid={options.ParentProcessId?.ToString() ?? "none"} " +
                    $"self_test={options.SelfTest} cache_only={options.CacheOnly} " +
                    $"timeout_seconds={options.MaximumLifetime.TotalSeconds:F0} " +
                    $"instance_id={options.InstanceId}");

                if (options.CacheOnly)
                {
                    return BuildCacheOnly(options, traceDirectory);
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var context = new PreloaderApplicationContext(options, traceDirectory))
                {
                    Application.Run(context);
                    Trace(traceDirectory, "preloader_stop", $"exit_code={context.ExitCode}");
                    return context.ExitCode;
                }
            }
        }

        internal static void Trace(string directory, string stage, string? detail = null) =>
            StartupTrace.Write(directory, "reactorv-preloader.log", "preloader", stage, detail);

        private static int BuildCacheOnly(PreloaderOptions options, string traceDirectory)
        {
            var processId = options.ParentProcessId!.Value;
            using (var ready = PreloadHandoff.CreatePreloadDataReadyWaitHandle(processId))
            {
                try
                {
                    var result = PreloadDataCache.Build(
                        options.GtaRoot!,
                        processId,
                        options.CacheRootOverride,
                        (stage, detail) => Trace(traceDirectory, stage, detail));
                    ready.Set();
                    Trace(
                        traceDirectory,
                        "preload_data_ready_signaled",
                        $"pid={processId} manifests={result.SnapshotPaths.Count} " +
                        $"entries={result.EntryCount} complete={result.Complete}");
                    return result.Complete ? 0 : 1;
                }
                catch (Exception error)
                {
                    ready.Set();
                    Trace(
                        traceDirectory,
                        "preload_data_failed",
                        $"pid={processId} type={error.GetType().Name} message={error.Message}");
                    return 1;
                }
            }
        }
    }

    internal sealed class PreloaderApplicationContext : ApplicationContext
    {
        private readonly PreloaderOptions _options;
        private readonly string _logDirectory;
        private readonly Stopwatch _lifetime = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer _lifecycleTimer;
        private readonly PreloadWindow _window;
        private Process? _targetProcess;
        private EventWaitHandle? _handoff;
        private EventWaitHandle? _preloadDataReady;
        private bool _contentReady;
        private bool _profileReleaseStarted;
        private bool _profileReleased;
        private bool _stopping;

        public PreloaderApplicationContext(PreloaderOptions options, string logDirectory)
        {
            _options = options;
            _logDirectory = logDirectory;
            _window = new PreloadWindow(
                options.UiDirectory,
                options.UserDataDirectory,
                (stage, detail) => Program.Trace(_logDirectory, stage, detail),
                OnContentReady,
                error => Stop(
                    1,
                    "browser_failed",
                    $"type={error.GetType().FullName} message={error.Message}"));
            _window.FormClosed += (_, __) => ExitThread();
            var unusedHandle = _window.Handle;
            _window.BeginInvoke(new Action(_window.BeginPreload));

            _lifecycleTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _lifecycleTimer.Tick += (_, __) => PollLifecycle();
            _lifecycleTimer.Start();

            if (options.ParentProcessId.HasValue)
            {
                AttachToProcess(options.ParentProcessId.Value);
            }
            else if (!options.SelfTest)
            {
                Program.Trace(
                    _logDirectory,
                    "process_wait_begin",
                    $"name={options.WaitForProcess} timeout_seconds={options.ProcessWaitTimeout.TotalSeconds:F0}");
            }
        }

        public int ExitCode { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lifecycleTimer.Dispose();
                _handoff?.Dispose();
                _preloadDataReady?.Dispose();
                _targetProcess?.Dispose();
                _window.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnContentReady()
        {
            _contentReady = true;
            BeginProfileRelease();
        }

        private async void BeginProfileRelease()
        {
            if (_profileReleaseStarted || _stopping)
            {
                return;
            }
            _profileReleaseStarted = true;
            try
            {
                var browserExited = await _window.ReleaseBrowserAsync(
                    TimeSpan.FromSeconds(8));
                if (_stopping)
                {
                    return;
                }
                if (!browserExited)
                {
                    Stop(
                        1,
                        "webview_profile_release_timeout",
                        "The shared WebView2 browser process did not release its resources.");
                    return;
                }

                _profileReleased = true;
                Program.Trace(
                    _logDirectory,
                    "webview_warm_cache_released",
                    $"elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3} udf={_options.UserDataDirectory}");
                if (_options.SelfTest)
                {
                    Stop(0, "self_test_complete", "profile_released=True");
                }
            }
            catch (Exception error)
            {
                Stop(
                    1,
                    "webview_profile_release_failed",
                    $"type={error.GetType().FullName} message={error.Message}");
            }
        }

        private void PollLifecycle()
        {
            if (_stopping)
            {
                return;
            }

            if (_lifetime.Elapsed >= _options.MaximumLifetime)
            {
                Stop(0, "maximum_lifetime_reached");
                return;
            }

            if (_options.SelfTest)
            {
                return;
            }

            if (_profileReleaseStarted && !_profileReleased)
            {
                return;
            }

            if (_targetProcess == null)
            {
                TryAttachToNamedProcess();
                if (_targetProcess == null && _lifetime.Elapsed >= _options.ProcessWaitTimeout)
                {
                    Stop(0, "process_wait_timeout", $"name={_options.WaitForProcess}");
                }
                return;
            }

            try
            {
                if (_targetProcess.HasExited)
                {
                    Stop(0, "target_process_exited", $"pid={_targetProcess.Id}");
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                Stop(0, "target_process_unavailable");
                return;
            }

            if (_profileReleased && _handoff?.WaitOne(0) == true)
            {
                Stop(
                    0,
                    "content_ready_handoff_received",
                    $"pid={_targetProcess.Id} preload_ready={_contentReady} profile_released=True");
            }
        }

        private void TryAttachToNamedProcess()
        {
            if (string.IsNullOrWhiteSpace(_options.WaitForProcess))
            {
                return;
            }

            Process? candidate = null;
            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(_options.WaitForProcess);
                candidate = candidates
                    .OrderByDescending(SafeStartTimeUtc)
                    .FirstOrDefault(process => !SafeHasExited(process));
            }
            catch
            {
                return;
            }

            foreach (var process in candidates)
            {
                if (!ReferenceEquals(process, candidate))
                {
                    process.Dispose();
                }
            }

            if (candidate != null)
            {
                AttachToProcess(candidate);
            }
        }

        private void AttachToProcess(int processId)
        {
            try
            {
                AttachToProcess(Process.GetProcessById(processId));
            }
            catch (ArgumentException)
            {
                Stop(2, "parent_process_missing", $"pid={processId}");
            }
        }

        private void AttachToProcess(Process process)
        {
            _targetProcess?.Dispose();
            _handoff?.Dispose();
            _targetProcess = process;
            _handoff = PreloadHandoff.CreateWaitHandle(process.Id);
            Program.Trace(
                _logDirectory,
                "target_process_attached",
                $"pid={process.Id} name={SafeProcessName(process)} event={PreloadHandoff.EventName(process.Id)} " +
                $"elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3}");
            BuildPreloadData(process.Id);
        }

        private void BuildPreloadData(int processId)
        {
            _preloadDataReady?.Dispose();
            _preloadDataReady = PreloadHandoff.CreatePreloadDataReadyWaitHandle(processId);
            try
            {
                var gtaRoot = _options.GtaRoot ??
                    PreloadDataCache.ResolveGtaRootFromPreloaderDirectory(
                        AppDomain.CurrentDomain.BaseDirectory);
                var result = PreloadDataCache.Build(
                    gtaRoot,
                    processId,
                    _options.CacheRootOverride,
                    (stage, detail) => Program.Trace(_logDirectory, stage, detail));
                _preloadDataReady.Set();
                Program.Trace(
                    _logDirectory,
                    "preload_data_ready_signaled",
                    $"pid={processId} manifests={result.SnapshotPaths.Count} " +
                    $"entries={result.EntryCount} complete={result.Complete}");
            }
            catch (Exception error)
            {
                _preloadDataReady.Set();
                Program.Trace(
                    _logDirectory,
                    "preload_data_failed",
                    $"pid={processId} type={error.GetType().Name} message={error.Message}");
            }
        }

        private void Stop(int exitCode, string stage, string? detail = null)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            ExitCode = exitCode;
            Program.Trace(
                _logDirectory,
                stage,
                $"elapsed_ms={_lifetime.Elapsed.TotalMilliseconds:F3}" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
            _lifecycleTimer.Stop();
            if (!_window.IsDisposed)
            {
                _window.Close();
            }
            else
            {
                ExitThread();
            }
        }

        private static DateTime SafeStartTimeUtc(Process process)
        {
            try
            {
                return process.StartTime.ToUniversalTime();
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static bool SafeHasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }
    }

    internal sealed class PreloaderSettings
    {
        [JsonProperty("processWaitTimeoutSeconds")]
        public int ProcessWaitTimeoutSeconds { get; set; } = 180;

        [JsonProperty("maximumLifetimeSeconds")]
        public int MaximumLifetimeSeconds { get; set; } = 300;

        public static PreloaderSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonConvert.DeserializeObject<PreloaderSettings>(File.ReadAllText(path))
                        ?? new PreloaderSettings();
                }
            }
            catch
            {
            }

            return new PreloaderSettings();
        }
    }

    internal sealed class PreloaderOptions
    {
        public string? WaitForProcess { get; private set; }
        public int? ParentProcessId { get; private set; }
        public string UiDirectory { get; private set; } = string.Empty;
        public string UserDataDirectory { get; private set; } = string.Empty;
        public TimeSpan ProcessWaitTimeout { get; private set; }
        public TimeSpan MaximumLifetime { get; private set; }
        public bool SelfTest { get; private set; }
        public bool CacheOnly { get; private set; }
        public string? GtaRoot { get; private set; }
        public string? CacheRootOverride { get; private set; }
        public string LogDirectory { get; private set; } = string.Empty;
        public string InstanceId { get; private set; } = "production";

        public static bool TryParse(
            string[] args,
            PreloaderSettings settings,
            string executableDirectory,
            out PreloaderOptions options,
            out string error)
        {
            options = new PreloaderOptions
            {
                UiDirectory = Path.Combine(executableDirectory, "ui"),
                UserDataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ReactorV",
                    "WebView2"),
                LogDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ReactorV"),
                ProcessWaitTimeout = TimeSpan.FromSeconds(Math.Max(5, settings.ProcessWaitTimeoutSeconds)),
                MaximumLifetime = TimeSpan.FromSeconds(Math.Max(15, settings.MaximumLifetimeSeconds)),
            };
            error = string.Empty;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    options.SelfTest = true;
                }
                else if (string.Equals(argument, "--cache-only", StringComparison.OrdinalIgnoreCase))
                {
                    options.CacheOnly = true;
                }
                else if (string.Equals(argument, "--wait-for-process", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--wait-for-process requires a process name.";
                        return false;
                    }
                    options.WaitForProcess = NormalizeProcessName(value);
                }
                else if (string.Equals(argument, "--parent-pid", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value) ||
                        !int.TryParse(value, out var processId) || processId <= 0)
                    {
                        error = "--parent-pid requires a positive integer.";
                        return false;
                    }
                    options.ParentProcessId = processId;
                }
                else if (string.Equals(argument, "--timeout-seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadPositiveSeconds(args, ref index, out var timeout))
                    {
                        error = "--timeout-seconds requires a positive integer.";
                        return false;
                    }
                    options.MaximumLifetime = timeout;
                }
                else if (string.Equals(argument, "--process-wait-timeout-seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadPositiveSeconds(args, ref index, out var timeout))
                    {
                        error = "--process-wait-timeout-seconds requires a positive integer.";
                        return false;
                    }
                    options.ProcessWaitTimeout = timeout;
                }
                else if (string.Equals(argument, "--ui-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--ui-dir requires a directory.";
                        return false;
                    }
                    options.UiDirectory = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--user-data-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--user-data-dir requires a directory.";
                        return false;
                    }
                    options.UserDataDirectory = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--log-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--log-dir requires a directory.";
                        return false;
                    }
                    options.LogDirectory = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--gta-root", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--gta-root requires a directory.";
                        return false;
                    }
                    options.GtaRoot = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--cache-root", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value))
                    {
                        error = "--cache-root requires a directory.";
                        return false;
                    }
                    options.CacheRootOverride = Path.GetFullPath(value);
                }
                else if (string.Equals(argument, "--instance-id", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadValue(args, ref index, out var value) ||
                        value.Length > 64 ||
                        value.Any(character =>
                            !char.IsLetterOrDigit(character) &&
                            character != '.' && character != '_' && character != '-'))
                    {
                        error = "--instance-id requires 1-64 letters, numbers, dots, underscores, or dashes.";
                        return false;
                    }
                    options.InstanceId = value;
                }
                else
                {
                    error = "Unknown argument: " + argument;
                    return false;
                }
            }

            if (options.GtaRoot != null && !options.SelfTest && !options.CacheOnly)
            {
                error = "--gta-root is accepted only by self-test or cache-only runs.";
                return false;
            }
            if (options.CacheRootOverride != null && !options.SelfTest && !options.CacheOnly)
            {
                error = "--cache-root is accepted only by self-test or cache-only runs.";
                return false;
            }
            if (options.CacheOnly)
            {
                if (options.GtaRoot == null || !options.ParentProcessId.HasValue)
                {
                    error = "--cache-only requires --gta-root and --parent-pid.";
                    return false;
                }
                return true;
            }

            if (!Directory.Exists(options.UiDirectory) ||
                !File.Exists(Path.Combine(options.UiDirectory, "index.html")))
            {
                error = "The local ReactorV UI is missing: " + options.UiDirectory;
                return false;
            }

            if (options.SelfTest)
            {
                return true;
            }

            if (options.ParentProcessId.HasValue == !string.IsNullOrWhiteSpace(options.WaitForProcess))
            {
                error = "Specify exactly one of --wait-for-process or --parent-pid.";
                return false;
            }

            return true;
        }

        private static bool TryReadPositiveSeconds(
            string[] args,
            ref int index,
            out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (!TryReadValue(args, ref index, out var raw) ||
                !int.TryParse(raw, out var seconds) || seconds <= 0)
            {
                return false;
            }
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        private static bool TryReadValue(string[] args, ref int index, out string value)
        {
            value = string.Empty;
            if (index + 1 >= args.Length)
            {
                return false;
            }
            value = args[++index];
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string NormalizeProcessName(string processName)
        {
            var name = Path.GetFileName(processName.Trim());
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4)
                : name;
        }
    }
}
