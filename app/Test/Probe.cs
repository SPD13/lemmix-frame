using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Lemmix.App.Test;

// The on-device probe (plan, device session 1): run with `-- --probe`, unattended.
// Records what only the Frame can answer - the OpenXR runtime and its extensions, the
// interaction profiles bound, refresh rates, foveation - then renders a stress scene shaped
// like a big level (1000 chunk meshes, 100 lemmings as a MultiMesh) for 20 s and writes
// frame-time percentiles. Output: user://probe.json, pulled by tools/frame-pull-report.sh.
public partial class Probe : Node3D
{
    const double Seconds = 20;
    readonly List<double> _frameMs = new();
    readonly Stopwatch _clock = new();
    double _last;
    MultiMesh? _lemmings;

    public override void _Ready()
    {
        BuildStress();
        _clock.Start();
    }

    void BuildStress()
    {
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true };
        var rnd = new RandomNumberGenerator { Seed = 1 };
        var board = new Node3D { Position = new Vector3(-2, 0.8f, -1.2f) };
        AddChild(board);
        for (int c = 0; c < 1000; c++)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int q = 0; q < 64; q++) // 64 relief quads a chunk
            {
                float x = rnd.Randf() * 0.08f, y = rnd.Randf() * 0.08f, z = rnd.Randf() * 0.02f, s = 0.01f;
                st.SetColor(new Color(rnd.Randf(), rnd.Randf(), rnd.Randf()));
                st.AddVertex(new Vector3(x, y, z)); st.AddVertex(new Vector3(x + s, y, z)); st.AddVertex(new Vector3(x, y + s, z));
                st.AddVertex(new Vector3(x + s, y, z)); st.AddVertex(new Vector3(x + s, y + s, z)); st.AddVertex(new Vector3(x, y + s, z));
            }
            var mi = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, Position = new Vector3(c % 50 * 0.08f, c / 50 * 0.08f, 0) };
            board.AddChild(mi);
        }
        _lemmings = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh { Size = new Vector3(0.02f, 0.025f, 0.005f) }, InstanceCount = 100 };
        board.AddChild(new MultiMeshInstance3D { Multimesh = _lemmings, MaterialOverride = mat });
    }

    public override void _Process(double delta)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_last > 0) _frameMs.Add(now - _last);
        _last = now;
        if (_lemmings != null)
            for (int i = 0; i < 100; i++)
                _lemmings.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(i * 0.04f, (float)(0.2 + 0.05 * Mathf.Sin(now / 300 + i)), 0.02f)));
        if (now / 1000 >= Seconds) Finish();
    }

    void Finish()
    {
        SetProcess(false);
        var sorted = _frameMs.OrderBy(x => x).ToList();
        double P(double q) => sorted.Count == 0 ? 0 : System.Math.Round(sorted[(int)System.Math.Min(sorted.Count - 1, sorted.Count * q)], 2);
        var xr = XRServer.FindInterface("OpenXR") as OpenXRInterface;
        var report = new Dictionary<string, object?>
        {
            ["renderer"] = RenderingServer.GetCurrentRenderingMethod(),
            ["videoAdapter"] = RenderingServer.GetVideoAdapterName() + " / " + RenderingServer.GetVideoAdapterVendor(),
            ["apiVersion"] = RenderingServer.GetVideoAdapterApiVersion(),
            ["xr"] = xr == null || !xr.IsInitialized() ? null : new Dictionary<string, object?>
            {
                ["displayRefreshRate"] = xr.DisplayRefreshRate,
                ["availableRefreshRates"] = xr.GetAvailableDisplayRefreshRates(),
                ["foveationSupported"] = xr.IsFoveationSupported(),
                ["renderTargetSize"] = xr.GetRenderTargetSize().ToString(),
                ["actionSets"] = xr.GetActionSets(),
                ["leftProfile"] = XRServer.GetTracker("left_hand") is XRControllerTracker l ? l.Profile : null,
                ["rightProfile"] = XRServer.GetTracker("right_hand") is XRControllerTracker r ? r.Profile : null,
            },
            ["frames"] = sorted.Count,
            ["frameMs"] = new { p50 = P(0.5), p90 = P(0.9), p99 = P(0.99), max = sorted.Count == 0 ? 0 : System.Math.Round(sorted[^1], 2) },
            ["gcPauseMs"] = System.Math.Round(System.GC.GetTotalPauseDuration().TotalMilliseconds, 2),
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        using (var f = FileAccess.Open("user://probe.json", FileAccess.ModeFlags.Write)) f.StoreString(json);
        GD.Print("[lemmix] probe done " + ProjectSettings.GlobalizePath("user://probe.json"));
        GD.Print(json);
        GetTree().Quit(0);
    }
}
