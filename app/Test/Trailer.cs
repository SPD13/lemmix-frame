using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.App.Board;
using Lemmix.App.Xr;
using Lemmix.Store;
using ShellApp = Lemmix.App.Shell.App;

namespace Lemmix.App.Test;

// Footage for the trailer: `-- --trailer <clip.json>` under Godot's Movie Maker (`--write-movie
// <dir>/f.png --fixed-fps 30`, tools/trailer/record.sh). The shell as a player has it, a level's
// stored solution playing from `start` (a game frame) at `speed` for `seconds`, the REPLAY badge
// and the replay's markers hidden (`markers: true` keeps them).
//
// The head is a director's camera: it stands where the player does (`x, y, z` metres off the
// standing pose) and looks at a target on the board - by default where the solution acts next
// (its next skill within `lead` game frames), else the middle of the lemmings out, eased so it
// glides (`tau` seconds); a key's `tx, ty` (level pixels) fixes the target instead. `near` (0..1)
// walks the head that far towards the target, for a close look with the board's depth; `fov` is
// the lens. Keys `cam: [{t, x, y, z, near, fov, tx?, ty?}]` are eased between. `hands: true`
// shows the controllers, the right one's beam on the target; `ui: false` hides the bar and the strip;
// the level's music is off (`music: true` keeps it), the game's sounds stay.
// Prints "[trailer] rec <frame>" when the shot starts and "[trailer] done <frame>" when it ends
// (record.sh cuts the warm-up off by those). The clock is the movie's (1/fps a frame), so the game
// runs at its own pace whatever the frames cost to draw.
public partial class Trailer : Node3D
{
    sealed class Key { public double T, X, Y, Z, Near, Fov = 55; public double? Tx, Ty; }

    ShellApp? _app;
    readonly ScriptedXrInput _input = new();
    readonly Camera3D _head = new() { Name = "head" };
    double _ms;                      // the movie clock
    int _frame;                      // frames drawn (the movie's file number)
    double _recAt = -1;              // when the shot started (movie ms)
    int _settle;
    string _level = "", _scene = "level";
    int _start, _lead = 50;
    double _speed = 1, _seconds = 10, _tau = 0.9;
    bool _hands, _markers, _ui = true;
    Vector3? _aim;                   // the eased target (world)
    readonly List<Key> _cam = new();

    static double Num(Godot.Collections.Dictionary d, string k, double def) =>
        d.ContainsKey(k) ? d[k].AsDouble() : def;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().SkipWhile(a => a != "--trailer").Skip(1).ToList();
        var spec = Json.ParseString(System.IO.File.ReadAllText(args[0])).AsGodotDictionary();
        _scene = spec.ContainsKey("scene") ? spec["scene"].AsString() : "level";
        _level = spec.ContainsKey("level") ? spec["level"].AsString() : "";
        _start = (int)Num(spec, "start", 0);
        _speed = Num(spec, "speed", 1);
        _seconds = Num(spec, "seconds", 10);
        _lead = (int)Num(spec, "lead", 50);
        _tau = Num(spec, "tau", 0.9);
        _hands = spec.ContainsKey("hands") && spec["hands"].AsBool();
        _markers = spec.ContainsKey("markers") && spec["markers"].AsBool();
        _ui = !spec.ContainsKey("ui") || spec["ui"].AsBool();
        ProcessPriority = 1000; // after the shell's frame, so what it shows can be hidden again
        if (spec.ContainsKey("cam"))
            foreach (var k in spec["cam"].AsGodotArray())
            {
                var d = k.AsGodotDictionary();
                _cam.Add(new Key
                {
                    T = Num(d, "t", 0), X = Num(d, "x", 0), Y = Num(d, "y", 0), Z = Num(d, "z", 0),
                    Near = Num(d, "near", 0), Fov = Num(d, "fov", 55),
                    Tx = d.ContainsKey("tx") ? d["tx"].AsDouble() : null, Ty = d.ContainsKey("ty") ? d["ty"].AsDouble() : null,
                });
            }
        if (_cam.Count == 0) _cam.Add(new Key());

