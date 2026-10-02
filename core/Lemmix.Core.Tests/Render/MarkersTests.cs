using System.Collections.Concurrent;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Parse;
using Lemmix.Render;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Render;

// oracle/markers.js: the replay markers (web/3d/js/replay-markers.js) of every stored solution
// plus a few release-rate changes and a nuke - planes, pictures, label layouts, and their state
// at a handful of frames.
public class MarkersTests
{
    // panel.js SKILL_ICONS / SKILL_BRICKS: the panel's button picture (GamePanel._skillIcon),
    // made here from the sprite PNGs so the test stands on its own
    static readonly Dictionary<string, (string Sprite, int Dx, int Frame, int X, int Y)> SkillIcons = new()
    {
        ["WALKER"] = ("walker", 1, 1, 6, 21), ["JUMPER"] = ("jumper", 1, 0, 6, 20), ["SHIMMIER"] = ("shimmier", 1, 1, 7, 20),
        ["SLIDER"] = ("slider", -1, 0, 5, 21), ["CLIMBER"] = ("climber", 1, 3, 10, 22), ["SWIMMER"] = ("swimmer", 1, 2, 8, 19),
        ["FLOATER"] = ("floater", 1, 4, 7, 26), ["GLIDER"] = ("glider", 1, 4, 7, 26), ["DISARMER"] = ("disarmer", 1, 6, 4, 21),
        ["BOMBER"] = ("bomber", 1, 0, 8, 21), ["STONER"] = ("stoner", 1, 0, 8, 21), ["BLOCKER"] = ("blocker", 1, 0, 7, 21),
        ["PLATFORMER"] = ("platformer", 1, 1, 7, 20), ["BUILDER"] = ("builder", 1, 1, 7, 20), ["STACKER"] = ("stacker", 1, 0, 7, 21),
        ["LASERER"] = ("laserer", 1, 0, 8, 21), ["BASHER"] = ("basher", 1, 0, 8, 21), ["FENCER"] = ("fencer", 1, 1, 7, 21),
        ["MINER"] = ("miner", 1, 12, 4, 21), ["DIGGER"] = ("digger", 1, 4, 7, 21), ["CLONER"] = ("walker", -1, 1, 6, 21),
    };
    static readonly Dictionary<string, int[][]> SkillBricks = new()
    {
        ["PLATFORMER"] = new[] { new[] { 2, 21 }, new[] { 5, 21 }, new[] { 8, 21 }, new[] { 11, 21 } },
        ["BUILDER"] = new[] { new[] { 4, 22 }, new[] { 6, 21 }, new[] { 8, 20 }, new[] { 10, 19 } },
        ["STACKER"] = new[] { new[] { 10, 20 }, new[] { 10, 19 }, new[] { 10, 18 }, new[] { 10, 17 } },
    };

