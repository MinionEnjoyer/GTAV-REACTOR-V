using System;
using System.Runtime.InteropServices;

namespace RageWebUI.DirectX.Native
{
    public enum RenderApi
    {
        None = 0,
        Direct3D11 = 11,
        Direct3D12 = 12,
    }

    internal enum NativeInputType
    {
        None = 0,
        MouseMove = 1,
        MouseDown = 2,
        MouseUp = 3,
        MouseWheel = 4,
        KeyDown = 5,
        KeyUp = 6,
        Character = 7,
        Resize = 8,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInputEvent
    {
        public NativeInputType Type;
        public int X;
        public int Y;
        public int Delta;
        public int Key;
        public uint Modifiers;
        public ulong Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderStats
    {
        public RenderApi Api;
        public int Width;
        public int Height;
        public ulong SubmittedFrames;
        public ulong RenderedFrames;
        public ulong DroppedFrames;
        public ulong LastFrameGeneration;
    }
}

