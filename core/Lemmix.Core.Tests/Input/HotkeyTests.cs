using System.Text.Json;
using Lemmix.Input;
using Lemmix.Store;
using Lemmix.Tests.Store;

namespace Lemmix.Tests.Input;

// oracle/settings.js: hotkeys.js's HotkeyManager over the store (the default table written at
// once, the layouts, set/clear, export, import of every kind of file, tables saved by older or
// edited files), its pure functions over every function x detail, and its tables; plus app.js's
// reading of the table while playing (HotkeyDispatch), which lives in a closure the oracle
// cannot run, checked against app.js line by line.
public class HotkeyTests
{
    static SettingsReplay Replay()
    {
        var r = SettingsReplay.Instance;
        Assert.SkipWhen(r == null, "no oracle output (oracle/out/settings.json.gz) or assets");
        return r!;
    }

    static void Scenarios(params string[] names)
    {
        var r = Replay();
        foreach (var n in names) Assert.True(r.StepsRun.ContainsKey(n), "scenario " + n + " not in the oracle output");
        var fs = r.FailuresOf(names);
        Assert.True(fs.Count == 0, SettingsReplay.Report(fs));
    }

    [Fact] public void FreshTableLayoutsAndEditsAsTheWeb() => Scenarios("hotkeys-fresh");
    [Fact] public void StoredTablesLoadAsTheWeb() => Scenarios("hotkeys-load");
    [Fact] public void ImportsAsTheWeb() => Scenarios("hotkeys-import", "hotkeys-roundtrip", "hotkeys-roundtrip-back");
    [Fact] public void RandomEditsAsTheWeb() => Scenarios("hotkeys-random");

    static JsonElement Fn(string name) => Replay().Doc.RootElement.GetProperty("hotkeys").GetProperty(name);

    static HotkeyBinding? BindingOf(JsonElement e)
    {
        var v = JsJson.Parse(e.GetRawText());
        if (v is not JsObject o) return null;
        return new HotkeyBinding((string)o.Get("action")!, o.Get("mod"));
    }

    [Fact]
    public void DescribeAndTagAsTheWeb()
    {
        var bad = new List<string>();
        foreach (var e in Fn("describe").EnumerateArray())
        {
            string want = e[1].GetString()!, got = JsJson.Stringify(Hotkeys.Describe(BindingOf(e[0])))!;
            if (got != want) bad.Add($"describe {e[0].GetRawText()}: web {want} port {got}");
        }
        foreach (var e in Fn("tagOf").EnumerateArray())
        {
            string want = e[1].GetString()!, got = Hotkeys.TagOf(BindingOf(e[0]));
            if (got != want) bad.Add($"tagOf {e[0].GetRawText()}: web {want} port {got}");
        }
        foreach (var e in Fn("allowedOn").EnumerateArray())
        {
            bool want = e[2].GetBoolean(), got = Hotkeys.AllowedOn(e[0].GetString()!, e[1].GetString());
            if (got != want) bad.Add($"allowedOn {e[0]} {e[1]}: web {want} port {got}");
        }
        foreach (var e in Fn("normalizeCode").EnumerateArray())
        {
            string want = e[1].GetString()!, got = Hotkeys.NormalizeCode(e[0].ValueKind == JsonValueKind.Null ? null : e[0].GetString());
            if (got != want) bad.Add($"normalizeCode {e[0]}: web {want} port {got}");
        }
        foreach (var e in Fn("keyName").EnumerateArray())
        {
            string want = e[1].GetString()!, got = Hotkeys.KeyName(e[0].GetString()!);
            if (got != want) bad.Add($"keyName {e[0]}: web {want} port {got}");
        }
        Assert.True(bad.Count == 0, bad.Count + " differ:\n" + string.Join("\n", bad.Take(30)));
    }

