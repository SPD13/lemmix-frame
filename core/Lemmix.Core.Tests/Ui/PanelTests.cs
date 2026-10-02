using System.Collections.Concurrent;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Tests.Oracle;
using Lemmix.Ui;

namespace Lemmix.Tests.Ui;

// The Game the 3D page builds for a level (app.js lemmixEngine.createGame): the sprite set its
// theme names, the pickup pictures painted from it.
public sealed class UiGameFactory
{
    readonly ConcurrentDictionary<string, SpriteSet> _sprites = new();
    public readonly Masks Masks = Masks.Load(OracleData.Io);

    public (Game Game, SpriteSet Sprites) Create(StyleManager styles, string id, string url)
    {
        var level = LevelBuilder.Build(LevelBuilder.ParseLevel(OracleData.Io.Text(url)!), styles, id);
        string setName = level.Theme.Lemmings is { Length: > 0 } s ? s : "default";
        var sprites = _sprites.GetOrAdd(setName, n => new SpriteSet(OracleData.Io).Load(n));
        var game = new Game(level, Masks, l => SpriteSet.GeneratePickupIcons(l, sprites, l.Theme));
        return (game, sprites);
    }
}

// oracle/panel.js: the skill panel through a fixed script of states and presses, on a sample of
// levels; after every step the panel's pixels and layout, and the game around it.
public class PanelTests
{
    const int Cell = 16;
    static readonly int[] PressYs = { 20, 27, 33 };

    sealed class Run
    {
        public readonly Game Game; public readonly GamePanel Gui; public readonly PixelCanvas Display; public readonly SpriteSet Sprites;
        public int LoadRequests;
        public readonly List<(string Label, string Hash)> Steps = new();

        public Run(Game game, GamePanel gui, PixelCanvas display, SpriteSet sprites)
        {
            Game = game; Gui = gui; Display = display; Sprites = sprites;
            game.OnLoadReplayRequest = () => LoadRequests++;
        }

        string PanelHash()
        {
            var h = new StateHash();
            h.Word(Display.Width); h.Word(Display.Height); h.Bytes(Display.Data!);
            var L = Gui.Layout;
            h.Word(L.Buttons); h.Word(L.DigitButtons); h.Word(L.Width); h.Word(L.Height); h.Bool(L.SharedBorder); h.Bool(L.ReliefFromMasks);
            h.Word(L.Cells.Count); foreach (string c in L.Cells) h.Str(c);
            var m = L.Minimap;
            h.Word(m.X); h.Word(m.Y); h.Word(m.W); h.Word(m.H); h.Word(m.ScaleX); h.Word(m.ScaleY); h.Int(m.Pad);
            h.Word(L.SplitCells.Count); foreach (int c in L.SplitCells) h.Word(c);
            h.Word(L.HalfUpperBottom); h.Word(L.HalfLowerTop);
            if (L.ReliefMasks == null) h.Null();
            else { h.Bytes(L.ReliefMasks.Art); h.Bytes(L.ReliefMasks.Digits); }
            return h.Hex();
        }

        string GameHash()
        {
            var g = Game; var sim = g.Sim; var timer = g.GameTimer;
            var h = SimState.HashFrame(sim, false);
            h.Str(sim.SelectedSkill); h.Word(sim.SelectDx); h.Bool(g.ClearPhysics); h.Bool(g.NukePrepared);
            h.Bool(timer.IsRunning()); h.Word((int)timer.SpeedFactor); h.Word(timer.TickIndex);
            if (g.Mode != null) { h.Str(g.Mode.Kind); h.Word(g.Mode.CutVersion); h.Word(g.Mode.RecordVersion); }
            else h.Null();
            h.Bool(sim.ReplayInsert); h.Bool(sim.Replaying);
            h.Str(g.CommandManager.Serialize());
            h.Word(Gui.RrHeld);
            if (Gui.Held != null) { h.Word(Gui.Held.Step); h.Word((int)Gui.Held.Next); } else h.Null();
            h.Word(LoadRequests);
            h.Word(sim.Recorded.Count);
            foreach (var r in sim.Recorded) { h.Str(r.Type); h.Word(r.Frame); h.Str(string.IsNullOrEmpty(r.Skill) ? null : r.Skill); h.Word(r.Type == "assignment" ? r.LemIndex : -1); }
            return h.Hex();
        }

