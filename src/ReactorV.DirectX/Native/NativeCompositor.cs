using System;
using System.Runtime.InteropServices;

namespace RageWebUI.DirectX.Native
{
    internal static class NativeCompositor
    {
        private const string LibraryName = "RageWebUI.Native.dll";

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

        public static void Shutdown() => RWUI_Shutdown();

        public static void SetVisible(bool visible) => RWUI_SetVisible(visible ? 1 : 0);

        public static bool SubmitFrame(IntPtr pixels, int width, int height, int stride, ulong generation) =>
            RWUI_SubmitFrame(pixels, width, height, stride, generation) != 0;

        public static bool PollInput(out NativeInputEvent inputEvent) => RWUI_PollInput(out inputEvent) != 0;

        public static bool TryGetStats(out RenderStats stats) => RWUI_GetStats(out stats) != 0;

        public static bool StartTest(RenderApi api, int width, int height, string title) =>
            RWUI_TestStart(api, width, height, title) != 0;

        public static void StopTest() => RWUI_TestStop();

        public static bool IsTestRunning => RWUI_TestIsRunning() != 0;
    }
}

