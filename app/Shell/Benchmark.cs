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
// everything). Records the frame times, the sim's share, draw calls and primitives, GC pauses and
// memory per run, writes user://perf.json and quits.
public partial class Benchmark : Node
{
    const double RunSeconds = 20, RewindEvery = 5, AssignEvery = 0.4;
    const int LevelCount = 10;

    readonly App _app;
    readonly List<(string Level, string Look)> _plan = new();
    int _index = -1;
    double _runStart, _lastRewind, _lastAssign, _lastFrame;
    readonly List<double> _frames = new();
    readonly List<double> _simMs = new();
    long _draws, _prims, _samples;
    double _gc0;
    int _gen2;
    readonly JsonArray _runs = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Random _rnd = new(1);

    public Benchmark(App app) { _app = app; Name = "benchmark"; }

    public override void _Ready()
    {
        foreach (string id in LargestLevels(LevelCount))
        {
            _plan.Add((id, "default"));
            _plan.Add((id, "heavy"));
        }
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

    void Next()
    {
        if (_index >= 0) Close();
        _index++;
        if (_index >= _plan.Count) { Finish(); return; }
        var (level, look) = _plan[_index];
        SetLook(look);
        _app.EnterLevel(level);
        _frames.Clear(); _simMs.Clear(); _draws = _prims = _samples = 0;
        _runStart = _lastRewind = _lastAssign = Now();
        _lastFrame = double.NaN;
        _gc0 = GC.GetTotalPauseDuration().TotalMilliseconds;
        _gen2 = GC.CollectionCount(2);
    }

    double Now() => _clock.Elapsed.TotalMilliseconds;

    public override void _Process(double delta)
    {
        if (_index < 0 || _index >= _plan.Count) return;
        var s = _app.Session;
        double now = Now();
        if (!double.IsNaN(_lastFrame)) _frames.Add(now - _lastFrame);
        _lastFrame = now;
        _draws += (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        _prims += (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        _samples++;
        if (s == null) return;
        var timer = s.Game.GameTimer;
        if (!timer.IsRunning()) timer.Continue();
        timer.SpeedFactor = 8;
        if ((now - _lastAssign) / 1000 >= AssignEvery)
        {
            _lastAssign = now;
            var lems = s.Game.Sim.Lemmings.Where(l => !l.Removed).ToList();
            var skills = s.Game.Sim.ActiveSkills;
            if (lems.Count > 0 && skills.Count > 0)
            {
                var L = lems[_rnd.Next(lems.Count)];
                s.Game.Sim.SetSelectedSkill(skills[_rnd.Next(skills.Count)]);
                s.AssignAt(L.X, L.Y - 5);
            }
        }
        if ((now - _lastRewind) / 1000 >= RewindEvery && s.Game.Sim.CurrentIteration > 200)
        {
            _lastRewind = now;
            var sw = Stopwatch.StartNew();
            s.Game.BackFrames(170);
            _simMs.Add(sw.Elapsed.TotalMilliseconds);
            timer.Continue();
        }
        if ((now - _runStart) / 1000 >= RunSeconds || s.Game.Sim.GameFinished) Next();
    }

    void Close()
    {
        var (level, look) = _plan[_index];
        var sorted = _frames.OrderBy(x => x).ToList();
        double P(double q) => sorted.Count == 0 ? 0 : Math.Round(sorted[(int)Math.Min(sorted.Count - 1, sorted.Count * q)], 2);
        var s = _app.Session;
        _runs.Add(new JsonObject
        {
            ["level"] = level, ["look"] = look,
            ["size"] = s == null ? "" : s.Level.Width + "x" + s.Level.Height,
            ["frames"] = sorted.Count,
            ["frameMs"] = new JsonObject { ["p50"] = P(0.5), ["p90"] = P(0.9), ["p99"] = P(0.99), ["max"] = sorted.Count == 0 ? 0 : Math.Round(sorted[^1], 2) },
            ["over11ms"] = sorted.Count(f => f > 11.1),
            ["rewindMs"] = _simMs.Count == 0 ? 0 : Math.Round(_simMs.Max(), 1),
            ["drawCalls"] = _samples == 0 ? 0 : _draws / _samples,
            ["primitives"] = _samples == 0 ? 0 : _prims / _samples,
            ["gcPauseMs"] = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds - _gc0, 2),
            ["gen2"] = GC.CollectionCount(2) - _gen2,
            ["memoryMB"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
        });
        GD.Print($"[lemmix] benchmark {_index + 1}/{_plan.Count} {look} {level}: p99 {P(0.99)} ms");
    }

    void Finish()
    {
        var xr = XRServer.FindInterface("OpenXR") as OpenXRInterface;
        var report = new JsonObject
        {
            ["version"] = ProjectSettings.GetSetting("application/config/version").AsString(),
            ["renderer"] = RenderingServer.GetCurrentRenderingMethod(),
            ["adapter"] = RenderingServer.GetVideoAdapterName(),
            ["xr"] = xr != null && xr.IsInitialized(),
            ["refreshRate"] = xr != null && xr.IsInitialized() ? xr.DisplayRefreshRate : 0,
            ["runs"] = _runs,
            ["worstP99"] = _runs.Max(r => r!["frameMs"]!["p99"]!.GetValue<double>()),
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        using (var f = Godot.FileAccess.Open("user://perf.json", Godot.FileAccess.ModeFlags.Write)) f?.StoreString(json);
        GD.Print("[lemmix] benchmark done " + ProjectSettings.GlobalizePath("user://perf.json"));
        GetTree().Quit(0);
    }
}