    [Fact]
    public void TablesAreTheWebs()
    {
        var t = JsonDocument.Parse(Fn("tables").GetString()!).RootElement;
        var actions = t.GetProperty("ACTIONS").EnumerateArray().ToList();
        Assert.Equal(actions.Count, Hotkeys.Actions.Length);
        string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetString() : null;
        bool Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.GetBoolean();
        for (int i = 0; i < actions.Count; i++)
        {
            var w = actions[i];
            var a = Hotkeys.Actions[i];
            Assert.Equal(new HotkeyAction(Str(w, "id")!, Str(w, "label")!, Str(w, "mod"), Bool(w, "held"), Bool(w, "repeat"), Str(w, "tag"), Str(w, "vr")), a);
        }
        Assert.Equal(t.GetProperty("KEYS").EnumerateArray().Select(k => k.GetProperty("code").GetString() + "=" + k.GetProperty("name").GetString()),
            Hotkeys.Keys.Select(k => k.Code + "=" + k.Name));
        Assert.Equal(t.GetProperty("VR_KEYS").EnumerateArray().Select(k => k.GetProperty("code").GetString() + "=" + k.GetProperty("name").GetString() + "=" + k.GetProperty("kind").GetString()),
            Hotkeys.VrKeys.Select(k => k.Code + "=" + k.Name + "=" + k.Kind));
        string Entries(IEnumerable<PresetEntry> es) => JsJson.Stringify(new JsArray(es.Select(e => (object?)(e.Mod == null ? new JsArray(new object?[] { e.Code, e.Action }) : new JsArray(new object?[] { e.Code, e.Action, e.Mod })))))!;
        Assert.Equal(t.GetProperty("VR_PRESET").GetRawText(), Entries(Hotkeys.VrPreset));
        var presets = t.GetProperty("PRESETS");
        Assert.Equal(presets.EnumerateObject().Select(p => p.Name), Hotkeys.Presets.Select(p => p.Key));
        foreach (var p in Hotkeys.Presets) Assert.Equal(presets.GetProperty(p.Key).GetRawText(), Entries(p.Value));
        Assert.Equal(t.GetProperty("SKILLS").EnumerateArray().Select(x => x.GetString()), Hotkeys.Skills);
        Assert.Equal(t.GetProperty("DOS_SKILLS").EnumerateArray().Select(x => x.GetString()), Hotkeys.DosSkills);
        Assert.Equal(t.GetProperty("SPECIAL_SKIPS").EnumerateArray().Select(x => x.GetString()), Hotkeys.SpecialSkips);
        Assert.Equal(t.GetProperty("DEFAULT_PRESET").GetString(), Hotkeys.DefaultPreset);
        Assert.Equal(t.GetProperty("EXPORT_FORMAT").GetString(), Hotkeys.ExportFormat);
        Assert.Equal(t.GetProperty("EXPORT_FILE").GetString(), Hotkeys.ExportFile);
    }

    // ---------------------------------------------------------------- app.js's dispatch

    static (HotkeyManager, HotkeyDispatch) Fresh()
    {
        var m = new HotkeyManager(new LocalStore());
        return (m, new HotkeyDispatch(m));
    }

    [Fact]
    public void KeyDownRunsOncePerPressUnlessItRepeats()
    {
        var (m, d) = Fresh();
        var r = d.KeyDown("KeyP", false); // traditional: pause
        Assert.Equal("pause", r.Binding!.Action);
        Assert.True(r.Run && r.Taken && !r.ViewKey && !r.CheckShifts);
        Assert.False(d.KeyDown("KeyP", true).Run);          // auto-repeat: not again
        Assert.True(d.KeyDown("Space", true).Run);          // skip 170 repeats
        Assert.True(d.KeyDown("PageUp", true).Run);         // zoom repeats
        var none = d.KeyDown("KeyG", false);                // nothing on G
        Assert.Null(none.Binding);
        Assert.True(none.ViewKey && !none.Taken);
        Assert.DoesNotContain("KeyG", d.HeldCodes);
        Assert.Equal(new[] { "KeyP", "Space", "PageUp" }, d.HeldCodes);
        m.Set("KeyG", "bogus");                             // a function app.js does not know: its handler fails
        var bogus = d.KeyDown("KeyG", false);
        Assert.True(bogus.Threw && !bogus.Run && !bogus.Taken);
        Assert.Contains("KeyG", d.HeldCodes);
    }

    [Fact]
    public void HeldKeysAreFilters()
    {
        var (m, d) = Fresh();
        var r = d.KeyDown("ArrowLeft", false); // dir_select_left, held
        Assert.True(r.CheckShifts && r.Taken && r.ViewKey && !r.Run); // the arrow still pans
        Assert.Equal(new HotkeyDispatch.Shifts(-1, false, false), d.CurrentShifts());
        d.KeyDown("ArrowRight", false);
        Assert.Equal(0, d.CurrentShifts().Dx);                // both cancel out
        d.KeyDown("ControlLeft", false);                      // folded to Control: force_walker
        d.KeyDown("AltRight", false);                         // athlete_info
        Assert.Equal(new HotkeyDispatch.Shifts(0, true, true), d.CurrentShifts());
        d.KeyUp("ArrowRight");
        Assert.Equal(-1, d.CurrentShifts().Dx);
        d.ReleaseHeldKeys();
        Assert.Equal(new HotkeyDispatch.Shifts(0, false, false), d.CurrentShifts());
        Assert.Empty(d.HeldCodes);
    }

    [Fact]
    public void KeyUpStopsTheHeldOnes()
    {
        var (m, d) = Fresh();
        d.KeyDown("F1", false);
        var up = d.KeyUp("F1");
        Assert.True(up.StopReleaseRate && !up.ClearPhysicsOff);
        Assert.False(d.KeyUp("F1", session: false).StopReleaseRate);
        Assert.True(d.KeyUp("KeyT").ClearPhysicsOff);         // clear_physics with hold (mod 1)
        m.Set("KeyT", "clear_physics", 0);
        Assert.False(d.KeyUp("KeyT").ClearPhysicsOff);        // the toggle kind
        Assert.True(HotkeyDispatch.ClearPhysicsHold(new HotkeyBinding("clear_physics", 1)));
    }