        string LemmingsHash()
        {
            var h = new StateHash();
            foreach (var L in Game.Sim.Lemmings)
            {
                h.Word(L.Index);
                Sprites.RenderLemming(Game, L, (f, x, y) => { UiHash.Frame(h, f); h.Word(x); h.Word(y); });
            }
            return h.Hex();
        }

        public void Record(string label, bool withLemmings = false) =>
            Steps.Add((label, PanelHash() + ":" + GameHash() + (withLemmings ? ":" + LemmingsHash() : "")));

        public void Ticks(int n) { for (int i = 0; i < n; i++) Game.GameTimer.Tick(); }
        public void Down(double x, double y, int button) => Display.OnMouseDown.Trigger(new PanelPointer(x, y, button));
        public void Up() => Display.OnMouseUp.Trigger(new PanelPointer(0, 0));
        public Lemming? FirstLive() => Game.Sim.Lemmings.FirstOrDefault(L => !L.Removed);
    }

    // oracle/panel.js script(), step for step
    static List<(string, string)> Script(Game game, GamePanel gui, PixelCanvas display, SpriteSet sprites, bool flat, Action<double> setClock)
    {
        var r = new Run(game, gui, display, sprites);
        r.Record("start", true);
        gui.SetFlatBackground(true); r.Record("flat on");
        gui.SetFlatBackground(false); r.Record("flat off");
        gui.SetFlatBackground(flat);
        game.Start(); r.Ticks(30); r.Record("30 frames", true);
        for (int i = 0; i < 400 && r.FirstLive() == null; i++) r.Ticks(1);
        r.Ticks(10); r.Record("lemming out", true);
        int nSkills = game.Sim.ActiveSkills.Count;
        r.Down(Cell * (2 + Math.Min(1, Math.Max(0, nSkills - 1))) + 3, 20, 0); r.Up(); r.Record("skill cell");
        var L = r.FirstLive();
        game.CursorLemming = L; gui.Render(true); r.Record("cursor lemming", true);
        if (L != null)
        {
            L.IsClimber = true; gui.Render(true); r.Record("climber", true);
            L.IsFloater = true; gui.Render(true); r.Record("athlete", true);
            L.IsSwimmer = true; L.IsDisarmer = true; gui.Render(true); r.Record("quadathlete", true);
            game.ShowAthleteInfo = true; gui.Render(true); r.Record("athlete info");
            game.ShowAthleteInfo = false;
        }
        if (L != null) game.QueueCommand(new CommandLemmingsAction(L.Id));
        r.Ticks(25); r.Record("assigned + 25", true);
        void Press(double x, double y, int button, string label) { r.Down(x, y, button); r.Up(); r.Record(label); }
        int CellX(string what) => gui.Cells.IndexOf(what) * Cell + 7;
        Press(CellX("pause"), 20, 0, "pause");
        Press(CellX("pause"), 20, 0, "unpause");
        for (int i = 0; i < 4; i++) Press(CellX("speed"), 25, 0, "speed " + i);
        Press(CellX("directional"), 20, 0, "dir left");
        Press(CellX("directional"), 33, 0, "dir right");
        Press(CellX("directional"), 27, 0, "dir line");
        Press(CellX("directional"), 33, 0, "dir off");
        Press(CellX("cpmreplay"), 18, 0, "clear physics"); r.Record("clear physics lemmings", true);
        Press(CellX("cpmreplay"), 36, 0, "load replay");
        Press(CellX("cpmreplay"), 26, 0, "clear physics off");
        r.Down(CellX("rrplus"), 30, 0); r.Ticks(3); r.Record("rr+ held"); r.Up(); r.Record("rr+ up");
        r.Down(CellX("rrminus"), 30, 2); r.Ticks(2); r.Up(); r.Record("rr- held");
        Press(CellX("restart"), 20, 0, "restart");
        game.ToggleReplayInsert(); r.Record("insert mode");
        game.ToggleReplayInsert();
        r.Ticks(5); r.Record("replaying", true);
        setClock(1000);
        r.Down(CellX("frameskip"), 33, 0); r.Record("forward 1");
        foreach (int t in new[] { 1100, 1249, 1250, 1300, 1350, 1449, 1450 }) { setClock(t); gui.Poll(t); r.Record("poll " + t); }
        r.Up(); setClock(1700); gui.Poll(1700); r.Record("released");
        r.Down(CellX("frameskip"), 20, 0); setClock(1950); gui.Poll(1950); r.Up(); r.Record("back 1 held");
        Press(CellX("frameskip"), 20, 2, "back 17");
        Press(CellX("frameskip"), 33, 1, "forward 85");
        Press(CellX("frameskip"), 20, 1, "back 85");
        Press(CellX("frameskip"), 27, 0, "frameskip line");
        Press(CellX("frameskip"), 33, 2, "forward 17");
        Press(CellX("frameskip"), 33, 4, "forward other button");
        for (int c = 0; c < gui.Cells.Count; c++)
        {
            if (gui.Cells[c] == "nuke" || gui.Cells[c] == "speed") continue;
            foreach (int y in PressYs)
                foreach (int b in new[] { 0, 1, 2 })
                    Press(c * Cell + 1 + ((y + b) % 14), y, b, "cell " + c + " y" + y + " b" + b);
            r.Ticks(1);
        }
        Press(5, 10, 0, "strip");
        Press(gui.Cells.Count * Cell + 20, 20, 0, "minimap");
        Press(-5, 20, 0, "x -5");
        Press(-20, 20, 0, "x -20");
        Press(Cell * 2 + 0.5, 15.9, 0, "y 15.9");
        r.Ticks(10); r.Record("10 more", true);
        Press(CellX("nuke"), 20, 0, "nuke armed");
        display.OnDoubleClick.Trigger(new PanelPointer(CellX("nuke"), 22)); r.Record("nuke double click");
        r.Ticks(20); r.Record("nuked", true);
        gui.Dispose();
        Press(CellX("pause"), 20, 0, "disposed pause");
        return r.Steps;
    }

