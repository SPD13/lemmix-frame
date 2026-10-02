using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Render;
using Lemmix.Tests.Oracle;
using Lemmix.Tests.Ui;
using Lemmix.Ui;

namespace Lemmix.Tests.Render;

// oracle/skillbar.js: the skill bar (gui.js GuiPanel) through a fixed script of placements,
// ticks, hovers, switches and presses, for four switch combinations per level; after every step
// the texture, every object's visibility and transform, the hover state, what reached the panel
// and the page, and every relief geometry.
public class SkillBarTests
{
    const int W = 416, H = 40;
    static (double, double) UvAt(double px, double py) => (px / W, 1 - py / H);

    static void F64(StateHash h, double? v)
    {
        if (v == null) { h.Null(); return; }
        long bits = BitConverter.DoubleToInt64Bits(v.Value);
        h.Word(unchecked((uint)bits)); h.Word(unchecked((uint)(bits >> 32)));
    }

    static void Geometry(StateHash h, ChunkGeometry? g)
    {
        if (g == null) { h.Null(); return; }
        foreach (var a in new[] { g.Positions, g.Colors, g.Uvs })
        {
            if (a == null) { h.Null(); continue; }
            h.Bytes(MemoryMarshal.AsBytes(a.AsSpan()));
        }
        h.Word(g.Index32 ? 4 : 2);
        if (g.Index32) h.Bytes(MemoryMarshal.AsBytes(g.Indices.AsSpan()));
        else { var s = g.Indices.Select(i => (ushort)i).ToArray(); h.Bytes(MemoryMarshal.AsBytes(s.AsSpan())); }
    }

    static void Obj(StateHash h, BarObject? o)
    {
        if (o == null) { h.Null(); return; }
        h.Bool(o.Visible);
        foreach (double v in new[] { o.Px, o.Py, o.Pz, o.Sx, o.Sy, o.Sz }) F64(h, v);
    }

