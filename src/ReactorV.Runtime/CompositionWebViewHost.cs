using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using ReactorV.WebView2Host;

namespace RageWebUI.Runtime
{
    /// <summary>
    /// Hosts WebView2 in DirectComposition instead of the WinForms child-HWND
    /// control. A child HWND cannot participate in its parent's
    /// TransparencyKey, so a nominally transparent page is otherwise composed
    /// as an opaque black/chroma rectangle. Visual hosting keeps the browser's
    /// alpha channel all the way to DWM.
    /// </summary>
    internal sealed class CompositionWebViewHost : IDisposable
    {
        private readonly Form _owner;
        private CoreWebView2CompositionController? _controller;
        private DirectCompositionDevice? _composition;
        private IntPtr _inputParentWindow;
        private bool _leftButtonDown;
        private bool _pointerInside;
        private bool _disposed;
        private bool _controllerVisible;

        internal CompositionWebViewHost(Form owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _owner.ClientSizeChanged += OnOwnerClientSizeChanged;
            _owner.LocationChanged += OnOwnerLocationChanged;
            _owner.ParentChanged += OnOwnerLocationChanged;
        }

        internal bool IsDisposed => _disposed;

        internal CoreWebView2? CoreWebView2 => _controller?.CoreWebView2;

        internal bool IsControllerReady => !_disposed && _controller != null;

        internal bool IsControllerVisible => !_disposed && _controllerVisible;

        internal int CompositionGeneration => _composition?.Generation ?? 0;

        internal int RootVisualRevision => _composition?.RootVisualRevision ?? 0;

        internal CompositionDeviceHealth CheckCompositionDeviceState()
        {
            if (_disposed || _composition == null)
                return new CompositionDeviceHealth(
                    CompositionDeviceState.Unavailable,
                    unchecked((int)0x80004005));
            return _composition.CheckDeviceState();
        }

