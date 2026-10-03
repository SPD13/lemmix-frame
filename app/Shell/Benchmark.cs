using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Lemmix.Engine;

namespace Lemmix.App.Shell;

// `-- --benchmark`: the device session's unattended performance run (docs/device-session-1.md).
// The ten largest levels, each in the default look and in the heaviest one (soft colour blend over
// smoothed edges, emboss, doors, the full room), 20 s at x8 with skills given steadily (digging
// and building re-mesh the terrain) and a step back of 170 frames every 5 s (a rewind re-meshes
// everything). The load is timed on its own and play is measured from the first second of game
// time; a run ends at 20 s or when the level ends. Records per run the frame times, the frames over
// the 90 Hz budget, the rewinds, draw calls and primitives, GC pauses and collections, garbage and
// memory, and per section of the frame (Perf.S) its time and garbage - also what it spent in the
// slow frames - then writes user://perf.json and quits.
public partial class Benchmark : Node
{
    const double RunSeconds = 20, RewindEvery = 5, AssignEvery = 0.4;
    const int LevelCount = 10;
    const int WarmTicks = 17;          // play is measured from the level's first second of game time
    const double SlowMs = 11.1;        // a frame over the Frame's 90 Hz budget

    readonly App _app;
    readonly List<(string Level, string Look)> _plan = new();
    int _index = -1;
    double _runStart, _lastRewind, _lastAssign, _lastFrame;
    bool _warm;                        // the first ticks are behind: frames count as play
    double _loadMs, _warmMs, _warmMax;
    int _warmFrames;
    bool _rewoundThisFrame;
    Lemmix.App.Session.GameSession? _session;
    bool _ended;                       // the level ended: the run ends with it (the shell would load a level)
    readonly List<double> _frames = new(4096);
    readonly List<double> _rewindMs = new();
    readonly List<double> _rewindFrameMs = new();
    readonly List<double> _frameGc = new(4096);
    readonly List<double>[] _sub = new List<double>[(int)Perf.S.Count];
    readonly long[] _subBytes = new long[(int)Perf.S.Count];
    readonly List<Lemming> _lems = new();
    long _draws, _prims, _samples;
    double _gc0, _gcLoad0, _gcLast;
    int _gen2, _gen2Load0, _gen0, _gen1;
    long _alloc0;
    readonly JsonArray _runs = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Random _rnd = new(1);

    public Benchmark(App app)
    {
        _app = app; Name = "benchmark";
        for (int i = 0; i < _sub.Length; i++) _sub[i] = new List<double>(4096);
    }

    public override void _Ready()
    {
        foreach (string id in LargestLevels(LevelCount))
        {
            _plan.Add((id, "default"));
            _plan.Add((id, "heavy"));
        }
        Perf.On = true;
        GD.Print($"[lemmix] benchmark: {_plan.Count} runs of {RunSeconds} s");
        Next();
    }

    IEnumerable<string> LargestLevels(int n)
    {
        string path = Path.Combine(_app.AssetRoot, "levels", "index.json");
        if (!File.Exists(path)) return Array.Empty<string>();
        var levels = new List<(string Id, long Area)>();
        void Walk(JsonNode? node)
        {
            if (node?["levels"] is JsonArray lv)
                foreach (var l in lv)
                {
                    long w = l?["width"]?.GetValue<long>() ?? 0, h = l?["height"]?.GetValue<long>() ?? 0;
                    if (l?["id"]?.GetValue<string>() is string id) levels.Add((id, w * h));
                }
            if (node?["children"] is JsonArray ch) foreach (var c in ch) Walk(c);
        }
        try { Walk(JsonNode.Parse(File.ReadAllText(path))); } catch (Exception e) { GD.PushError("[benchmark] index: " + e.Message); }
        return levels.OrderByDescending(l => l.Area).Take(n).Select(l => l.Id);
    }

    void SetLook(string look)
    {
        var fx = _app.Fx;
        bool heavy = look == "heavy";
        // the switches as toggles, until each is where the look wants it
        if (fx.Emboss != heavy) fx.ToggleEmboss();
        if (fx.Smooth != heavy) fx.ToggleSmooth();
        if (fx.SmoothTerrain != heavy) fx.ToggleSmoothTerrain();
        if (!fx.Doors) fx.ToggleDoors();
        string blend = heavy ? "soft" : "off";
        for (int i = 0; i < 3 && fx.ColorBlend != blend; i++) fx.ToggleColorBlend();
        string env = heavy ? "full" : "none";
        for (int i = 0; i < 3 && fx.Environment != env; i++) fx.ToggleEnvironment();
    }

