using System.Globalization;
using System.Text;
using Lemmix.Parse;
using Lemmix.Util;

namespace Lemmix.Engine;

public sealed record ReplayMeta(string Title, string Author, string Game, string Group, int Level, string Id, string User, int CompletionFrame);
public sealed record ReplayAssignment(int Frame, string Skill, int LemIndex, string LemId, int X, int Y, int Dx);
public sealed record ReplaySpawnInterval(int Frame, int Interval, int Spawned);
public sealed record ReplayNuke(int Frame);
public sealed record ParsedReplay(ReplayMeta Meta, List<ReplayAssignment> Assignments, List<ReplaySpawnInterval> SpawnIntervals, List<ReplayNuke> Nukes);

public sealed class ReplayExtra
{
    public string? Author, Game, Group, User;
    public int? Level;
}

// web/lemmix/js/replay.js - NeoLemmix replays (.nxrp), as LemReplay.pas reads and writes them.
public static class Replay
{
    public static ParsedReplay Parse(string text)
    {
        var nx = NxParser.Parse(text);
        var meta = new ReplayMeta(
            StyleManager.Or(nx.Get("TITLE")), StyleManager.Or(nx.Get("AUTHOR")), StyleManager.Or(nx.Get("GAME")),
            StyleManager.Or(nx.Get("GROUP")), nx.Int("LEVEL", 0), StyleManager.Or(nx.Get("ID")), StyleManager.Or(nx.Get("USER")),
            nx.Int("COMPLETION_FRAME", 0));
        var assignments = nx.SectionsNamed("ASSIGNMENT").Select(s => new ReplayAssignment(
            s.Int("FRAME", 0),
            JsString.Upper(JsString.Trim(StyleManager.Or(s.Get("ACTION")))),
            s.Has("LEM_INDEX") ? s.Int("LEM_INDEX", -1) : -1,
            JsString.Upper(JsString.Trim(StyleManager.Or(s.Get("LEM_IDENTIFIER")))),
            s.Int("LEM_X", 0), s.Int("LEM_Y", 0),
            StyleManager.Lower(s.Get("LEM_DIR")).StartsWith('l') ? -1 : 1)).ToList();
        var spawnIntervals = nx.SectionsNamed("SPAWN_INTERVAL").Select(s => new ReplaySpawnInterval(
            s.Int("FRAME", 0),
            s.Has("INTERVAL") ? s.Int("INTERVAL", 53) : s.Int("RATE", 53),
            s.Int("SPAWNED", 0))).ToList();
        var nukes = nx.SectionsNamed("NUKE").Select(s => new ReplayNuke(s.Int("FRAME", 0))).ToList();
        return new ParsedReplay(meta, assignments, spawnIntervals, nukes);
    }

    // The .nxrp text for what `sim` recorded on its level.
    public static string Serialize(LemGame sim, ReplayExtra? extra = null)
    {
        var info = sim.Level.Info;
        extra ??= new ReplayExtra();
        var lines = new List<string>();
        void Line(string k, string? v) { if (!string.IsNullOrEmpty(v)) lines.Add(k + " " + v); }
        string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        Line("TITLE", info.Title);
        Line("AUTHOR", extra.Author ?? info.Author);
        Line("GAME", extra.Game);
        Line("GROUP", extra.Group);
        Line("LEVEL", extra.Level is int l ? I(l) : null);
        Line("ID", info.Id);
        Line("USER", StyleManager.Or(extra.User, "LemmingsJS"));
        if (sim.LemmingsIn >= sim.Level.NeedCount && sim.LemmingsIn > 0) Line("COMPLETION_FRAME", I(sim.CurrentIteration));
        lines.Add("");
        foreach (var r in sim.Recorded)
        {
            if (r.Type == "assignment")
                lines.AddRange(new[] { "$ASSIGNMENT", "  FRAME " + I(r.Frame), "  LEM_INDEX " + I(r.LemIndex), "  LEM_IDENTIFIER " + r.LemId,
                    "  LEM_X " + I(r.X), "  LEM_Y " + I(r.Y), "  LEM_DIR " + (r.Dx < 0 ? "left" : "right"), "  ACTION " + r.Skill, "$END", "" });
            else if (r.Type == "spawn_interval")
                lines.AddRange(new[] { "$SPAWN_INTERVAL", "  FRAME " + I(r.Frame), "  RATE " + I(r.Interval), "  SPAWNED " + I(r.Spawned), "$END", "" });
            else if (r.Type == "nuke")
                lines.AddRange(new[] { "$NUKE", "  FRAME " + I(r.Frame), "$END", "" });
        }
        return string.Join("\n", lines);
    }
}