    // a JS number as String(n) gives it (the minimap points are whole or halves)
    static string Num(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("R", CultureInfo.InvariantCulture);

    sealed class Rec
    {
        readonly SkillBar _gui; readonly Game _game;
        public readonly List<(string, string)> Steps = new();
        public readonly List<string> Log = new();
        public Rec(SkillBar gui, Game game)
        {
            _gui = gui; _game = game;
            gui.Display.OnMouseDown.On(p => Log.Add("d " + Num(p.X) + " " + Num(p.Y) + " " + p.Button));
            gui.Display.OnMouseUp.On(p => Log.Add("u " + Num(p.X) + " " + Num(p.Y)));
            gui.Display.OnDoubleClick.On(p => Log.Add("c " + Num(p.X) + " " + Num(p.Y)));
            gui.OnMinimapCenter = (x, y) => Log.Add("m " + Num(x) + " " + Num(y));
        }

        string Hash()
        {
            var gui = _gui; var h = new StateHash();
            h.Bytes(gui.Canvas);
            Obj(h, gui.Mesh); Obj(h, gui.HoverTile); Obj(h, gui.Socket); Obj(h, gui.HoverRelief);
            Obj(h, gui.TextMesh); Obj(h, gui.MinimapPlane);
            if (gui.Mesh != null) { F64(h, gui.HoverRepeatX); F64(h, gui.HoverRepeatY); F64(h, gui.HoverOffsetX); F64(h, gui.HoverOffsetY); }
            else h.Null();
            if (gui.TileReliefs != null)
            {
                h.Word(gui.TileReliefs.Count);
                for (int i = 0; i < gui.TileReliefs.Count; i++) { h.Word(gui.ReliefParts![i].Index); h.Str(gui.ReliefParts[i].Half); Obj(h, gui.TileReliefs[i]); }
            }
            else h.Null();
            h.Int(gui.HoverIndex); h.Str(gui.HoverHalf);
            var tip = gui.HoverTip();
            h.Str(tip?.Text); F64(h, tip?.Since);
            h.Bool(gui.ReliefOn); F64(h, gui.ReliefDepth); h.Bool(gui.FlatSkills); h.Bool(gui.MinimapDrag);
            h.Word(Log.Count); foreach (string s in Log) h.Str(s);
            Log.Clear();
            var g = _game; var sim = g.Sim;
            h.Str(sim.SelectedSkill); h.Word(sim.SelectDx); h.Bool(g.NukePrepared); h.Bool(g.GameTimer.IsRunning());
            h.Word((int)g.GameTimer.SpeedFactor); h.Word(sim.CurrentIteration); h.Str(g.CommandManager.Serialize());
            string transforms = h.Hex();
            var gh = new StateHash();
            if (gui.TileReliefs != null) foreach (var m in gui.TileReliefs) Geometry(gh, m.Geometry);
            else gh.Null();
            Geometry(gh, gui.HoverRelief?.Geometry);
            Geometry(gh, gui.TextMesh?.Geometry);
            return transforms + ":" + gh.Hex();
        }

        public void Record(string label) => Steps.Add((label, Hash()));
    }

    static string Labels(SkillBar gui)
    {
        var h = new StateHash();
        for (int i = -1; i <= 20; i++) foreach (string? half in new[] { null, "upper", "lower" }) h.Str(gui.ButtonLabel(i < 0 ? null : i, half));
        F64(h, gui.RaisedTileBottomOffset());
        return h.Hex();
    }

    static string P(double v) => Num(v);

    // oracle/skillbar.js script(), step for step
    static List<(string, string)> Script(SkillBar gui, Game game, bool relief, bool flat, Func<double> getClock, Action<double> setClock)
    {
        var r = new Rec(gui, game);
        var timer = game.GameTimer;
        r.Record("constructed");
        gui.Update(); r.Record("first update");
        gui.Place(0.6 * 1.3, -0.3, -0.75); gui.SetReliefDepth(1); gui.Update(); r.Record("vr placed");
        game.Start(); for (int i = 0; i < 40; i++) timer.Tick(); gui.Update(); r.Record("40 ticks");
        var points = new List<(double, double)>();
        for (int c = 0; c < 19; c++) points.Add((c * 16 + 8, 30));
        foreach (int c in new[] { 16, 17, 18 }) foreach (double y in new[] { 16.5, 18, 26.9, 27, 27.5, 28, 39.9 }) points.Add((c * 16 + 3, y));
        points.AddRange(new (double, double)[] { (100, 8), (40, 16), (350, 20), (415.9, 39.9), (0.2, 20), (303.99, 20), (304, 20) });
        foreach (var (px, py) in points) { setClock(getClock() + 7); gui.SetHover(UvAt(px, py)); gui.Update(); r.Record("hover " + P(px) + "," + P(py)); }
        gui.SetHover(null); gui.Update(); r.Record("hover off");
        gui.SetHover(UvAt(17 * 16 + 8, 35)); gui.SetRelief(!relief); gui.Update(); r.Record("relief flipped while hovering");
        gui.SetRelief(relief); gui.SetFlatSkills(!flat); gui.Update(); r.Record("flat flipped while hovering");
        gui.SetFlatSkills(flat); gui.Update(); r.Record("flat back");
        gui.SetReliefDepth(3); gui.Update(); r.Record("depth 3");
        gui.Place(500, -180, -600); gui.Update(); r.Record("monitor placed");
        foreach (var (px, py, b) in new (double, double, int)[] { (2 * 16 + 5, 30, 0), (3 * 16 + 5, 30, 0), (17 * 16 + 5, 20, 0), (17 * 16 + 5, 27, 2), (17 * 16 + 5, 33, 0),
            (17 * 16 + 5, 33, 0), (16 * 16 + 5, 33, 2), (16 * 16 + 5, 20, 1), (12 * 16 + 5, 30, 0), (100, 8, 0), (13 * 16 + 4, 20, 0) })
        {
            gui.OnMouseDown(UvAt(px, py), b); gui.OnMouseUp(UvAt(px, py)); gui.Update(); r.Record("press " + P(px) + "," + P(py) + " b" + b);
        }
        gui.OnMouseUp(null); gui.Update(); r.Record("up null");
        gui.OnDoubleClick(UvAt(13 * 16 + 4, 20)); gui.Update(); r.Record("double click nuke");
        gui.SetViewRect(new LevelRect(10, 5, 330, 165));
        gui.OnMouseDown(UvAt(340, 20), 0); gui.Update(); r.Record("map down");
        gui.OnMouseMove(UvAt(360.5, 25)); gui.Update(); r.Record("map move");
        gui.OnMouseMove(UvAt(200, 25)); gui.Update(); r.Record("map move off");
        gui.OnMouseMove(UvAt(360, 25)); gui.OnMouseUp(UvAt(360, 25)); gui.Update(); r.Record("map up");
        gui.OnMouseDown(UvAt(330, 30), 2); gui.OnMouseMove(null); gui.OnMouseUp(UvAt(330, 30)); gui.Update(); r.Record("map down, null move");
        gui.OnDoubleClick(UvAt(330, 30)); r.Record("map double click");
        r.Log.Add("isMinimap " + (gui.IsMinimap(UvAt(330, 30)) ? "true" : "false") + " " + (gui.IsMinimap(UvAt(30, 30)) ? "true" : "false") + " " + (gui.IsMinimap(null) ? "true" : "false"));
        gui.SetHover(UvAt(3 * 16 + 8, 30));
        timer.Continue(); for (int i = 0; i < 30; i++) timer.Tick(); gui.Update(); r.Record("30 more, hovering");
        gui.SetHover(null); gui.Update(); r.Record("end");
        return r.Steps;
    }

    [Fact]
    public void SkillBarMatchesTheWebPage()
    {
        using var doc = OracleData.Load("skillbar.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var rows = doc!.RootElement.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var factory = new UiGameFactory();
        var failures = new ConcurrentBag<string>();
        int steps = 0;
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            bool relief = row.GetProperty("relief").GetBoolean(), flat = row.GetProperty("flat").GetBoolean();
            string what = $"{id} (relief {relief}, flat {flat})";
            try
            {
                var (game, sprites) = factory.Create(styles, id, row.GetProperty("url").GetString()!);
                string? packDir = row.GetProperty("packDir").ValueKind == JsonValueKind.Null ? null : row.GetProperty("packDir").GetString();
                double clock = 0;
                var gui = new SkillBar(game, PanelAssets.Load(OracleData.Io, packDir), sprites, () => clock);
                gui.SetRelief(relief);
                gui.SetFlatSkills(flat);
                if (Labels(gui) != row.GetProperty("labels").GetString()) failures.Add($"{what}: labels differ");
                var got = Script(gui, game, relief, flat, () => clock, t => clock = t);
                gui.Dispose();
                var want = row.GetProperty("steps").EnumerateArray().Select(s => (s[0].GetString()!, s[1].GetString()!)).ToList();
                Interlocked.Add(ref steps, want.Count);
                for (int i = 0; i < Math.Min(got.Count, want.Count); i++)
                {
                    if (got[i].Item1 != want[i].Item1) { failures.Add($"{what}: step {i} is '{got[i].Item1}', web '{want[i].Item1}'"); return styles; }
                    if (got[i].Item2 != want[i].Item2)
                    {
                        var g = got[i].Item2.Split(':'); var w = want[i].Item2.Split(':');
                        failures.Add($"{what}: step {i} '{want[i].Item1}' differs ({(g[0] != w[0] ? "state" : "")}{(g[1] != w[1] ? " geometry" : "")})");
                        return styles;
                    }
                }
                if (got.Count != want.Count) failures.Add($"{what}: {got.Count} steps, web {want.Count}");
            }
            catch (Exception e) { failures.Add($"{what}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            return styles;
        }, _ => { });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{list.Count}/{rows.Count} runs differ ({steps} steps):\n" + string.Join("\n", list.Take(40)));
    }
}
