using System.Runtime.InteropServices;
using Brutal.ImGuiApi;

namespace WobblyWindows.Rendering;

internal unsafe sealed class OwnedDrawList : IDisposable
{
    public ImDrawList* Pointer { get; } = (ImDrawList*)NativeMemory.AllocZeroed((nuint)sizeof(ImDrawList));
    public NativeBuffer<ImDrawVert> Vertices { get; } = new();
    public NativeBuffer<ushort> Indices { get; } = new();
    public NativeBuffer<ImDrawCmd> Commands { get; } = new();
    private bool _disposed;

    public OwnedDrawList() { if (Pointer == null) throw new OutOfMemoryException(); }
    public void Reset() { Vertices.Reset(); Indices.Reset(); Commands.Reset(); }
    public void CopyFrom(ImDrawList* source)
    {
        Reset(); Vertices.AddRange(source->VtxBuffer.Span); Indices.AddRange(source->IdxBuffer.Span);
        Commands.AddRange(source->CmdBuffer.Span); Complete(source->Flags);
    }
    public void Complete(ImDrawListFlags flags)
    {
        // Only audited backend fields are populated. No native draw-list methods may use this object.
        Pointer->VtxBuffer = Vertices.View; Pointer->IdxBuffer = Indices.View;
        Pointer->CmdBuffer = Commands.View; Pointer->Flags = flags;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Vertices.Dispose(); Indices.Dispose(); Commands.Dispose(); NativeMemory.Free(Pointer);
    }
}