    static Bitmap SkillIcon(string setName, string name)
    {
        var io = OracleData.Io;
        string dir = "neolemmix/styles/" + setName + "/lemmings/";
        string? text = io.Text(dir + "scheme.nxmi");
        if (text == null) { dir = "neolemmix/styles/default/lemmings/"; text = io.Text(dir + "scheme.nxmi")!; }
        var bmp = new Bitmap(16, 23);
        if (SkillIcons.TryGetValue(name, out var spec))
        {
            var sec = NxParser.Parse(text).Section("ANIMATIONS")?.Section(spec.Sprite.ToUpperInvariant());
            var image = sec != null ? io.Image(dir + spec.Sprite + ".png") : null;
            if (sec != null && image != null)
            {
                int count = sec.Int("FRAMES", 1); if (count == 0) count = 1;
                int w = image.Width >> 1, h = image.Height / count;
                var side = sec.Section(spec.Dx > 0 ? "RIGHT" : "LEFT");
                int footX = side?.Int("FOOT_X", 0) ?? 0, footY = side?.Int("FOOT_Y", 0) ?? 0;
                var frame = image.Crop(spec.Dx > 0 ? w : 0, Math.Min(spec.Frame, count - 1) * h, w, h);
                Pixels.Blit(bmp, spec.X - footX, spec.Y - footY, frame, 0, 0, frame.Width, frame.Height, Pixels.CombineGadget);
            }
        }
        if (SkillBricks.TryGetValue(name, out var bricks))
            foreach (var b in bricks)
                for (int o = 0; o < 2; o++)
                {
                    int x = b[0] + o, y = b[1];
                    if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) continue;
                    int p = (y * bmp.Width + x) * 4;
                    bmp.Data[p] = 0xf0; bmp.Data[p + 1] = 0xd0; bmp.Data[p + 2] = 0xd0; bmp.Data[p + 3] = 255;
                }
        return bmp;
    }

    static void Canvas(StateHash h, int w, int hgt, Bitmap? image, List<object[]> ops)
    {
        h.Word(w); h.Word(hgt);
        if (image != null) h.Bytes(image.Data); else h.Null();
        h.Word(ops.Count);
        foreach (var op in ops)
        {
            h.Word(op.Length);
            foreach (var v in op) if (v is string s) h.Str(s); else RenderHash.F64(h, Convert.ToDouble(v));
        }
    }

    static void IconCanvas(StateHash h, ReplayMarkers.Icon icon) =>
        Canvas(h, icon.W, icon.H, icon.Picture, new List<object[]> { new object[] { "putImageData", 0, 0 } });

    static void LabelCanvas(StateHash h, ReplayMarkers.LabelSpec l) =>
        Canvas(h, l.Width, l.Height, null, new List<object[]>
        {
            new object[] { "fillRect", l.Background, 0, 0, l.Width, l.Height },
            new object[] { "fillText", l.Color, l.Font, l.Align, l.Baseline, l.Text, l.X, l.Y },
        });

    static void PlaneObject(StateHash h, ReplayMarkers.Plane p, double scaleY, bool visible)
    {
        RenderHash.F64(h, p.X); RenderHash.F64(h, p.Y); RenderHash.F64(h, p.Z); RenderHash.F64(h, scaleY); h.Word(p.RenderOrder); h.Bool(visible);
    }

    [Fact]
    public void MarkersStandAsTheWebStandsThem()
    {
        using var doc = OracleData.Load("markers.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        double z = doc!.RootElement.GetProperty("z").GetDouble();
        Assert.Equal(Portals.LEMMING_Z + 2, z);
        var rows = doc.RootElement.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var wantIcons = doc.RootElement.GetProperty("icons").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        var gotIcons = new ConcurrentDictionary<string, string>();
        var failures = new ConcurrentBag<string>();
        var masks = RenderHash.Masks;
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try
            {
                var level = RenderHash.Build(row, styles);
                var sim = new LemGame(level, masks);
                sim.LoadReplay(Replay.Parse(File.ReadAllText(Path.Combine(OracleData.RepoRoot, "web", row.GetProperty("nxrp").GetString()!))));
                sim.Recorded.Add(new ReplayEntry { Type = "spawn_interval", Frame = 40, Interval = level.SpawnInterval - 10, Spawned = 0 });
                sim.Recorded.Add(new ReplayEntry { Type = "spawn_interval", Frame = 40, Interval = level.SpawnInterval + 4, Spawned = 3 });
                sim.Recorded.Add(new ReplayEntry { Type = "spawn_interval", Frame = 300, Interval = level.SpawnInterval + 7, Spawned = 200 });
                sim.Recorded.Add(new ReplayEntry { Type = "nuke", Frame = 777 });
                string setName = level.Theme.Lemmings is { Length: > 0 } l ? l : "default";
                var markers = new ReplayMarkers.Set(level, z, name => SkillIcon(setName, name),
                    name => OracleData.Io.Image("neolemmix/gfx/panel/" + name + ".png"));
                var h = new StateHash();
                int last = sim.Recorded.Max(r => r.Frame);
                var steps = new (int Frame, double Now)[] { (0, 0), (40, 250), (41, 1000), (last / 2, 3333.3), (last, 77), (last + 1, 5) };
                for (int k = 0; k < steps.Length; k++)
                {
                    markers.Hidden = k == 2;
                    markers.Update(true, sim.Recorded, sim.RecordVersion, steps[k].Frame, steps[k].Now);
                    if (k == 0)
                    {
                        RenderHash.Geometry(h, markers.RingGeometry);
                        h.Word(markers.Markers.Count);
                        foreach (var m in markers.Markers)
                        {
                            h.Word(m.Frame); h.Str(m.Entry.Type); h.Str(m.TextOnly);
                            h.Word(2 + (m.IconPlane != null ? 1 : 0) + (m.TextPlane != null ? 1 : 0));
                            PlaneObject(h, m.Ring, 1, true); h.Str("RingGeometry"); RenderHash.Geometry(h, markers.RingGeometry); h.Null();
                            if (m.IconPlane != null)
                            {
                                PlaneObject(h, m.IconPlane, -1, true); h.Str("plane"); RenderHash.F64(h, m.IconPlane.W); RenderHash.F64(h, m.IconPlane.H);
                                IconCanvas(h, m.Icon!);
                            }
                            if (m.TextPlane != null)
                            {
                                PlaneObject(h, m.TextPlane, -1, true); h.Str("plane"); RenderHash.F64(h, 12); RenderHash.F64(h, 5);
                                LabelCanvas(h, m.TextLabel!);
                            }
                            PlaneObject(h, m.Countdown, -1, m.LabelVisible); h.Str("plane"); RenderHash.F64(h, 12); RenderHash.F64(h, 5);
                            if (m.LabelSeconds >= 0) LabelCanvas(h, ReplayMarkers.Set.CountdownLabel(m.LabelSeconds)); else h.Null();
                            RenderHash.F64(h, m.IconAt.X); RenderHash.F64(h, m.IconAt.Y);
                        }
                        h.Word(markers.TextureOrder.Count);
                        foreach (var icon in markers.TextureOrder)
                        {
                            h.Str(icon.Key); h.Word(icon.W); h.Word(icon.H);
                            gotIcons[setName + "/" + icon.Key] = RenderHash.Hex(c => IconCanvas(c, icon));
                        }
                    }
                    h.Bool(markers.Visible);
                    foreach (var m in markers.Markers)
                    {
                        foreach (double o in m.Opacities) RenderHash.F64(h, o);
                        h.Bool(m.LabelVisible); h.Word(m.LabelSeconds); RenderHash.F64(h, m.LabelOpacity);
                        if (m.LabelSeconds >= 0) LabelCanvas(h, ReplayMarkers.Set.CountdownLabel(m.LabelSeconds)); else h.Null();
                    }
                }
                if (markers.Markers.Count != row.GetProperty("markers").GetInt32()) failures.Add($"{id}: {markers.Markers.Count} markers, oracle {row.GetProperty("markers").GetInt32()}");
                else if (h.Hex() != row.GetProperty("hash").GetString()) failures.Add(id);
            }
            catch (Exception e) { failures.Add($"{id}: {e}"); }
            return styles;
        }, _ => { });
        foreach (var (key, want) in wantIcons)
            if (!gotIcons.TryGetValue(key, out var got) || got != want) failures.Add("picture " + key);
        Assert.True(failures.IsEmpty, $"{rows.Count} solutions; {failures.Count} differ:\n" + string.Join("\n", failures.Take(20)));
    }
}