        internal async Task EnsureCoreWebView2Async(
            CoreWebView2Environment environment)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CompositionWebViewHost));
            if (_controller != null) return;

            // Accessing Handle is intentional and occurs on the overlay's STA
            // thread. It creates the top-level HWND before DirectComposition is
            // bound to it, without showing the window.
            var window = _owner.Handle;
            var inputParent = _inputParentWindow != IntPtr.Zero
                ? _inputParentWindow
                : window;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.DefaultBackgroundColor = Color.Transparent;
            CoreWebView2CompositionController? controller = null;
            DirectCompositionDevice? composition = null;
            try
            {
                composition = DirectCompositionDevice.Create(window);
                controller = await environment.CreateCoreWebView2CompositionControllerAsync(
                    inputParent,
                    options);
                controller.DefaultBackgroundColor = Color.Transparent;
                controller.IsVisible = true;
                _controllerVisible = true;
                _composition = composition;
                _controller = controller;
                composition = null;
                controller = null;

                var bound = ApplyCompositionMutation(
                    CompositionMutation.InitialBind);
                if (!bound.Succeeded)
                {
                    throw new COMException(
                        "The WebView2 composition root could not be bound.",
                        bound.HResult);
                }
            }
            catch
            {
                var failedController = _controller ?? controller;
                _controller = null;
                if (failedController != null)
                {
                    try { failedController.Close(); }
                    catch (COMException) { }
                }
                var failedComposition = _composition ?? composition;
                _composition = null;
                failedComposition?.Dispose();
                _controllerVisible = false;
                throw;
            }
        }

        /// <summary>
        /// Applies the same-process parent selected by WindowedInputPolicy.
        /// ParentWindow and the DirectComposition root must remain aligned;
        /// GTA ownership belongs to the outer overlay HWND, not WebView2.
        /// </summary>
        internal bool SetInputParentWindow(IntPtr window)
        {
            if (_disposed || window == IntPtr.Zero)
                return false;

            try
            {
                var controller = _controller;
                if (_inputParentWindow == window &&
                    (controller == null || controller.ParentWindow == window))
                    return true;
                if (controller != null)
                {
                    controller.ParentWindow = window;
                    var synchronized = ApplyCompositionMutation(
                        CompositionMutation.ParentPosition);
                    if (!synchronized.Succeeded)
                        return false;
                }
                // Commit the managed cache only after the complete WebView2
                // transition succeeds. If ParentWindow or Notify throws, the
                // caller retains its old observation and retries the required
                // hide/close transition rather than falsely treating it as
                // applied.
                _inputParentWindow = window;
                return true;
            }
            catch (COMException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reasserts the Reactor parent before shutdown. This is normally a
        /// no-op now that cross-process ParentWindow transitions are forbidden,
        /// but the explicit readback keeps teardown fail closed.
        /// </summary>
        internal bool DetachExternalInputParentForShutdown()
        {
            if (_disposed)
                return false;

            var ownerWindow = _owner.Handle;
            try
            {
                _inputParentWindow = ownerWindow;
                if (_controller != null)
                {
                    _controller.ParentWindow = ownerWindow;
                    var synchronized = ApplyCompositionMutation(
                        CompositionMutation.ParentPosition);
                    if (!synchronized.Succeeded)
                        return false;
                }
                return true;
            }
            catch (Exception error) when (
                error is COMException ||
                error is InvalidOperationException ||
                error is ObjectDisposedException)
            {
                return false;
            }
        }

        internal IntPtr InputParentWindow =>
            _controller?.ParentWindow ?? (_inputParentWindow != IntPtr.Zero
                ? _inputParentWindow
                : _owner.IsHandleCreated ? _owner.Handle : IntPtr.Zero);

        internal bool SynchronizeBounds()
        {
            return ApplyCompositionMutation(
                CompositionMutation.BoundsAndParent).Succeeded;
        }

        /// <summary>
        /// Waits for the most recently submitted DirectComposition commit on
        /// the owning overlay STA. The COM device is apartment-bound: moving
        /// this call to Task.Run can strand the wait in ScriptHookVDotNet's
        /// secondary AppDomain and prevent a warm overlay from ever appearing.
        /// Callers restrict this fence to the final qualified reveal boundary.
        /// </summary>
        internal int WaitForCommitCompletion()
        {
            if (_disposed || _controller == null || _composition == null)
                return unchecked((int)0x80004005);
            return _composition.WaitForCommitCompletion();
        }

        /// <summary>
        /// WebView2 composition hosting does not observe movement of its input
        /// parent automatically. Keep this notification separate from bounds:
        /// an ancestor/owner can move while the browser's client size remains
        /// unchanged.
        /// </summary>
        internal bool NotifyParentWindowPositionChanged()
        {
            return ApplyCompositionMutation(
                CompositionMutation.ParentPosition).Succeeded;
        }

        /// <summary>
        /// Recreates the DirectComposition target/visual when CheckDeviceState
        /// reports a lost device. The WebView controller is retained, but all
        /// Reactor-owned DirectComposition content is rebuilt and rebound.
        /// No synchronous commit-completion wait is used.
        /// </summary>
        internal CompositionDeviceRecoveryResult RecoverCompositionDevice()
        {
            if (_disposed || _controller == null || _composition == null)
            {
                return CompositionDeviceRecoveryResult.Failed(
                    CompositionDeviceState.Unavailable,
                    unchecked((int)0x80004005));
            }

            var observed = _composition.CheckDeviceState();
            if (observed.State == CompositionDeviceState.Ready)
                return CompositionDeviceRecoveryResult.NotRequired(observed);

            return RecoverCompositionDeviceCore(observed);
        }

        /// <summary>
        /// Republishes the existing HWND target when an independent desktop
        /// witness proves that a nominally successful commit was not
        /// presented. CheckDeviceState can still report Ready in this failure
        /// mode. A healthy target must be rebound rather than duplicated:
        /// DirectComposition permits only one target of a given kind for an
        /// HWND and reports DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED when a
        /// second target is created while the first remains registered.
        /// </summary>
        internal CompositionDeviceRecoveryResult ForceRecreateCompositionDevice()
        {
            if (_disposed || _controller == null || _composition == null)
            {
                return CompositionDeviceRecoveryResult.Failed(
                    CompositionDeviceState.Unavailable,
                    unchecked((int)0x80004005));
            }

            var observed = _composition.CheckDeviceState();
            var rebound = ApplyCompositionMutation(
                CompositionMutation.RootVisualRebind);
            return rebound.Succeeded
                ? CompositionDeviceRecoveryResult.Recovered(
                    observed,
                    rebound.CompositionGeneration,
                    CompositionDeviceRecoveryMode.ExistingTargetRebound)
                : CompositionDeviceRecoveryResult.Failed(
                    rebound.ObservedState,
                    rebound.HResult);
        }

        /// <summary>
        /// Performs the bounded visual-tree recovery used at a warm-reopen
        /// boundary or after a concrete desktop-presentation mismatch: detach,
        /// commit, rebind the existing root, apply current bounds, and commit.
        /// Unlike the old implementation this never pretends that an IsVisible
        /// toggle republishes the root.
        /// </summary>
        internal RootVisualRebindResult RebindRootVisual()
        {
            if (_disposed || _controller == null || _composition == null)
            {
                return RootVisualRebindResult.Failed(
                    CompositionDeviceState.Unavailable,
                    unchecked((int)0x80004005));
            }

            var result = ApplyCompositionMutation(
                CompositionMutation.RootVisualRebind);
            if (!result.Succeeded)
            {
                return RootVisualRebindResult.Failed(
                    result.ObservedState,
                    result.HResult);
            }

            return result.Outcome == CompositionMutationOutcome.DeviceRecovered
                ? RootVisualRebindResult.DeviceRecovered(
                    result.ObservedState,
                    result.CompositionGeneration)
                : RootVisualRebindResult.Rebound(
                    result.CompositionGeneration);
        }

        /// <summary>
        /// Bounds, parent-position notifications, root binding, and commits all
        /// cross this single boundary. A DirectComposition failure can surface
        /// during a hidden resize or warm reopen before WM_PAINT is delivered;
        /// catch it here, rebuild the Reactor-owned device once, and leave the
        /// STA alive for the caller to either continue or enter software
        /// browser recovery.
        /// </summary>
        private CompositionMutationResult ApplyCompositionMutation(
            CompositionMutation mutation)
        {
            if (_disposed || _controller == null || _composition == null)
            {
                return CompositionMutationResult.Failed(
                    CompositionDeviceState.Unavailable,
                    unchecked((int)0x80004005));
            }

            var health = _composition.CheckDeviceState();
            if (health.State != CompositionDeviceState.Ready)
            {
                var recovered = RecoverCompositionDeviceCore(health);
                return recovered.Outcome == CompositionDeviceRecoveryOutcome.Recovered
                    ? CompositionMutationResult.DeviceRecovered(
                        health.State,
                        recovered.CompositionGeneration)
                    : CompositionMutationResult.Failed(
                        health.State,
                        recovered.HResult);
            }

            try
            {
                ApplyCompositionMutationUnsafe(mutation);
                return CompositionMutationResult.Applied(
                    _composition.Generation);
            }
            catch (Exception error) when (
                error is COMException ||
                error is InvalidOperationException ||
                error is ObjectDisposedException)
            {
                // CheckDeviceState can still report Ready after the exact
                // mutation failed. Treat the COM boundary itself as the lost-
                // device signal and force one clean device/root replacement.
                var observed = _composition.CheckDeviceState();
                var recovered = RecoverCompositionDeviceCore(observed);
                return recovered.Outcome == CompositionDeviceRecoveryOutcome.Recovered
                    ? CompositionMutationResult.DeviceRecovered(
                        observed.State,
                        recovered.CompositionGeneration)
                    : CompositionMutationResult.Failed(
                        observed.State,
                        recovered.HResult != 0
                            ? recovered.HResult
                            : error.HResult);
            }
        }

        private void ApplyCompositionMutationUnsafe(
            CompositionMutation mutation)
        {
            var controller = _controller ?? throw new ObjectDisposedException(
                nameof(CompositionWebViewHost));
            var composition = _composition ?? throw new ObjectDisposedException(
                nameof(CompositionWebViewHost));

            if (mutation == CompositionMutation.RootVisualRebind)
            {
                // Replace the visual identity instead of detach/commit/wait/
                // reattach. That makes the republish unambiguous without an
                // unbounded synchronous compositor fence on the host STA; the
                // caller performs the bounded fence at its qualified visibility
                // boundary (before an ordinary Show, or after a cold
                // initializer's off-screen leased Show).
                composition.ReplaceRoot(controller);
            }

            if (mutation == CompositionMutation.InitialBind ||
                mutation == CompositionMutation.BoundsAndParent ||
                mutation == CompositionMutation.RootVisualRebind)
            {
                controller.Bounds = CurrentBounds();
            }

            if (mutation == CompositionMutation.InitialBind)
            {
                composition.BindRoot(controller);
            }

            controller.NotifyParentWindowPositionChanged();
            composition.Commit();
        }

        private CompositionDeviceRecoveryResult RecoverCompositionDeviceCore(
            CompositionDeviceHealth observed)
        {
            if (_disposed || _controller == null || _composition == null)
            {
                return CompositionDeviceRecoveryResult.Failed(
                    CompositionDeviceState.Unavailable,
                    unchecked((int)0x80004005));
            }

            DirectCompositionDevice? replacement = null;
            var retired = _composition;
            _composition = null;
            try
            {
                // A DComposition target remains registered against its HWND
                // until every Reactor/WebView reference to the old visual tree
                // has been detached and the target has been released. Retire
                // it before CreateTargetForHwnd; creating the replacement
                // first deterministically fails with 0x88980800 on Enhanced.
                var retirement = retired.RetireForReplacement(_controller);
                if (!retirement.ReplacementSafe)
                {
                    return CompositionDeviceRecoveryResult.Failed(
                        observed.State,
                        retirement.HResult);
                }

                replacement = DirectCompositionDevice.Create(_owner.Handle);
                _controller.Bounds = CurrentBounds();
                replacement.BindRoot(_controller);
                _controller.NotifyParentWindowPositionChanged();
                replacement.Commit();

                _composition = replacement;
                replacement = null;
                return CompositionDeviceRecoveryResult.Recovered(
                    observed,
                    _composition.Generation,
                    CompositionDeviceRecoveryMode.TargetRetiredAndRecreated);
            }
            catch (Exception error) when (
                error is COMException ||
                error is InvalidOperationException ||
                error is ObjectDisposedException)
            {
                // BindRoot can succeed before a later position notification or
                // commit fails. Drop that WebView reference before releasing
                // the replacement target so a later full browser recovery is
                // not left attached to a dead visual.
                try { _controller.RootVisualTarget = null; }
                catch (Exception detachError) when (
                    detachError is COMException ||
                    detachError is InvalidOperationException ||
                    detachError is ObjectDisposedException)
                {
                }
                replacement?.Dispose();
                return CompositionDeviceRecoveryResult.Failed(
                    observed.State,
                    error.HResult);
            }
        }

        /// <summary>
        /// Forwards GTA's normalized cursor state through WebView2's visual-
        /// hosting input contract. Composition controllers do not receive
        /// ordinary child-window mouse messages, so generating DOM events is
        /// not equivalent to browser input and does not reliably activate
        /// React controls.
        /// </summary>
        internal bool SendMouseInput(
            float normalizedX,
            float normalizedY,
            bool pressed,
            bool released,
            int wheelDelta)
        {
            if (_disposed || _controller == null ||
                float.IsNaN(normalizedX) || float.IsInfinity(normalizedX) ||
                float.IsNaN(normalizedY) || float.IsInfinity(normalizedY))
                return false;

            var bounds = CurrentBounds();
            var x = Math.Max(0, Math.Min(
                bounds.Width - 1,
                (int)Math.Round(
                    Math.Max(0f, Math.Min(1f, normalizedX)) *
                    Math.Max(0, bounds.Width - 1))));
            var y = Math.Max(0, Math.Min(
                bounds.Height - 1,
                (int)Math.Round(
                    Math.Max(0f, Math.Min(1f, normalizedY)) *
                    Math.Max(0, bounds.Height - 1))));
            var point = new Point(x, y);
            var moveKeys = _leftButtonDown
                ? CoreWebView2MouseEventVirtualKeys.LeftButton
                : CoreWebView2MouseEventVirtualKeys.None;

            _controller.SendMouseInput(
                CoreWebView2MouseEventKind.Move,
                moveKeys,
                0,
                point);
            _pointerInside = true;

            if (pressed && !_leftButtonDown)
            {
                _leftButtonDown = true;
                _controller.SendMouseInput(
                    CoreWebView2MouseEventKind.LeftButtonDown,
                    CoreWebView2MouseEventVirtualKeys.LeftButton,
                    0,
                    point);
            }

            if (wheelDelta != 0)
            {
                _controller.SendMouseInput(
                    CoreWebView2MouseEventKind.Wheel,
                    _leftButtonDown
                        ? CoreWebView2MouseEventVirtualKeys.LeftButton
                        : CoreWebView2MouseEventVirtualKeys.None,
                    unchecked((uint)wheelDelta),
                    point);
            }

            if (released)
            {
                _controller.SendMouseInput(
                    CoreWebView2MouseEventKind.LeftButtonUp,
                    CoreWebView2MouseEventVirtualKeys.None,
                    0,
                    point);
                _leftButtonDown = false;
            }

            return true;
        }

        internal void ResetMouseInput()
        {
            if (_disposed || _controller == null)
                return;

            if (_leftButtonDown)
            {
                _controller.SendMouseInput(
                    CoreWebView2MouseEventKind.LeftButtonUp,
                    CoreWebView2MouseEventVirtualKeys.None,
                    0,
                    Point.Empty);
            }
            if (_pointerInside)
            {
                _controller.SendMouseInput(
                    CoreWebView2MouseEventKind.Leave,
                    CoreWebView2MouseEventVirtualKeys.None,
                    0,
                    Point.Empty);
            }
            _leftButtonDown = false;
            _pointerInside = false;
        }

        /// <summary>
        /// Captures the browser's current composition surface. This is stronger
        /// evidence than a DOM/layout acknowledgement: it proves Chromium has
        /// supplied concrete pixels for the persistent WebView controller.
        /// </summary>
        internal async Task<byte[]> CapturePreviewAsync()
        {
            if (_disposed || _controller?.CoreWebView2 == null)
                return Array.Empty<byte>();

            using var stream = new MemoryStream();
            await _controller.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png,
                stream);
            return stream.ToArray();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _controllerVisible = false;
            _owner.ClientSizeChanged -= OnOwnerClientSizeChanged;
            _owner.LocationChanged -= OnOwnerLocationChanged;
            _owner.ParentChanged -= OnOwnerLocationChanged;

            try { ResetMouseInput(); }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            var controller = _controller;
            _controller = null;
            if (controller != null)
            {
                try { controller.RootVisualTarget = null; }
                catch (Exception error) when (
                    error is COMException ||
                    error is InvalidOperationException ||
                    error is ObjectDisposedException) { }
                try { controller.Close(); }
                catch (Exception error) when (
                    error is COMException ||
                    error is InvalidOperationException ||
                    error is ObjectDisposedException) { }
            }
            _composition?.Dispose();
            _composition = null;
        }

        private Rectangle CurrentBounds() => new Rectangle(
            Point.Empty,
            new Size(
                Math.Max(1, _owner.ClientSize.Width),
                Math.Max(1, _owner.ClientSize.Height)));

        private void OnOwnerClientSizeChanged(object? sender, EventArgs args) =>
            SynchronizeBounds();

        private void OnOwnerLocationChanged(object? sender, EventArgs args) =>
            NotifyParentWindowPositionChanged();

        private enum CompositionMutation
        {
            InitialBind,
            BoundsAndParent,
            ParentPosition,
            RootVisualRebind,
        }

        private enum CompositionMutationOutcome
        {
            Applied,
            DeviceRecovered,
            Failed,
        }

        private readonly struct CompositionMutationResult
        {
            private CompositionMutationResult(
                CompositionMutationOutcome outcome,
                CompositionDeviceState observedState,
                int hresult,
                int compositionGeneration)
            {
                Outcome = outcome;
                ObservedState = observedState;
                HResult = hresult;
                CompositionGeneration = compositionGeneration;
            }

            internal CompositionMutationOutcome Outcome { get; }
            internal CompositionDeviceState ObservedState { get; }
            internal int HResult { get; }
            internal int CompositionGeneration { get; }
            internal bool Succeeded => Outcome != CompositionMutationOutcome.Failed;

            internal static CompositionMutationResult Applied(int generation) =>
                new CompositionMutationResult(
                    CompositionMutationOutcome.Applied,
                    CompositionDeviceState.Ready,
                    0,
                    generation);

            internal static CompositionMutationResult DeviceRecovered(
                CompositionDeviceState observedState,
                int generation) => new CompositionMutationResult(
                    CompositionMutationOutcome.DeviceRecovered,
                    observedState,
                    0,
                    generation);

            internal static CompositionMutationResult Failed(
                CompositionDeviceState observedState,
                int hresult) => new CompositionMutationResult(
                    CompositionMutationOutcome.Failed,
                    observedState,
                    hresult,
                    0);
        }

        private sealed class DirectCompositionDevice : IDisposable
        {
            private static int _nextGeneration;
            private IDCompositionDevice? _device;
            private IDCompositionTarget? _target;
            private IDCompositionVisual? _root;
            private int _rootVisualRevision = 1;

            private DirectCompositionDevice(
                IDCompositionDevice device,
                IDCompositionTarget target,
                IDCompositionVisual root)
            {
                _device = device;
                _target = target;
                _root = root;
                Generation = System.Threading.Interlocked.Increment(
                    ref _nextGeneration);
            }

            internal int Generation { get; }

            internal int RootVisualRevision => _rootVisualRevision;

            internal static DirectCompositionDevice Create(IntPtr window)
            {
                var interfaceId = typeof(IDCompositionDevice).GUID;
                Marshal.ThrowExceptionForHR(NativeMethods.DCompositionCreateDevice(
                    IntPtr.Zero,
                    ref interfaceId,
                    out var device));
                IDCompositionTarget? target = null;
                IDCompositionVisual? root = null;
                try
                {
                    Marshal.ThrowExceptionForHR(device.CreateTargetForHwnd(
                        window,
                        true,
                        out target));
                    Marshal.ThrowExceptionForHR(device.CreateVisual(out root));
                    Marshal.ThrowExceptionForHR(target.SetRoot(root));
                    Marshal.ThrowExceptionForHR(device.Commit());
                    return new DirectCompositionDevice(device, target, root);
                }
                catch
                {
                    Release(root);
                    Release(target);
                    Release(device);
                    throw;
                }
            }

            internal void BindRoot(CoreWebView2CompositionController controller)
            {
                var root = _root ?? throw new ObjectDisposedException(
                    nameof(DirectCompositionDevice));
                controller.RootVisualTarget = root;
            }

            internal void ReplaceRoot(
                CoreWebView2CompositionController controller)
            {
                var device = _device ?? throw new ObjectDisposedException(
                    nameof(DirectCompositionDevice));
                var target = _target ?? throw new ObjectDisposedException(
                    nameof(DirectCompositionDevice));
                Marshal.ThrowExceptionForHR(device.CreateVisual(out var replacement));
                try
                {
                    Marshal.ThrowExceptionForHR(target.SetRoot(replacement));
                    controller.RootVisualTarget = replacement;
                    var retired = _root;
                    _root = replacement;
                    _rootVisualRevision = unchecked(_rootVisualRevision + 1);
                    if (_rootVisualRevision == 0)
                        _rootVisualRevision = 1;
                    replacement = null!;
                    Release(retired);
                }
                finally
                {
                    Release(replacement);
                }
            }

            internal void Commit()
            {
                var device = _device;
                if (device == null)
                    return;
                Marshal.ThrowExceptionForHR(device.Commit());
            }

            internal int WaitForCommitCompletion()
            {
                var device = _device;
                if (device == null)
                    return unchecked((int)0x80004005);
                try
                {
                    return device.WaitForCommitCompletion();
                }
                catch (COMException error)
                {
                    return error.HResult;
                }
                catch (InvalidOperationException error)
                {
                    return error.HResult != 0
                        ? error.HResult
                        : unchecked((int)0x80004005);
                }
            }

            internal CompositionDeviceHealth CheckDeviceState()
            {
                var device = _device;
                if (device == null)
                {
                    return new CompositionDeviceHealth(
                        CompositionDeviceState.Unavailable,
                        unchecked((int)0x80004005));
                }

                try
                {
                    var result = device.CheckDeviceState(out var valid);
                    return new CompositionDeviceHealth(
                        OverlayPresentationPolicy.ClassifyCompositionDeviceState(
                            available: true,
                            result,
                            valid),
                        result);
                }
                catch (COMException error)
                {
                    return new CompositionDeviceHealth(
                        CompositionDeviceState.QueryFailed,
                        error.HResult);
                }
            }

            public void Dispose()
            {
                if (_device != null)
                    DisposeCore();
            }

            /// <summary>
            /// Retires the one HWND target owned by this device before a
            /// replacement calls CreateTargetForHwnd. Clearing WebView2's root
            /// reference is authoritative: if it fails, the caller must not
            /// risk registering a second target. SetRoot(null), Commit, and COM
            /// release are still attempted in every case so broader browser
            /// recovery can continue from a clean best-effort state.
            /// </summary>
            internal CompositionTargetRetirement RetireForReplacement(
                CoreWebView2CompositionController controller)
            {
                var controllerDetached = false;
                var detachHResult = 0;
                try
                {
                    controller.RootVisualTarget = null;
                    controllerDetached = true;
                }
                catch (Exception error) when (
                    error is COMException ||
                    error is InvalidOperationException ||
                    error is ObjectDisposedException)
                {
                    detachHResult = error.HResult != 0
                        ? error.HResult
                        : unchecked((int)0x80004005);
                }

                var retirementHResult = DisposeCore();
                return controllerDetached
                    ? CompositionTargetRetirement.ReplacementAllowed(
                        retirementHResult)
                    : CompositionTargetRetirement.ReplacementBlocked(
                        detachHResult != 0
                            ? detachHResult
                            : retirementHResult);
            }

            internal readonly struct CompositionTargetRetirement
            {
                private CompositionTargetRetirement(
                    bool replacementSafe,
                    int hresult)
                {
                    ReplacementSafe = replacementSafe;
                    HResult = hresult;
                }

                internal bool ReplacementSafe { get; }
                internal int HResult { get; }

                internal static CompositionTargetRetirement ReplacementAllowed(
                    int hresult) => new CompositionTargetRetirement(
                        replacementSafe: true,
                        hresult: hresult);

                internal static CompositionTargetRetirement ReplacementBlocked(
                    int hresult) => new CompositionTargetRetirement(
                        replacementSafe: false,
                        hresult: hresult != 0
                            ? hresult
                            : unchecked((int)0x80004005));
            }

            private int DisposeCore()
            {
                var firstFailure = 0;
                var target = _target;
                _target = null;
                if (target != null)
                {
                    try
                    {
                        var result = target.SetRoot(null);
                        if (result < 0) firstFailure = result;
                    }
                    catch (COMException error)
                    {
                        firstFailure = error.HResult;
                    }
                }
                if (_device != null)
                {
                    try
                    {
                        var result = _device.Commit();
                        if (firstFailure == 0 && result < 0)
                            firstFailure = result;
                    }
                    catch (COMException error)
                    {
                        if (firstFailure == 0)
                            firstFailure = error.HResult;
                    }
                }
                Release(_root);
                Release(target);
                Release(_device);
                _root = null;
                _device = null;
                return firstFailure;
            }

            private static void Release(object? value)
            {
                if (value != null && Marshal.IsComObject(value))
                    Marshal.ReleaseComObject(value);
            }
        }

        private static class NativeMethods
        {
            [DllImport("dcomp.dll", ExactSpelling = true, PreserveSig = true)]
            internal static extern int DCompositionCreateDevice(
                IntPtr dxgiDevice,
                ref Guid interfaceId,
                [MarshalAs(UnmanagedType.Interface)] out IDCompositionDevice device);
        }

        [ComImport]
        [Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDCompositionDevice
        {
            [PreserveSig] int Commit();
            [PreserveSig] int WaitForCommitCompletion();
            [PreserveSig] int GetFrameStatistics(IntPtr statistics);
            [PreserveSig] int CreateTargetForHwnd(
                IntPtr window,
                [MarshalAs(UnmanagedType.Bool)] bool topmost,
                out IDCompositionTarget target);
            [PreserveSig] int CreateVisual(out IDCompositionVisual visual);
            // Unused factory slots are declared solely to preserve the native
            // IDCompositionDevice vtable up to CheckDeviceState.
            [PreserveSig] int Reserved5();
            [PreserveSig] int Reserved6();
            [PreserveSig] int Reserved7();
            [PreserveSig] int Reserved8();
            [PreserveSig] int Reserved9();
            [PreserveSig] int Reserved10();
            [PreserveSig] int Reserved11();
            [PreserveSig] int Reserved12();
            [PreserveSig] int Reserved13();
            [PreserveSig] int Reserved14();
            [PreserveSig] int Reserved15();
            [PreserveSig] int Reserved16();
            [PreserveSig] int Reserved17();
            [PreserveSig] int Reserved18();
            [PreserveSig] int Reserved19();
            [PreserveSig] int Reserved20();
            [PreserveSig] int Reserved21();
            [PreserveSig] int Reserved22();
            [PreserveSig] int CheckDeviceState(
                [MarshalAs(UnmanagedType.Bool)] out bool valid);
        }

        [ComImport]
        [Guid("EACDD04C-117E-4E17-88F4-D1B12B0E3D89")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDCompositionTarget
        {
            [PreserveSig] int SetRoot(IDCompositionVisual? visual);
        }

        [ComImport]
        [Guid("4D93059D-097B-4651-9A60-F0F25116E2F3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDCompositionVisual
        {
        }
    }

    internal readonly struct CompositionDeviceHealth
    {
        internal CompositionDeviceHealth(
            CompositionDeviceState state,
            int hresult)
        {
            State = state;
            HResult = hresult;
        }

        internal CompositionDeviceState State { get; }
        internal int HResult { get; }
    }

    internal enum CompositionDeviceRecoveryOutcome
    {
        NotRequired,
        Recovered,
        Failed,
    }

    internal enum CompositionDeviceRecoveryMode
    {
        None,
        ExistingTargetRebound,
        TargetRetiredAndRecreated,
    }

    internal readonly struct CompositionDeviceRecoveryResult
    {
        private CompositionDeviceRecoveryResult(
            CompositionDeviceRecoveryOutcome outcome,
            CompositionDeviceState observedState,
            int hresult,
            int compositionGeneration,
            CompositionDeviceRecoveryMode recoveryMode)
        {
            Outcome = outcome;
            ObservedState = observedState;
            HResult = hresult;
            CompositionGeneration = compositionGeneration;
            RecoveryMode = recoveryMode;
        }

        internal CompositionDeviceRecoveryOutcome Outcome { get; }
        internal CompositionDeviceState ObservedState { get; }
        internal int HResult { get; }
        internal int CompositionGeneration { get; }
        internal CompositionDeviceRecoveryMode RecoveryMode { get; }

        internal static CompositionDeviceRecoveryResult NotRequired(
            CompositionDeviceHealth observed) => new CompositionDeviceRecoveryResult(
                CompositionDeviceRecoveryOutcome.NotRequired,
                observed.State,
                observed.HResult,
                0,
                CompositionDeviceRecoveryMode.None);

        internal static CompositionDeviceRecoveryResult Recovered(
            CompositionDeviceHealth observed,
            int generation,
            CompositionDeviceRecoveryMode recoveryMode) =>
                new CompositionDeviceRecoveryResult(
                CompositionDeviceRecoveryOutcome.Recovered,
                observed.State,
                observed.HResult,
                generation,
                recoveryMode);

        internal static CompositionDeviceRecoveryResult Failed(
            CompositionDeviceState observedState,
            int hresult) => new CompositionDeviceRecoveryResult(
                CompositionDeviceRecoveryOutcome.Failed,
                observedState,
                hresult,
                0,
                CompositionDeviceRecoveryMode.None);
    }

    internal enum RootVisualRebindOutcome
    {
        Rebound,
        CompositionDeviceRecovered,
        Failed,
    }

    internal readonly struct RootVisualRebindResult
    {
        private RootVisualRebindResult(
            RootVisualRebindOutcome outcome,
            CompositionDeviceState deviceState,
            int hresult,
            int compositionGeneration)
        {
            Outcome = outcome;
            DeviceState = deviceState;
            HResult = hresult;
            CompositionGeneration = compositionGeneration;
        }

        internal RootVisualRebindOutcome Outcome { get; }
        internal CompositionDeviceState DeviceState { get; }
        internal int HResult { get; }
        internal int CompositionGeneration { get; }
        internal bool Succeeded => Outcome != RootVisualRebindOutcome.Failed;

        internal static RootVisualRebindResult Rebound(int generation) =>
            new RootVisualRebindResult(
                RootVisualRebindOutcome.Rebound,
                CompositionDeviceState.Ready,
                0,
                generation);

        internal static RootVisualRebindResult DeviceRecovered(
            CompositionDeviceState observedState,
            int generation) => new RootVisualRebindResult(
                RootVisualRebindOutcome.CompositionDeviceRecovered,
                observedState,
                0,
                generation);

        internal static RootVisualRebindResult Failed(
            CompositionDeviceState observedState,
            int hresult) => new RootVisualRebindResult(
                RootVisualRebindOutcome.Failed,
                observedState,
                hresult,
                0);
    }
}