    [Fact]
    public void PanelMatchesTheWebPage()
    {
        using var doc = OracleData.Load("panel.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var rows = doc!.RootElement.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var factory = new UiGameFactory();
        var failures = new ConcurrentBag<string>();
        int steps = 0;
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try
            {
                var (game, sprites) = factory.Create(styles, id, row.GetProperty("url").GetString()!);
                string? packDir = row.GetProperty("packDir").ValueKind == JsonValueKind.Null ? null : row.GetProperty("packDir").GetString();
                double clock = 0;
                var display = new PixelCanvas();
                var gui = GamePanel.SetGuiDisplay(game, display, PanelAssets.Load(OracleData.Io, packDir), sprites, () => clock);
                var got = Script(game, gui, display, sprites, row.GetProperty("flat").GetBoolean(), t => clock = t);
                var want = row.GetProperty("steps").EnumerateArray().Select(s => (s[0].GetString()!, s[1].GetString()!)).ToList();
                Interlocked.Add(ref steps, want.Count);
                for (int i = 0; i < Math.Min(got.Count, want.Count); i++)
                {
                    if (got[i].Item1 != want[i].Item1) { failures.Add($"{id}: step {i} is '{got[i].Item1}', web '{want[i].Item1}'"); return styles; }
                    if (got[i].Item2 != want[i].Item2)
                    {
                        var g = got[i].Item2.Split(':'); var w = want[i].Item2.Split(':');
                        string what = string.Join(",", new[] { "panel", "game", "lemmings" }.Where((n, k) => k < g.Length && k < w.Length && g[k] != w[k]));
                        failures.Add($"{id}: step {i} '{want[i].Item1}' differs ({what})");
                        return styles;
                    }
                }
                if (got.Count != want.Count) failures.Add($"{id}: {got.Count} steps, web {want.Count}");
            }
            catch (Exception e) { failures.Add($"{id}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            return styles;
        }, _ => { });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{list.Count}/{rows.Count} levels differ ({steps} steps):\n" + string.Join("\n", list.Take(40)));
    }
}
