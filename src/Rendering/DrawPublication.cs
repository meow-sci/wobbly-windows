using Brutal.ImGuiApi;

namespace WobblyWindows.Rendering;

/// <summary>Substitutes a borrowed draw-data header until all frame consumers finish.</summary>
internal unsafe sealed class DrawPublication : IDisposable
{
    public NativeBuffer<ImDrawListPtr> Lists { get; } = new();
    private ImDrawData* _data;
    private ImVector<ImDrawListPtr> _original;
    private int _listCount, _vertices, _indices;
    public bool IsPublished => _data != null;

    public void Publish(ImDrawData* data, int vertices, int indices)
    {
        if (_data != null) throw new InvalidOperationException("Draw data already published.");
        _original = data->CmdLists; _listCount = data->CmdListsCount;
        _vertices = data->TotalVtxCount; _indices = data->TotalIdxCount;
        _data = data;
        data->CmdLists = Lists.View; data->CmdListsCount = Lists.Count;
        data->TotalVtxCount = vertices; data->TotalIdxCount = indices;
    }

    public void Restore()
    {
        if (_data == null) return;
        _data->CmdLists = _original; _data->CmdListsCount = _listCount;
        _data->TotalVtxCount = _vertices; _data->TotalIdxCount = _indices;
        _data = null; _original = default;
    }

    public void Dispose() { Restore(); Lists.Dispose(); }
}
