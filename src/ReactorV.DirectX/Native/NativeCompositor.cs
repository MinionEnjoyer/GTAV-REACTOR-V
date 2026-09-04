using System;
using System.Runtime.InteropServices;

namespace RageWebUI.DirectX.Native
{
    internal static class NativeCompositor
    {
        private const string LibraryName = "RageWebUI.Native.dll";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_ArmEnhancedHook();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_Initialize(IntPtr targetWindow);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RWUI_Shutdown();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RWUI_SetVisible(int visible);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_SubmitFrame(
            IntPtr bgraPixels,
            int width,
            int height,
            int stride,
            ulong generation);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_GetSharedTextureCapabilities(ref SharedTextureCapabilities capabilities);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_StartSharedTextureProducer(uint targetGtaProcessId);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RWUI_StopSharedTextureProducer();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_SetSharedTextureProducerVisible(int visible);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_SubmitSharedTexture(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            uint dxgiFormat,
            ulong generation);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint RWUI_SubmitSharedTextureStatus(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            uint dxgiFormat,
            ulong generation);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_ProbeSharedTexture(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            uint dxgiFormat,
            ulong generation);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint RWUI_ProbeSharedTextureStatus(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            uint dxgiFormat,
            ulong generation);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_GetSharedTextureProducerDiagnostics(
            ref SharedTextureProducerDiagnostics diagnostics);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_GetSharedTextureConsumerDiagnostics(
            ref SharedTextureConsumerDiagnostics diagnostics);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_PollInput(out NativeInputEvent inputEvent);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_GetStats(out RenderStats stats);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int RWUI_TestStart(RenderApi api, int width, int height, string title);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RWUI_TestStop();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RWUI_TestIsRunning();

        public static bool Initialize(IntPtr targetWindow) => RWUI_Initialize(targetWindow) != 0;

        public static bool ArmEnhancedHook() => RWUI_ArmEnhancedHook() != 0;

        public static void Shutdown() => RWUI_Shutdown();

        public static void SetVisible(bool visible) => RWUI_SetVisible(visible ? 1 : 0);

        public static bool SubmitFrame(IntPtr pixels, int width, int height, int stride, ulong generation) =>
            RWUI_SubmitFrame(pixels, width, height, stride, generation) != 0;

        public static bool TryGetSharedTextureCapabilities(out SharedTextureCapabilities capabilities)
        {
            capabilities = SharedTextureCapabilities.CreateRequest();
            try
            {
                return RWUI_GetSharedTextureCapabilities(ref capabilities) != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static bool StartSharedTextureProducer(uint targetGtaProcessId)
        {
            if (targetGtaProcessId == 0) return false;
            try
            {
                return RWUI_StartSharedTextureProducer(targetGtaProcessId) != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static void StopSharedTextureProducer()
        {
            try
            {
                RWUI_StopSharedTextureProducer();
            }
            catch (DllNotFoundException)
            {
                // A missing optional producer ABI is already stopped.
            }
            catch (EntryPointNotFoundException)
            {
                // Older native builds retain the CPU/windowed fallback.
            }
            catch (BadImageFormatException)
            {
                // An incompatible native image cannot own a live producer.
            }
        }

        public static bool SetSharedTextureProducerVisible(bool visible)
        {
            try
            {
                return RWUI_SetSharedTextureProducerVisible(visible ? 1 : 0) != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static bool SubmitSharedTexture(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            uint dxgiFormat,
            ulong generation)
        {
            try
            {
                return RWUI_SubmitSharedTexture(
                    sharedTextureHandle,
                    width,
                    height,
                    dxgiFormat,
                    generation) != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static SharedTextureSubmitStatus SubmitSharedTextureStatus(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            uint dxgiFormat,
            ulong generation)
        {
            try
            {
                return (SharedTextureSubmitStatus)RWUI_SubmitSharedTextureStatus(
                    sharedTextureHandle,
                    width,
                    height,
                    dxgiFormat,
                    generation);
            }
            catch (EntryPointNotFoundException)
            {
                // Compatibility with an older native image. A rejection is
                // deliberately classified as unknown, never as backpressure.
                return SubmitSharedTexture(
                    sharedTextureHandle,
                    width,
                    height,
                    dxgiFormat,
                    generation)
                    ? SharedTextureSubmitStatus.Submitted
                    : SharedTextureSubmitStatus.UnknownFailure;
            }
            catch (DllNotFoundException)
            {
                return SharedTextureSubmitStatus.ProducerStopped;
            }
            catch (BadImageFormatException)
            {
                return SharedTextureSubmitStatus.ProducerStopped;
            }
        }

        public static bool ProbeSharedTexture(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            uint dxgiFormat,
            ulong generation)
        {
            try
            {
                return RWUI_ProbeSharedTexture(
                    sharedTextureHandle,
                    width,
                    height,
                    dxgiFormat,
                    generation) != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static SharedTextureSubmitStatus ProbeSharedTextureStatus(
            IntPtr sharedTextureHandle,
            int width,
            int height,
            uint dxgiFormat,
            ulong generation)
        {
            try
            {
                return (SharedTextureSubmitStatus)RWUI_ProbeSharedTextureStatus(
                    sharedTextureHandle,
                    width,
                    height,
                    dxgiFormat,
                    generation);
            }
            catch (EntryPointNotFoundException)
            {
                return ProbeSharedTexture(
                    sharedTextureHandle,
                    width,
                    height,
                    dxgiFormat,
                    generation)
                    ? SharedTextureSubmitStatus.Submitted
                    : SharedTextureSubmitStatus.UnknownFailure;
            }
            catch (DllNotFoundException)
            {
                return SharedTextureSubmitStatus.ProducerStopped;
            }
            catch (BadImageFormatException)
            {
                return SharedTextureSubmitStatus.ProducerStopped;
            }
        }

        public static bool TryGetSharedTextureProducerDiagnostics(
            out SharedTextureProducerDiagnostics diagnostics)
        {
            diagnostics = SharedTextureProducerDiagnostics.CreateRequest();
            try
            {
                return RWUI_GetSharedTextureProducerDiagnostics(ref diagnostics) != 0 &&
                    diagnostics.ByteSize >= SharedTextureProducerDiagnostics.ExpectedByteSize &&
                    diagnostics.Major == 1;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static bool TryGetSharedTextureConsumerDiagnostics(
            out SharedTextureConsumerDiagnostics diagnostics)
        {
            diagnostics = SharedTextureConsumerDiagnostics.CreateRequest();
            try
            {
                return RWUI_GetSharedTextureConsumerDiagnostics(ref diagnostics) != 0 &&
                    diagnostics.ByteSize >= SharedTextureConsumerDiagnostics.ExpectedByteSize &&
                    diagnostics.Major == 1;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static bool PollInput(out NativeInputEvent inputEvent) => RWUI_PollInput(out inputEvent) != 0;

        public static bool TryGetStats(out RenderStats stats) => RWUI_GetStats(out stats) != 0;

        public static bool StartTest(RenderApi api, int width, int height, string title) =>
            RWUI_TestStart(api, width, height, title) != 0;

        public static void StopTest() => RWUI_TestStop();

        public static bool IsTestRunning => RWUI_TestIsRunning() != 0;
    }
}