    [Fact]
    public void RunGateAsRunHotkey()
    {
        Assert.Equal(HotkeyDispatch.Gate.NotTaken, HotkeyDispatch.RunGate(new HotkeyBinding("dir_select_left", 0), true, true));
        Assert.Equal(HotkeyDispatch.Gate.NotTaken, HotkeyDispatch.RunGate(new HotkeyBinding("nope", 0), true, true));
        Assert.Equal(HotkeyDispatch.Gate.Run, HotkeyDispatch.RunGate(new HotkeyBinding("quit", 0), false, false));
        Assert.Equal(HotkeyDispatch.Gate.Run, HotkeyDispatch.RunGate(new HotkeyBinding("next_level", 0), false, false));
        Assert.Equal(HotkeyDispatch.Gate.NotTaken, HotkeyDispatch.RunGate(new HotkeyBinding("pause", 0), false, false));
        Assert.Equal(HotkeyDispatch.Gate.Swallowed, HotkeyDispatch.RunGate(new HotkeyBinding("skill", "jumper"), true, false));
        Assert.Equal(HotkeyDispatch.Gate.Run, HotkeyDispatch.RunGate(new HotkeyBinding("skill", "digger"), true, false));
        Assert.Equal(HotkeyDispatch.Gate.Swallowed, HotkeyDispatch.RunGate(new HotkeyBinding("skip", -17), true, false));
        Assert.Equal(HotkeyDispatch.Gate.Run, HotkeyDispatch.RunGate(new HotkeyBinding("skip", -17), true, true));
        Assert.Equal(HotkeyDispatch.Gate.Run, HotkeyDispatch.RunGate(new HotkeyBinding("reset_view", 0), true, false));
        Assert.Equal(-17, HotkeyDispatch.FramesOf(new HotkeyBinding("skip", "-17.9")));
        Assert.True(HotkeyDispatch.SpecialIsLastAction(new HotkeyBinding("special_skip", "x")));
        Assert.Equal("digger", HotkeyDispatch.SkillOf(new HotkeyBinding("skill", "digger")));
        Assert.Equal(10, HotkeyDispatch.SpeedUp(9.5));
        Assert.Equal(0.5, HotkeyDispatch.SpeedDown(0.75));
        Assert.Equal(-Math.PI / 12, HotkeyDispatch.Yaw("yaw_left", 0));
    }

    [Fact]
    public void NukeNeedsTwoPresses()
    {
        var (_, d) = Fresh();
        Assert.False(d.NukePress(1000));
        Assert.True(d.NukePress(1200));
        Assert.False(d.NukePress(1300));  // the pair was used up
        Assert.False(d.NukePress(1600));  // too late for 1300
        Assert.True(d.NukePress(1849.9));
    }

    [Fact]
    public void ControllersAsOnVrButtonAndOnStick()
    {
        var (m, d) = Fresh();
        var a = d.VrButtonDown("VrPointA", windowUp: false); // recenter_vr
        Assert.True(a.Run);
        Assert.True(d.VrButtonDown("VrPointA", windowUp: true).Run); // a recentre is the way out of anything
        Assert.False(d.VrButtonDown("VrFreeB", false).Run);  // a dolly: held, not pressed
        Assert.Equal(1, d.VrButtonHeld("VrFreeB"));
        Assert.Equal(-1, d.VrButtonHeld("VrFreeA"));
        Assert.Equal(0, d.VrButtonHeld("VrPointA"));
        Assert.Equal("vr_pan", d.Stick("VrPointStick"));
        Assert.Equal("vr_tilt", d.Stick("VrFreeStick"));
        m.Set("VrPointB", "pause");
        Assert.False(d.VrButtonDown("VrPointB", windowUp: true).Run); // a window owns the hands
        Assert.True(d.VrButtonDown("VrPointB", windowUp: false).Run);
        m.Set("VrFreeStickClick", "force_walker");
        Assert.True(d.VrButtonDown("VrFreeStickClick", false).CheckShifts);
        Assert.True(d.CurrentShifts().SelectWalkerOnly);
        d.VrButtonUp("VrFreeStickClick");
        Assert.False(d.CurrentShifts().SelectWalkerOnly);
        Assert.Null(d.Stick("VrPointA"));
        Assert.Null(d.VrButtonDown("VrPointStickClick", false).Binding);
    }

    [Fact]
    public void ImportStatusAsTheDialog()
    {
        Assert.Equal("c.json: 1 binding loaded, 2 skipped (unknown key or function), keyboard and VR left at the default",
            Hotkeys.ImportStatus(new HotkeyImport(1, 2, new List<string> { "keyboard", "VR" }), "c.json"));
        Assert.Equal("3 bindings loaded", Hotkeys.ImportStatus(new HotkeyImport(3, 0, new List<string>()), null));
    }
}