    // the next run: the level loaded in its look (timed as the load), the counters reset
    void Next()
    {
        if (_index >= 0) Close();
        _index++;
        if (_index >= _plan.Count) { Finish(); return; }
        var (level, look) = _plan[_index];
        _gcLoad0 = GC.GetTotalPauseDuration().TotalMilliseconds;
        _gen2Load0 = GC.CollectionCount(2);
        double t0 = Now();
        SetLook(look);
        _app.EnterLevel(level);
        _loadMs = Now() - t0;
        _session = _app.Session;
        _ended = false;
        if (_session != null) _session.LevelEnded += _ => _ended = true;
        _frames.Clear(); _rewindMs.Clear(); _rewindFrameMs.Clear(); _frameGc.Clear();
        foreach (var l in _sub) l.Clear();
        Array.Clear(_subBytes);
        _draws = _prims = _samples = 0;
        _warm = false; _warmFrames = 0; _warmMax = 0; _warmMs = 0;
        _lastFrame = double.NaN;
        _runStart = _lastRewind = _lastAssign = Now();
    }

    // play starts: the clocks and GC counters from here
    void StartPlay(double now)
    {
        _warm = true;
        _warmMs = now - _runStart;
        _runStart = _lastRewind = _lastAssign = now;
        _gc0 = _gcLast = GC.GetTotalPauseDuration().TotalMilliseconds;
        _gen2 = GC.CollectionCount(2); _gen1 = GC.CollectionCount(1); _gen0 = GC.CollectionCount(0);
        _alloc0 = GC.GetTotalAllocatedBytes();
    }

    double Now() => _clock.Elapsed.TotalMilliseconds;

    public override void _Process(double delta)
    {
        if (_index < 0 || _index >= _plan.Count) return;
        var s = _app.Session;
        double now = Now();
        // the frame that just ended: its time, its GC pause, its sections
        if (!double.IsNaN(_lastFrame))
        {
            double ms = now - _lastFrame;
            if (_warm)
            {
                _frames.Add(ms);
                double gc = GC.GetTotalPauseDuration().TotalMilliseconds;
                _frameGc.Add(gc - _gcLast);
                _gcLast = gc;
                for (int i = 0; i < _sub.Length; i++) { _sub[i].Add(Perf.Ms(Perf.Ticks[i])); _subBytes[i] += Perf.Bytes[i]; }
                if (_rewoundThisFrame) _rewindFrameMs.Add(ms);
            }
            else { _warmFrames++; _warmMax = Math.Max(_warmMax, ms); }
        }
        Perf.Reset();
        _rewoundThisFrame = false;
        _lastFrame = now;
        if (_warm)
        {
            _draws += (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            _prims += (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
            _samples++;
        }
        if (s == null) return;
        if (s != _session || _ended) { Next(); return; }
        var timer = s.Game.GameTimer;
        if (!timer.IsRunning()) timer.Continue();
        timer.SpeedFactor = 8;
        if (!_warm)
        {
            if (s.Game.Sim.CurrentIteration >= WarmTicks) StartPlay(now);
            return;
        }
        if ((now - _lastAssign) / 1000 >= AssignEvery)
        {
            using var _ = Perf.Time(Perf.S.Bench);
            _lastAssign = now;
            _lems.Clear();
            foreach (var l in s.Game.Sim.Lemmings) if (!l.Removed) _lems.Add(l);
            var skills = s.Game.Sim.ActiveSkills;
            if (_lems.Count > 0 && skills.Count > 0)
            {
                var L = _lems[_rnd.Next(_lems.Count)];
                s.Game.Sim.SetSelectedSkill(skills[_rnd.Next(skills.Count)]);
                s.AssignAt(L.X, L.Y - 5);
            }
        }
        if ((now - _lastRewind) / 1000 >= RewindEvery && s.Game.Sim.CurrentIteration > 200)
        {
            _lastRewind = now;
            var sw = Stopwatch.StartNew();
            using (Perf.Time(Perf.S.Rewind)) s.Game.BackFrames(170);
            _rewindMs.Add(sw.Elapsed.TotalMilliseconds);
            _rewoundThisFrame = true;
            timer.Continue();
        }
        if ((now - _runStart) / 1000 >= RunSeconds || s.Game.Sim.GameFinished) Next();
    }

    static double Round(double v, int d = 2) => Math.Round(v, d);

    static double Pct(List<double> sorted, double q) =>
        sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * q)];

