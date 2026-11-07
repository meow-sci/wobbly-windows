using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using WobblyWindows.Rendering;

namespace WobblyWindows.Runtime;

internal static class Compatibility
{
    public const string ReferenceGameBuild = "2026.10.25.5707";

    public static void Validate()
    {
        ValidateBindings();
        ValidateRenderer(ImGui.GetIO().BackendFlags);
        // BRUTAL owns native library resolution and the binding/native ABI pairing, just as
        // it does for the game. Versions are diagnostic information, not a binary whitelist.
        WobbleRuntime.Log($"Using host BRUTAL.ImGui / Dear ImGui {ImGui.GetVersion().ToString()} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}).");
    }

    internal static void ValidateBindings()
    {
        Require(IntPtr.Size == 8, $"a 64-bit runtime is required (found {IntPtr.Size * 8}-bit)");
        ValidateVector<ImDrawVert>();
        ValidateVector<ushort>();
        ValidateVector<ImDrawCmd>();
        ValidateVector<ImDrawListPtr>();
    }

    private static unsafe void ValidateVector<T>() where T : unmanaged
    {
        // NativeBuffer builds these headers itself. Check that assumption against the
        // loaded C# bindings before reinterpreting memory, without pinning whole structs.
        Require(sizeof(ImVector<T>) == 2 * sizeof(int) + IntPtr.Size,
            $"BRUTAL ImVector<{typeof(T).Name}> has an incompatible header size ({sizeof(ImVector<T>)} bytes)");
        using var buffer = new NativeBuffer<T>();
        buffer.Add(default);
        ImVector<T> view = buffer.View;
        Require(view.Count == buffer.Count && view.Capacity == buffer.Capacity && view.DataRaw == buffer.Pointer,
            $"BRUTAL ImVector<{typeof(T).Name}> has an incompatible header layout");
    }

    internal static void ValidateRenderer(ImGuiBackendFlags flags) =>
        Require((flags & ImGuiBackendFlags.RendererHasVtxOffset) != 0,
            "the host ImGui renderer must support vertex offsets for 16-bit draw lists (RendererHasVtxOffset)");

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new NotSupportedException($"Wobble is disabled: {reason}.");
    }
}