        foreach (var h in _input.HandsValue) h.Connected = _hands;
        _input.HeadValue = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(-14)), new Vector3(0, 1.6f, 0));
        string env = spec.ContainsKey("environment") ? spec["environment"].AsString() : "full";
        // the level's music off unless asked for: the trailer lays its own under the cuts
        string music = "--music=" + (spec.ContainsKey("music") && spec["music"].AsBool() ? "1" : "0");
        _app = new ShellApp(new Shell.AppOptions
        {
            Args = Shell.ShellArgs.Parse(_scene == "level"
                ? new[] { "--level=" + _level, "--solution", "--environment=" + env, music }
                : new[] { "--environment=" + env, music }),
            Input = _input, Store = new LocalStore(), Head = _head, Clock = () => _ms,
            UserDataDir = OS.GetUserDataDir(), AssetRoot = TerrainShot.Assets,
        });
        if (_scene == "catalog") _app.Ready += () => _app.Library.Navigate(spec.ContainsKey("folder") ? spec["folder"].AsString() : "Lemmings_Redux/Gentle");
        AddChild(_app);
        _head.Current = true;
        RenderingServer.FramePreDraw += HideReplayLook;
    }

    public override void _ExitTree() => RenderingServer.FramePreDraw -= HideReplayLook;

    // what says "a replay" on screen: the badge over the strip and the markers of the moves to come;
    // with `ui: false` the bar and the strip too
    void HideReplayLook()
    {
        if (_app?.Session is not { } s) return;
        _app.Windows.Status.Badge.Panel.Visible = false;
        if (!_markers) { s.Board.Markers.Hidden = true; s.Board.Markers.Visible = false; }
        _app.Windows.Toolbar.GuiRoot.Visible = _ui;
        _app.Windows.Status.Root.Visible = _ui;
    }

    static double Ease(double u) => u * u * (3 - 2 * u);

    // the keys at t: (a, b, u) with u eased
    (Key A, Key B, double U) At(double t)
    {
        Key a = _cam[0], b = _cam[^1];
        if (t >= _cam[^1].T) return (b, b, 0);
        for (int i = 0; i + 1 < _cam.Count; i++)
            if (t >= _cam[i].T && t <= _cam[i + 1].T) { a = _cam[i]; b = _cam[i + 1]; break; }
        double u = b.T > a.T ? Ease(Math.Clamp((t - a.T) / (b.T - a.T), 0, 1)) : 0;
        return (a, b, u);
    }

    Vector3 OnBoard(double x, double y) =>
        _app!.Session!.Board.GlobalTransform * new Vector3((float)x, (float)y, (float)BoardZ.LEMMING_Z);

    // where the eye should be: the key's fixed point, the solution's next move, the crowd
    Vector3 Target(Key a, Key b, double u)
    {
        if (_scene != "level")
        {
            // the screen or the catalog window, `tx, ty` metres across and up from its middle
            var root = _scene == "lobby" ? (Node3D)_app!.Lobby.Screen : _app!.Windows.Catalog.Panel;
            double ox = a.Tx == null ? 0 : a.Tx.Value + (b.Tx!.Value - a.Tx.Value) * u, oy = a.Ty == null ? 0 : a.Ty.Value + (b.Ty!.Value - a.Ty.Value) * u;
            return root.GlobalTransform * new Vector3((float)ox, (float)oy, 0);
        }
        var s = _app!.Session!;
        if (a.Tx != null && b.Tx != null)
            return OnBoard(a.Tx.Value + (b.Tx.Value - a.Tx.Value) * u, a.Ty!.Value + (b.Ty!.Value - a.Ty!.Value) * u);
        var sim = s.Game.Sim;
        int now = sim.CurrentIteration;
        // a bomber counting down is where the eye goes: the next to blow
        var fuse = sim.Lemmings.Where(l => !l.Removed && l.ExplosionTimer > 0).OrderBy(l => l.ExplosionTimer).FirstOrDefault();
        if (fuse != null) return OnBoard(fuse.X, fuse.Y - 6);
        var next = sim.Recorded.FirstOrDefault(r => r.Frame >= now && r.Frame <= now + _lead && r.Type == "assignment");
        if (next != null)
        {
            var lem = next.LemIndex >= 0 && next.LemIndex < sim.Lemmings.Count ? sim.Lemmings[next.LemIndex] : null;
            if (lem != null && !lem.Removed) return OnBoard(lem.X, lem.Y - 6);
            return OnBoard(next.X, next.Y - 6);
        }
        var live = sim.Lemmings.Where(l => !l.Removed).ToList();
        if (live.Count > 0) return OnBoard(live.Average(l => l.X), live.Average(l => l.Y) - 6);
        return OnBoard(sim.Width / 2.0, sim.Height / 2.0);
    }

    // the hands where a standing player holds them, in the head's frame; the right's beam on the target
    void PlaceHands(Transform3D head, Vector3 target)
    {
        if (!_hands) return;
        var r = _input.HandsValue[1];
        var from = head * new Vector3(0.16f, -0.2f, -0.3f);
        r.Aim = new Transform3D(Basis.LookingAt((target - from).Normalized(), Vector3.Up), from);
        r.Grip = r.Aim.Translated(new Vector3(0, -0.02f, 0.05f));
        var l = _input.HandsValue[0];
        var lf = head * new Vector3(-0.26f, -0.42f, -0.34f);
        l.Aim = new Transform3D(Basis.LookingAt((head * new Vector3(-0.15f, -0.9f, -1.0f) - lf).Normalized(), Vector3.Up), lf);
        l.Grip = l.Aim;
    }

    public override void _Process(double delta)
    {
        _ms += delta * 1000;
        _frame++;
        if (_app == null) return;
        HideReplayLook();
        var s = _app.Session;
        if (_recAt >= 0 && (s != null || _scene != "level"))
        {
            double t = (_ms - _recAt) / 1000;
            var (a, b, u) = At(t);
            double L(double p, double q) => p + (q - p) * u;
            var raw = Target(a, b, u);
            float k = (float)(1 - Math.Exp(-delta / _tau));
            _aim = _aim == null ? raw : _aim.Value + (raw - _aim.Value) * k;
            var stand = new Vector3((float)L(a.X, b.X), 1.6f + (float)L(a.Y, b.Y), (float)L(a.Z, b.Z));
            var eye = stand + (_aim.Value - stand) * (float)L(a.Near, b.Near);
            var pose = new Transform3D(Basis.LookingAt((_aim.Value - eye).Normalized(), Vector3.Up), eye);
            _head.Fov = (float)L(a.Fov, b.Fov);
            _input.HeadValue = pose;
            PlaceHands(pose, _scene == "lobby" ? _app.Lobby.Signs[0].GlobalPosition : _aim.Value);
            if (t >= _seconds) { GD.Print($"[trailer] done {_frame}"); GetTree().Quit(0); _app = null; }
            return;
        }
        if (_scene != "level")
        {
            // the lobby the app starts on, or the catalog its PLAY opens
            if (++_settle == 2)
            {
                if (_scene == "catalog") _app.Windows.SetCatalog(true);
                else _app.Lobby.CentreScrollerText();
            }
            if (_settle < 40) return;
            _recAt = _ms;
            GD.Print($"[trailer] rec {_frame} scene={_scene}");
            return;
        }
        // warm-up: the level loaded, its room built, then the solution from `start` at `speed`
        if (s == null || _app.Loading) return;
        if (++_settle == 1) { s.Game.GotoFrame(_start, true); return; }
        if (_settle < 45) return;
        s.Game.GameTimer.SpeedFactor = _speed;
        if (!s.Running) s.TogglePause();
        _recAt = _ms;
        GD.Print($"[trailer] rec {_frame} level={_level} frame={_start}");
    }
}
