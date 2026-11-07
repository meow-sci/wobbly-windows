using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace WobblyWindows.Runtime;

internal unsafe sealed partial class WobbleRuntime
{
    private bool _settingsVisible, _identityOutput;
    private string? _saveError;
    private DateTime _saveAt = DateTime.MaxValue;

    public void DrawMenu()
    {
        if (_disposed || !ImGui.BeginMenu("Wobbly Windows"u8)) return;
        try
        {
            if (Program.HoveredViewport is IGameViewportLifecycle viewport) viewport.SetMenuBarInUse(true);
            if (ImGui.MenuItem("Enabled"u8, default, _settings.Enabled))
            { _settings.Enabled = !_settings.Enabled; Changed(); }
            if (ImGui.MenuItem("Settings"u8, default, _settingsVisible)) _settingsVisible = !_settingsVisible;
        }
        finally { ImGui.EndMenu(); }
    }

    public void DrawUi()
    {
        if (_disposed) return;
        if (DateTime.UtcNow >= _saveAt) SaveSettings();
        if (_settingsVisible) DrawSettings();
    }

    private void DrawSettings()
    {
        ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
        ImGui.SetNextWindowSize(new float2(460, 440), ImGuiCond.FirstUseEver);
        bool visible = ImGui.Begin("Wobbly Windows###WobblyWindows.Settings"u8, ref _settingsVisible);
        try
        {
            if (!visible) return;
            bool enabled = _settings.Enabled;
            if (ImGui.Checkbox("Enable wobble"u8, ref enabled)) { _settings.Enabled = enabled; Changed(); }
            ImGui.TextWrapped("Mouse hitboxes stay unchanged. Let the wobble settle before targeting controls. Turn the effect off for reduced motion."u8);
            if (_fault != null) ImGui.TextWrapped(_fault);
            if (_saveError != null) ImGui.TextWrapped(_saveError);
            ImGui.Spacing();
            ImGui.SeparatorText("Feel"u8);
            if (ImGui.Button(" Subtle "u8)) Preset(0.55f, 4, 0.7f);
            ImGui.SameLine(0, 8);
            if (ImGui.Button(" Classic "u8)) Preset(1, 3.2f, 0.48f);
            ImGui.SameLine(0, 8);
            if (ImGui.Button(" Playful "u8)) Preset(1.5f, 2.7f, 0.36f);
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new float2(6, 6));
            try
            {
                if (ImGui.BeginTable("##feel"u8, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX))
                {
                    try
                    {
                        ImGui.TableSetupColumn("##label"u8, ImGuiTableColumnFlags.WidthStretch, 1);
                        ImGui.TableSetupColumn("##value"u8, ImGuiTableColumnFlags.WidthStretch, 2);
                        float strength = _settings.Strength, frequency = _settings.Frequency, damping = _settings.Damping;
                        Row("Strength"u8); bool changed = ImGui.SliderFloat("##strength"u8, ref strength, 0, 2, "%.2f");
                        Row("Responsiveness"u8); changed |= ImGui.SliderFloat("##frequency"u8, ref frequency, 2, 6, "%.1f Hz");
                        Row("Settling"u8); changed |= ImGui.SliderFloat("##damping"u8, ref damping, 0.25f, 1, "%.2f");
                        ImGui.SetItemTooltip("Higher values settle faster with less oscillation."u8);
                        if (changed) { _settings.Strength = strength; _settings.Frequency = frequency; _settings.Damping = damping; Changed(); }
                    }
                    finally { ImGui.EndTable(); }
                }
            }
            finally { ImGui.PopStyleVar(); }
            if (ImGui.Button(" Reset preferences "u8))
            { _settings = new(); _identityOutput = false; Changed(); }
            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Window exclusions"u8))
            {
                ImGui.TextWrapped("Uncheck a window to keep it still. Docked windows, popups, custom-rendered panels and windows at screen edges are automatically excluded."u8);
                foreach (WindowState state in _windows.Values)
                {
                    if (!state.Eligible && !_settings.ExcludedWindows.Contains(state.Id)) continue;
                    bool allowed = !_settings.ExcludedWindows.Contains(state.Id);
                    if (ImGui.Checkbox($"{state.Name}##exclude{state.Id}", ref allowed))
                    {
                        if (allowed) _settings.ExcludedWindows.Remove(state.Id); else _settings.ExcludedWindows.Add(state.Id);
                        Changed();
                    }
                }
            }
            if (ImGui.CollapsingHeader("Diagnostics"u8))
            {
                ImGui.Text($"Reference game build: {Compatibility.ReferenceGameBuild}");
                ImGui.Text($"Status: {_frameStatus}");
                ImGui.Text($"Active windows: {_activeCount} | output vertices: {_outputVertices}");
                ImGui.Text($"CPU processing: {_processingMs:F3} ms");
                ImGui.Checkbox("Identity output check"u8, ref _identityOutput);
                ImGui.SetItemTooltip("Substitutes identical copies of supported draw lists to check integration without distortion. Not saved."u8);
            }
        }
        finally { ImGui.End(); }
    }

    private static void Row(ImString label)
    {
        ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text(label);
        ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
    }

    private void Preset(float strength, float frequency, float damping)
    { _settings.Strength = strength; _settings.Frequency = frequency; _settings.Damping = damping; Changed(); }
    private void Changed() { _settings.Normalize(); _saveAt = DateTime.UtcNow.AddMilliseconds(500); }
    public void SaveSettings()
    {
        _saveAt = DateTime.MaxValue;
        try { _settings.Save(_settingsPath); _saveError = null; }
        catch (Exception error) { _saveError = "Could not save settings: " + error.Message; Log(_saveError); }
    }
}
