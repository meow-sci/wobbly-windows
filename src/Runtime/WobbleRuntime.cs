using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Internal;
using WobblyWindows.Core;
using WobblyWindows.Rendering;

namespace WobblyWindows.Runtime;

internal unsafe sealed partial class WobbleRuntime : IDisposable
{
    private readonly DrawPublication _publication = new();
    private readonly List<OwnedDrawList> _outputs = [];
    private readonly Dictionary<uint, WindowState> _windows = [];
    private readonly Dictionary<nint, WindowState> _owners = [];
    private readonly List<uint> _expired = [];
    private readonly string _settingsPath;
    private readonly ImGuiContextHookCallback _callback;
    private readonly ImGuiID[] _hookIds = new ImGuiID[3];
    private ImGuiContext* _context;
    private bool _checked, _disposed, _insideCallback;
    private int _lastFrame = -1;
    private string? _fault;
    private int _activeCount, _outputVertices;
    private double _processingMs;
    private string _frameStatus = "Waiting for the main UI frame.";
    private WobbleSettings _settings;

    internal sealed class WindowState
    {
        public required uint Id;
        public required string Name;
        public nint Pointer;
        public int LastSeen;
        public bool Eligible, Blocked;
        public readonly SpringSurface Surface = new();
    }

    public WobbleRuntime(string directory)
    {
        _settingsPath = Path.Combine(directory, "settings.json");
        try { _settings = WobbleSettings.Load(_settingsPath); }
        catch (Exception error) { _settings = new(); _saveError = "Settings reset to defaults: " + error.Message; }
        _callback = OnContextHook;
    }

