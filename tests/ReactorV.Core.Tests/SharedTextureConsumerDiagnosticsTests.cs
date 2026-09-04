using System.Runtime.InteropServices;
using RageWebUI.DirectX.Native;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class SharedTextureConsumerDiagnosticsTests
{
    [Fact]
    public void DriverErrorUsesReservedSlotWithoutChangingAbi()
    {
        Assert.Equal(128, Marshal.SizeOf<SharedTextureConsumerDiagnostics>());
        Assert.Equal(20, Marshal.OffsetOf<SharedTextureConsumerDiagnostics>(
            nameof(SharedTextureConsumerDiagnostics.LastImportHresult)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<SharedTextureConsumerDiagnostics>(
            nameof(SharedTextureConsumerDiagnostics.DiscoveryMisses)).ToInt32());
        var diagnostics = SharedTextureConsumerDiagnostics.CreateRequest();
        diagnostics.LastImportError = 9;
        diagnostics.LastImportHresult = 0x80070057;
        Assert.Contains("import_error=9 import_hresult=0x80070057", diagnostics.ToTraceDetail());
    }
}
