using Godot;
using Lemmix.Store;

namespace Lemmix.App.Xr;

// Foveated rendering on the headset (a native setting, the VR window): on or off, and its strength
// - OpenXR's foveation level 1 low, 2 medium, 3 high (XR_FB_foveation on Vulkan; eye-tracked where
// the runtime offers XR_META_foveation_eye_tracked, project setting xr/openxr/foveation_eye_tracked).
// On at medium by default (device session 1). Kept in the store, applied at start and on change.
public sealed class Foveation
{
    public const string OnKey = "lemmix-frame-foveation", LevelKey = "lemmix-frame-foveation-level";
    public const int DefaultLevel = 2;
    static readonly string[] Names = { "off", "low", "medium", "high" };

    readonly IStorage _store;
    public bool On { get; private set; }
    public int Level { get; private set; }

    public Foveation(IStorage store)
    {
        _store = store;
        On = store.GetItem(OnKey) != "off";
        Level = int.TryParse(store.GetItem(LevelKey), out int l) && l >= 1 && l <= 3 ? l : DefaultLevel;
    }

    public string LevelName => Names[Level];

    /** The level asked of the runtime: 0 when off. */
    public int EffectiveLevel => On ? Level : 0;

    public void Toggle()
    {
        On = !On;
        _store.SetItem(OnKey, On ? "on" : "off");
        Apply();
    }

    /** The strength: low, medium, high, low again (turned on if it was off). */
    public void CycleLevel()
    {
        Level = Level % 3 + 1;
        _store.SetItem(LevelKey, Level.ToString());
        if (!On) { On = true; _store.SetItem(OnKey, "on"); }
        Apply();
    }

    /** Into the OpenXR interface, when there is one running. */
    public void Apply()
    {
        if (XRServer.FindInterface("OpenXR") is not OpenXRInterface xr || !xr.IsInitialized()) return;
        xr.FoveationDynamic = false;
        xr.FoveationLevel = EffectiveLevel;
        GD.Print($"[xr] foveation {(On ? LevelName : "off")} (level {xr.FoveationLevel})");
    }
}