    public void PrepareMainFrame()
    {
        if (_disposed || _fault != null) return;
        try
        {
            if (!_checked) { Compatibility.Validate(); _checked = true; }
            ImGuiContextPtr current = ImGui.GetCurrentContext();
            if (current.IsNull()) return;
            if (_context == current.Ptr) return;
            if (_context != null) Detach();
            _context = current.Ptr;
            nint callback = Marshal.GetFunctionPointerForDelegate(_callback);
            ReadOnlySpan<ImGuiContextHookType> types = [ImGuiContextHookType.NewFramePre, ImGuiContextHookType.RenderPost, ImGuiContextHookType.Shutdown];
            for (int i = 0; i < types.Length; ++i)
            {
                var hook = new ImGuiContextHook { Type = types[i], Callback = callback };
                _hookIds[i] = ImGui.Internal.AddContextHook(current, &hook);
                if (_hookIds[i].Value == 0) throw new InvalidOperationException("ImGui did not register a context hook.");
            }
            _frameStatus = "Ready";
            Log("Attached to the main ImGui context. Native mouse hitboxes are unchanged.");
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnContextHook(ImGuiContext* context, ImGuiContextHook* hook)
    {
        if (_disposed || context != _context || _insideCallback) return;
        _insideCallback = true;
        try
        {
            switch (hook->Type)
            {
                case ImGuiContextHookType.NewFramePre:
                    _publication.Restore();
                    break;
                case ImGuiContextHookType.RenderPost:
                    if (_fault == null) Render(new ImGuiContextPtr(context));
                    break;
                case ImGuiContextHookType.Shutdown:
                    _publication.Restore();
                    _context = null; Array.Clear(_hookIds); _windows.Clear(); _owners.Clear(); _lastFrame = -1;
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
        finally { _insideCallback = false; }
    }

    private void Render(ImGuiContextPtr context)
    {
        int frame = context.FrameCount;
        if (frame == _lastFrame) return;
        _lastFrame = frame;
        _activeCount = _outputVertices = 0; _processingMs = 0;
        if (!_settings.Enabled || _settings.Strength <= 0)
        { _windows.Clear(); _owners.Clear(); _frameStatus = "Disabled"; return; }
        if (_publication.IsPublished) throw new InvalidOperationException("Previous frame was not restored.");
        ImDrawDataPtr data = ImGui.GetDrawData();
        if (data.IsNull() || !data.Valid || data.CmdListsCount != data.CmdLists.Count) return;
        if (data.CmdLists.Count > 4096 || context.Windows.Count > 4096) return;
        long start = Stopwatch.GetTimestamp();
        try
        {
            ImGuiID viewportId = ImGui.GetMainViewport().ID;
            Vector4 viewport = new(data.DisplayPos.X, data.DisplayPos.Y,
                data.DisplayPos.X + data.DisplaySize.X, data.DisplayPos.Y + data.DisplaySize.Y);
            _owners.Clear();
            // Discover roots first so descendants can invalidate an entire family.
            foreach (ImGuiWindowPtr window in context.Windows.Span)
            {
                if (window.IsNull() || !window.Active || window.LastFrameActive != frame || window.RootWindow.Ptr != window.Ptr) continue;
                uint id = window.ID.Value;
                if (!_windows.TryGetValue(id, out WindowState? state))
                {
                    state = new WindowState { Id = id, Name = Marshal.PtrToStringUTF8((nint)window.Name.Pointer) ?? "Window" };
                    _windows.Add(id, state);
                }
                Vector2 pos = V(window.Pos), size = V(window.Size);
                if (state.Pointer != (nint)window.Ptr || state.LastSeen < frame - 1) state.Surface.Reset(pos, size);
                state.Pointer = (nint)window.Ptr; state.LastSeen = frame; state.Blocked = false;
                state.Eligible = Eligible(window, viewportId) && !_settings.ExcludedWindows.Contains(id)
                    && pos.X >= viewport.X + 2 && pos.Y >= viewport.Y + 2
                    && pos.X + size.X <= viewport.Z - 2 && pos.Y + size.Y <= viewport.W - 2;
            }
            foreach (ImGuiWindowPtr window in context.Windows.Span)
            {
                if (window.IsNull() || !window.Active || window.LastFrameActive != frame || window.RootWindow.IsNull()) continue;
                if (!_windows.TryGetValue(window.RootWindow.ID.Value, out WindowState? state) || state.LastSeen != frame) continue;
                if (!window.DrawList.IsNull())
                {
                    nint key = (nint)window.DrawList.Ptr;
                    if (_owners.TryGetValue(key, out WindowState? previous) && previous != state)
                    { previous.Blocked = state.Blocked = true; }
                    else _owners[key] = state;
                }
                if (window.Hidden || window.ViewportId.Value != viewportId.Value) state.Blocked = true;
                // A popup can have its own root while still depending on an ordinary window.
                if ((window.Flags & (ImGuiWindowFlags.Popup | ImGuiWindowFlags.Modal | ImGuiWindowFlags.ChildMenu)) != 0)
                {
                    ImGuiWindowPtr parent = window.ParentWindow;
                    for (int depth = 0; !parent.IsNull() && depth < 32; ++depth, parent = parent.ParentWindow)
                        if (!parent.RootWindow.IsNull() && _windows.TryGetValue(parent.RootWindow.ID.Value, out var ancestor)) ancestor.Blocked = true;
                }
            }
            foreach (ImDrawListPtr list in data.CmdLists.Span)
            {
                if (list.IsNull()) throw new InvalidOperationException("Null submitted draw list.");
                if (!_owners.TryGetValue((nint)list.Ptr, out var owner)) continue;
                foreach (ImDrawCmd command in list.CmdBuffer.Span)
                    if (command.UserCallback != 0) { owner.Blocked = true; break; }
            }
            _expired.Clear();
            foreach (WindowState state in _windows.Values)
            {
                if (state.LastSeen != frame) { _expired.Add(state.Id); continue; }
                var window = new ImGuiWindowPtr((ImGuiWindow*)state.Pointer);
                Vector2 pos = V(window.Pos), size = V(window.Size);
                if (!state.Eligible || state.Blocked) { state.Surface.Reset(pos, size); continue; }
                ImGuiWindowPtr moving = context.MovingWindow;
                bool dragging = !moving.IsNull() && moving.RootWindow.Ptr == window.Ptr;
                state.Surface.Advance(pos, size, V(context.IO.MousePos), dragging, context.IO.DeltaTime, _settings);
                if (state.Surface.IsActive) ++_activeCount;
            }
            foreach (uint id in _expired) _windows.Remove(id);
            if (_activeCount == 0 && !_identityOutput) { _frameStatus = "Idle — original geometry"; return; }
            _publication.Lists.Reset();
            int outputIndex = 0, vertices = 0, indices = 0, vertexBudget = GeometryProcessor.MaximumVertices, triangleBudget = 120_000;
            foreach (ImDrawListPtr list in data.CmdLists.Span)
            {
                ImDrawListPtr result = list;
                if (_owners.TryGetValue((nint)list.Ptr, out var state) && state.Eligible && !state.Blocked
                    && (state.Surface.IsActive || _identityOutput))
                {
                    if (outputIndex == _outputs.Count) _outputs.Add(new OwnedDrawList());
                    OwnedDrawList output = _outputs[outputIndex++];
                    bool built;
                    if (_identityOutput)
                    {
                        built = list.VtxBuffer.Count <= vertexBudget;
                        if (built) { output.CopyFrom(list.Ptr); vertexBudget -= list.VtxBuffer.Count; }
                    }
                    else built = GeometryProcessor.Build(list.Ptr, output, state.Surface, viewport,
                        Math.Max(data.FramebufferScale.X, data.FramebufferScale.Y), ref vertexBudget, ref triangleBudget);
                    if (!built)
                    { _frameStatus = "Geometry budget or unsupported data — original frame retained"; return; }
                    result = new ImDrawListPtr(output.Pointer);
                    _outputVertices += output.Vertices.Count;
                }
                _publication.Lists.Add(result);
                vertices = checked(vertices + result.VtxBuffer.Count); indices = checked(indices + result.IdxBuffer.Count);
            }
            _publication.Publish(data.Ptr, vertices, indices);
            _frameStatus = _identityOutput ? "Identity output check" : "Wobbling";
        }
        finally { _processingMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    }

    private static bool Eligible(ImGuiWindowPtr window, ImGuiID viewport)
    {
        const ImGuiWindowFlags excluded = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoMouseInputs
            | ImGuiWindowFlags.ChildWindow | ImGuiWindowFlags.Popup | ImGuiWindowFlags.Modal
            | ImGuiWindowFlags.Tooltip | ImGuiWindowFlags.ChildMenu | ImGuiWindowFlags.DockNodeHost;
        return (window.Flags & excluded) == 0 && !window.Hidden && !window.Collapsed && !window.DockIsActive
            && window.DockNodeAsHost.IsNull() && window.ViewportId.Value == viewport.Value;
    }

    private static Vector2 V(Brutal.Numerics.float2 value) => new(value.X, value.Y);
    internal void Fail(Exception error)
    {
        _publication.Restore();
        if (_fault != null) return;
        _fault = error.Message;
        Log("Effect disabled: " + error);
    }
    internal static void Log(string message) { try { Console.WriteLine("[wobbly-windows] " + message); } catch (Exception) { } }

    private void Detach()
    {
        _publication.Restore();
        if (_context != null)
        {
            foreach (ImGuiID id in _hookIds) if (id.Value != 0) ImGui.Internal.RemoveContextHook(_context, id);
            _context = null;
        }
        Array.Clear(_hookIds); _lastFrame = -1; _windows.Clear(); _owners.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach(); _disposed = true;
        _publication.Dispose();
        foreach (OwnedDrawList output in _outputs) output.Dispose();
        _outputs.Clear();
        GC.KeepAlive(_callback);
    }
}