    void Close()
    {
        var (level, look) = _plan[_index];
        var sorted = _frames.OrderBy(x => x).ToList();
        var s = _app.Session;
        int slow = _frames.Count(f => f > SlowMs);
        // per section: the total, the mean, the 99th percentile and worst frame, the garbage, and
        // what it spent in the slow frames (with "other": the frame outside every section -
        // rendering, the engine, a GC pause)
        var sections = new JsonObject();
        var inSlow = new double[_sub.Length + 1];
        int slowWithGc = 0;
        double slowGcMs = 0;
        for (int f = 0; f < _frames.Count; f++)
        {
            if (_frames[f] <= SlowMs) continue;
            double sum = 0;
            for (int i = 0; i < _sub.Length; i++) { inSlow[i] += _sub[i][f]; sum += _sub[i][f]; }
            inSlow[_sub.Length] += Math.Max(0, _frames[f] - sum);
            if (_frameGc[f] > 0) { slowWithGc++; slowGcMs += _frameGc[f]; }
        }
        for (int i = 0; i < _sub.Length; i++)
        {
            var v = _sub[i];
            if (v.Count == 0) continue;
            var vs = v.OrderBy(x => x).ToList();
            double total = v.Sum();
            if (total == 0 && _subBytes[i] == 0) continue;
            sections[Perf.Name(i)] = new JsonObject
            {
                ["totalMs"] = Round(total, 1), ["meanMs"] = Round(total / v.Count, 3),
                ["p99Ms"] = Round(Pct(vs, 0.99)), ["maxMs"] = Round(vs[^1]),
                ["allocMB"] = Round(_subBytes[i] / 1048576.0), ["slowFramesMs"] = Round(inSlow[i], 1),
            };
        }
        var other = new List<double>(_frames.Count);
        for (int f = 0; f < _frames.Count; f++)
        {
            double sum = 0;
            for (int i = 0; i < _sub.Length; i++) sum += _sub[i][f];
            other.Add(Math.Max(0, _frames[f] - sum));
        }
        var os = other.OrderBy(x => x).ToList();
        sections["Other"] = new JsonObject
        {
            ["totalMs"] = Round(other.Sum(), 1), ["meanMs"] = Round(other.Count == 0 ? 0 : other.Average(), 3),
            ["p99Ms"] = Round(Pct(os, 0.99)), ["maxMs"] = Round(os.Count == 0 ? 0 : os[^1]),
            ["slowFramesMs"] = Round(inSlow[_sub.Length], 1),
        };
        _runs.Add(new JsonObject
        {
            ["level"] = level, ["look"] = look,
            ["size"] = s == null ? "" : s.Level.Width + "x" + s.Level.Height,
            ["loadMs"] = Round(_loadMs, 1),
            ["warmup"] = new JsonObject { ["frames"] = _warmFrames, ["ms"] = Round(_warmMs, 1), ["maxFrameMs"] = Round(_warmMax), ["gcPauseMs"] = Round(_gc0 - _gcLoad0), ["gen2"] = _gen2 - _gen2Load0 },
            ["frames"] = sorted.Count,
            ["frameMs"] = new JsonObject { ["p50"] = Round(Pct(sorted, 0.5)), ["p90"] = Round(Pct(sorted, 0.9)), ["p99"] = Round(Pct(sorted, 0.99)), ["max"] = sorted.Count == 0 ? 0 : Round(sorted[^1]) },
            ["over11ms"] = slow,
            ["over11pct"] = sorted.Count == 0 ? 0 : Round(100.0 * slow / sorted.Count),
            ["slowFramesWithGc"] = slowWithGc, ["slowFramesGcMs"] = Round(slowGcMs, 1),
            ["rewinds"] = _rewindMs.Count,
            ["rewindMs"] = _rewindMs.Count == 0 ? 0 : Round(_rewindMs.Max(), 1),
            ["rewindFrameMs"] = _rewindFrameMs.Count == 0 ? 0 : Round(_rewindFrameMs.Max(), 1),
            ["drawCalls"] = _samples == 0 ? 0 : _draws / _samples,
            ["primitives"] = _samples == 0 ? 0 : _prims / _samples,
            ["gcPauseMs"] = Round(GC.GetTotalPauseDuration().TotalMilliseconds - _gc0),
            ["gen0"] = GC.CollectionCount(0) - _gen0, ["gen1"] = GC.CollectionCount(1) - _gen1,
            ["gen2"] = GC.CollectionCount(2) - _gen2,
            ["allocMB"] = Round((GC.GetTotalAllocatedBytes() - _alloc0) / 1048576.0, 1),
            ["memoryMB"] = Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            ["sections"] = sections,
        });
        GD.Print($"[lemmix] benchmark {_index + 1}/{_plan.Count} {look} {level}: load {_loadMs:F0} ms, p99 {Round(Pct(sorted, 0.99))} ms, {slow} slow, rewind {(_rewindFrameMs.Count == 0 ? 0 : _rewindFrameMs.Max()):F1} ms, gc {GC.GetTotalPauseDuration().TotalMilliseconds - _gc0:F0} ms");
    }

    void Finish()
    {
        Perf.On = false;
        var xr = XRServer.FindInterface("OpenXR") as OpenXRInterface;
        var report = new JsonObject
        {
            ["version"] = ProjectSettings.GetSetting("application/config/version").AsString(),
            ["renderer"] = RenderingServer.GetCurrentRenderingMethod(),
            ["adapter"] = RenderingServer.GetVideoAdapterName(),
            ["xr"] = xr != null && xr.IsInitialized(),
            ["refreshRate"] = xr != null && xr.IsInitialized() ? xr.DisplayRefreshRate : 0,
            ["gc"] = new JsonObject { ["server"] = System.Runtime.GCSettings.IsServerGC, ["latencyMode"] = System.Runtime.GCSettings.LatencyMode.ToString() },
            ["runs"] = _runs,
            ["worstP99"] = _runs.Max(r => r!["frameMs"]!["p99"]!.GetValue<double>()),
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        using (var f = Godot.FileAccess.Open("user://perf.json", Godot.FileAccess.ModeFlags.Write)) f?.StoreString(json);
        GD.Print("[lemmix] benchmark done " + ProjectSettings.GlobalizePath("user://perf.json"));
        GetTree().Quit(0);
    }
}
