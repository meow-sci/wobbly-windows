using System.Diagnostics;
using System.Security.Cryptography;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Internal;

namespace WobblyWindows.Runtime;

internal static class Compatibility
{
    public const string GameBuild = "2026.10.25.5707";
    private const string NativeHash = "F9BA4A3BE42035DDA61F0F3DF3B97632FD69627BEF5D267641CD7A4D189BC331";

    public static unsafe void Validate()
    {
        // Check loaded metadata and the actual loaded native module before reading any internal pointer.
        Require(typeof(KSA.Program).Module.ModuleVersionId == new Guid("a6318490-1e4f-4d4c-9f1a-b48f97f44be4"), "KSA build");
        Require(typeof(ImGui).Module.ModuleVersionId == new Guid("ed379898-6c19-48b8-a05b-be57942bf38e"), "BRUTAL ImGui build");
        Require(IntPtr.Size == 8 && sizeof(ImGuiContext) == 11320 && sizeof(ImGuiWindow) == 1224
            && sizeof(ImDrawList) == 224 && sizeof(ImDrawData) == 72 && sizeof(ImDrawCmd) == 72
            && sizeof(ImDrawVert) == 20 && sizeof(ImVector<ushort>) == 16 && sizeof(ImGuiContextHook) == 32, "native layouts");
        using var process = Process.GetCurrentProcess();
        string? modulePath = null;
        foreach (ProcessModule module in process.Modules)
        {
            if (string.Equals(module.ModuleName, "imgui.dll", StringComparison.OrdinalIgnoreCase))
            { modulePath = module.FileName; break; }
        }
        Require(modulePath != null, "loaded native imgui.dll (Windows x64 profile)");
        using var file = File.OpenRead(modulePath!);
        Require(Convert.ToHexString(SHA256.HashData(file)) == NativeHash, "native ImGui build");
        Require(ImGui.GetVersion().ToString() == "1.92.2", "native ImGui version");
    }

    private static void Require(bool condition, string item)
    {
        if (!condition) throw new NotSupportedException($"Unsupported {item}. Wobble is disabled; this release supports KSA {GameBuild} with its original ImGui binaries.");
    }
}
