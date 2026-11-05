using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;
using StarMap.API;
using WobblyWindows.Runtime;

namespace WobblyWindows;

[StarMapMod]
public sealed class Mod
{
    private Harmony? _harmony;
    private string _directory = Path.GetDirectoryName(typeof(Mod).Assembly.Location)!;
    private static WobbleRuntime? _runtime;

    [StarMapImmediateLoad]
    public void ImmediateLoad(KSA.Mod mod) => _directory = mod.DirectoryPath;

    [StarMapAllModsLoaded]
    public void AllModsLoaded()
    {
        if (_runtime != null) return;
        try
        {
            _runtime = new WobbleRuntime(_directory);
            _harmony = new Harmony("meow-sci.wobbly-windows");
            Patch(typeof(Program), "PrepareImGui", nameof(PrepareFrame), true, [typeof(double)]);
            Patch(typeof(Program), nameof(Program.DrawProgramMenusHook), nameof(Menu), false, []);
            Patch(typeof(ImGuiBackend), nameof(ImGuiBackend.Shutdown), nameof(BeforeBackendShutdown), true, []);
            Patch(typeof(GameSettings), nameof(GameSettings.OnKeyAll), nameof(GuardTextInput), true);
            WobbleRuntime.Log("Loaded. Open the Wobbly Windows menu for settings and a sample window.");
        }
        catch (Exception error)
        {
            WobbleRuntime.Log("Initialization failed: " + error);
            _runtime?.Dispose(); _runtime = null;
            _harmony?.UnpatchAll(_harmony.Id); _harmony = null;
        }
    }

    private void Patch(Type type, string name, string patch, bool prefix, Type[]? arguments = null)
    {
        var original = AccessTools.Method(type, name, arguments)
            ?? throw new MissingMethodException(type.FullName, name);
        var hook = new HarmonyMethod(typeof(Mod), patch);
        _harmony!.Patch(original, prefix: prefix ? hook : null, postfix: prefix ? null : hook);
    }

    private static void PrepareFrame() => _runtime?.PrepareMainFrame();
    private static void Menu()
    {
        try { _runtime?.DrawMenu(); }
        catch (Exception error) { _runtime?.Fail(error); }
    }
    private static void BeforeBackendShutdown()
    {
        try { _runtime?.SaveSettings(); _runtime?.Dispose(); }
        catch (Exception error) { WobbleRuntime.Log("Shutdown cleanup failed: " + error); }
    }
    private static bool GuardTextInput(ref bool __result)
    {
        // Preserve the normal console path and only consume keys while a UI text editor is active.
        if (_runtime?.CanReadInput == true && !(Program.ConsoleWindow?.IsOpen ?? false) && ImGui.GetIO().WantTextInput)
        { __result = true; return false; }
        return true;
    }

    [StarMapAfterGui]
    public void AfterGui(double dt)
    {
        try { _runtime?.DrawUi(); }
        catch (Exception error) { _runtime?.Fail(error); }
    }

    [StarMapUnload]
    public void Unload()
    {
        try { _runtime?.SaveSettings(); _runtime?.Dispose(); }
        finally
        {
            _harmony?.UnpatchAll(_harmony.Id);
            _harmony = null; _runtime = null;
        }
    }
}
