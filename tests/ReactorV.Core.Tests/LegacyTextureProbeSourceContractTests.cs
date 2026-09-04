using System;
using System.IO;
using Xunit;

namespace RageWebUI.Core.Tests;

public sealed class LegacyTextureProbeSourceContractTests
{
    [Fact]
    public void Probe_is_opt_in_and_legacy_only()
    {
        var source = Read("native", "renderhook", "RenderHookMain.cpp");
        var block = source.Substring(source.IndexOf("if (edition == reactorv::renderhook::RenderHookEdition::Legacy &&", StringComparison.Ordinal));
        block = block.Substring(0, block.IndexOf("const std::int32_t armResult", StringComparison.Ordinal));
        Assert.Contains("ReactorV.LegacyLiveTest.json", block);
        Assert.Contains("ReactorV.LegacyTextureProbe.enabled", block);
        Assert.Contains("ReactorV.TextureProbe.Partner.exe", block);
        Assert.Contains("RWUI_ConfigureLegacyTextureProbe", block);
    }

    [Fact]
    public void Diagnostic_present_has_no_wait_readback_logging_or_input_authority()
    {
        var source = Read("native", "src", "LegacyTextureProbe.cpp");
        var draw = source.Substring(source.IndexOf("bool LegacyTextureProbe::Draw(", StringComparison.Ordinal));
        Assert.Contains("std::try_to_lock", draw);
        Assert.Contains("AcquireSync(1, 0)", draw);
        foreach (var forbidden in new[] { "CreateTexture2D", "CreateProcess", "Sleep(", "WaitForSingleObject", "->Map(", "Log(", "SetCapture", "SetVisible" })
            Assert.DoesNotContain(forbidden, draw);
        var compositor = Read("native", "src", "DirectXCompositor.cpp");
        Assert.Contains("textureProbe_.Draw(*probeRenderer_", compositor);
        Assert.Contains("probeRenderer_->InvalidateBackBuffer()", compositor);
        Assert.Contains("menu_ready=UNMODIFIED", source);
    }

    [Fact]
    public void Visibility_control_is_last_before_present_and_separates_submission_from_commit()
    {
        var source = Read("native", "src", "HookManager.cpp");
        foreach (var method in new[] { "PresentHook(", "Present1Hook(" })
        {
            var start = source.IndexOf("HRESULT STDMETHODCALLTYPE " + method, StringComparison.Ordinal);
            var end = source.IndexOf("\nHRESULT STDMETHODCALLTYPE ", start + 1, StringComparison.Ordinal);
            var body = source.Substring(start, end - start);
            Assert.Contains("if (IsTestPresent(flags))", body);
            Assert.True(body.IndexOf("RenderTargetSwapChain(swapChain)", StringComparison.Ordinal) <
                body.IndexOf("RenderLegacyVisibilityProbe(swapChain)", StringComparison.Ordinal));
            Assert.True(body.IndexOf("RenderLegacyVisibilityProbe(swapChain)", StringComparison.Ordinal) <
                body.IndexOf("const auto result = original", StringComparison.Ordinal));
            Assert.Contains("if (visibilityProbeDrawn) g_compositor.RecordLegacyProbePresent(result)", body);
        }
    }

    [Fact]
    public void Visibility_control_bypasses_shaders_with_bounded_nonblocking_pixel_checks()
    {
        var source = Read("native", "src", "LegacyVisibilityProbe.cpp");
        var start = source.IndexOf("void LegacyVisibilityProbe::PollSample(", StringComparison.Ordinal);
        var end = source.IndexOf("void LegacyVisibilityProbe::Presented(", StringComparison.Ordinal);
        var hotPath = source.Substring(start, end - start);
        Assert.Contains("D3D11_ASYNC_GETDATA_DONOTFLUSH", hotPath);
        Assert.Contains("D3D11_MAP_FLAG_DO_NOT_WAIT", hotPath);
        Assert.Contains("std::try_to_lock", hotPath);
        Assert.Contains("chain->GetBuffer(0", hotPath);
        Assert.Contains("context_->ClearView", hotPath);
        Assert.Contains("context_->SetPredication(predicate.Get(), predicateValue)", hotPath);
        foreach (var forbidden in new[] { "Sleep(", "CreateTexture2D", "CreateRenderTargetView", "CreateProcess", "Report(", "->Flush(", "SetCapture", "SetVisible" })
            Assert.DoesNotContain(forbidden, hotPath);
        Assert.Contains("context_->Map(staging_.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT", hotPath);
        Assert.Contains("onscreen_visibility=USER_VERIFICATION_REQUIRED menu_ready=UNMODIFIED", source);
        Assert.Contains("visibilityProbe_.Invalidate()", Read("native", "src", "DirectXCompositor.cpp"));
    }

    [Fact]
    public void Local_texture_control_uses_production_renderer_without_external_sharing()
    {
        var source = Read("native", "src", "LegacyVisibilityProbe.cpp");
        Assert.Contains("device->CreateTexture2D(&d, &initial, &texture)", source);
        Assert.Contains("std::make_unique<D3D11OverlayRenderer>", source);
        Assert.Contains("patternRenderer_->RenderShared(actual.Get(), patternView_.Get(), 1, false)", source);
        Assert.Contains("texture_matches=", source);
        Assert.Contains("texture_mismatches=", source);
        Assert.Contains("texture_checks_not_run=", source);
        foreach (var forbidden in new[] { "OpenSharedResource", "CreateSharedHandle", "D3D11_RESOURCE_MISC_SHARED", "RWUI_SetVisible", "g_inputQueue" })
            Assert.DoesNotContain(forbidden, source);
        Assert.Contains("patternRenderer_.reset(); patternView_.Reset()", source);
    }

    [Fact]
    public void Partner_uses_restricted_inheritance_and_does_not_reinterpret_nt_as_kmt()
    {
        var source = Read("native", "src", "LegacyTextureProbe.cpp");
        Assert.Contains("PROC_THREAD_ATTRIBUTE_HANDLE_LIST", source);
        Assert.Contains("CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT", source);
        Assert.Contains("ValidPacket(*m.value, GetProcessId(owner))", source);
        Assert.Contains("if (kind == Kind::Nt)", source);
        Assert.Contains("OpenSharedResource1(duplicated.value", source);
        Assert.Contains("OpenSharedResource(handle", source);
        Assert.Contains("for (auto& result : p.results) result = E_PENDING", source);
    }

    private static string Read(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "ReactorV.json"))) current = current.Parent;
        Assert.NotNull(current);
        return File.ReadAllText(Path.Combine(current!.FullName, Path.Combine(parts)));
    }
}
