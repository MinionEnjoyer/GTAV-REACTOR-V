namespace RageWebUI.DirectX.Native
{
    internal enum SharedTextureSubmitStatus : uint
    {
        UnknownFailure = 0,
        Submitted = 1,
        Backpressure = 2,
        SessionInvalid = 3,
        AdapterOrResourceInvalid = 4,
        DeviceOrCopyFailure = 5,
        ProducerStopped = 6,
        InvalidFrame = 7
    }
}
