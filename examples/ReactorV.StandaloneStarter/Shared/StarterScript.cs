using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace ReactorV.Starter
{
    public sealed class StarterScript : GTA.Script
    {
#if STARTER_B
        private const string ExtensionId = "reactorv.starter-b";
        private const string Title = "Reactor Starter B";
        private const Keys MenuKey = Keys.F7;
#else
        private const string ExtensionId = "reactorv.starter-a";
        private const string Title = "Reactor Starter A";
        private const Keys MenuKey = Keys.F6;
#endif
        private IDisposable? _extension;
        private Func<bool>? _toggle;
        private bool _keyDown;
        private long _lastToggleAt = -250;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public StarterScript()
        {
            try { Register(); }
            catch (Exception error)
            {
                GTA.UI.Notification.Show("Reactor starter unavailable. Install compatible Reactor V first. " + error.GetType().Name);
                return;
            }
            KeyDown += OnKeyDown;
            KeyUp += (_, e) => { if (e.KeyCode == MenuKey) _keyDown = false; };
            Aborted += (_, __) => { _extension?.Dispose(); _extension = null; _toggle = null; };
        }

        // Keep Reactor type resolution inside the guarded call, not the GTA.Script constructor's JIT.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Register()
        {
            var api = typeof(ReactorV.Integration.ReactorApi);
            var apiVersion = (int?)api.GetField("ExtensionApiVersion")?.GetRawConstantValue();
            if (apiVersion != 1 || api.Assembly.GetName().Version < new Version(0, 2, 0, 0))
                throw new NotSupportedException("Reactor V 0.2.0 / extension API 1 is required.");
            var extension = new StarterExtension(ExtensionId, Title);
            _extension = extension;
            _toggle = extension.ToggleMenu;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != MenuKey || _keyDown) return;
            _keyDown = true;
            if (_clock.ElapsedMilliseconds - _lastToggleAt < 250) return;
            _lastToggleAt = _clock.ElapsedMilliseconds;
            if (_toggle?.Invoke() != true)
                GTA.UI.Notification.Show("Reactor is not ready to present this menu yet. Try again after Story Mode loads.");
        }
    }
}
