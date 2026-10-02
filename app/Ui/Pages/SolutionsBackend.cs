using System;
using System.Collections.Generic;
using System.Linq;
using Lemmix.Library;
using Lemmix.Store;

namespace Lemmix.App.Ui.Pages;

// a Lemmix level as solutions.js lists it: where it lives, its number there, its title
public sealed record SolutionLevel(string Id, string Title, IReadOnlyList<string> Where, string Folder, string Pack, int Ordinal, int Order, string Lemmings, string Save);

// a solution's worth (solutions/index.json's record of a solved level)
public sealed record SolutionInfo(int Saved, int Count, int Needed, int SkillsUsed, int CompletionFrame, int Tier, double ElapsedMs);

// What the solutions page reads and does (solutions.js over LevelTree and Solutions).
public interface ISolutionsBackend
{
    IReadOnlyList<SolutionLevel> Levels();
    SolutionInfo? Info(string id);
    int TriedTier(string id);       // the index's tier for a level, solved or not; 0 when never tried
    bool NotFound(string id);       // no solution after the widest tier (3)
    void Play(string id, bool solution);   // "play level", "▶ play solution": the level, its solution replaying
    void Back();                    // "‹ Back to the game"
}

// The native backend: the level tree and the solutions index's text. Core's Solutions answers
// solved levels only; an unsolved level's tier (the "tried at" column, "not found") is read from
// the same index here.
public sealed class SolutionsBackend : ISolutionsBackend
{
    readonly List<SolutionLevel> _levels = new();
    readonly Solutions _solutions = new();
    readonly Dictionary<string, (string Status, int Tier)> _tried = new(StringComparer.Ordinal);
    readonly Action<string, bool> _play;
    readonly Action _back;

    public SolutionsBackend(LevelTree tree, string? solutionsIndexJson, Action<string, bool> play, Action back)
    {
        _play = play;
        _back = back;
        _solutions.Load(solutionsIndexJson);
        try
        {
            if (solutionsIndexJson != null && Js.Get(JsJson.Parse(solutionsIndexJson), "levels") is JsObject lv)
                foreach (var id in lv.Keys)
                {
                    var r = lv.Get(id);
                    _tried[id] = (Js.Get(r, "status") as string ?? "", Js.Get(r, "tier") is double t ? (int)t : 0);
                }
        }
        catch (JsSyntaxError) { }
        int order = 0;
        foreach (var (id, hit) in tree.ById)
        {
            if (hit.Node.Engine != "lemmix") continue;
            var where = new List<string>();
            for (var n = hit.Node; n != null && n.Parent != null; n = n.Parent) where.Insert(0, n.Name ?? "");
            string folder = where.Count > 0 ? where[0] : "";
            static string Num(object? v) => v is double d ? Js.NumberToString(d) : v is string s ? s : "";
            _levels.Add(new SolutionLevel(id, Js.Truthy(hit.Level.Title) ? Js.ToStr(hit.Level.Title) : "", where, folder,
                hit.Pack?.Name ?? folder, hit.Node.Levels.IndexOf(hit.Level) + 1, order++,
                Num(hit.Level.Raw.Get("lemmings")), Num(hit.Level.Raw.Get("save"))));
        }
    }

    public IReadOnlyList<SolutionLevel> Levels() => _levels;

    public SolutionInfo? Info(string id)
    {
        var r = _solutions.Info(id);
        if (r == null) return null;
        int I(string k) => Js.Get(r, k) is double d ? (int)d : 0;
        return new SolutionInfo(I("saved"), I("count"), I("needed"), I("skillsUsed"), I("completionFrame"), I("tier"), Js.Get(r, "elapsedMs") is double e ? e : 0);
    }

    public int TriedTier(string id) => _tried.TryGetValue(id, out var t) ? t.Tier : 0;
    public bool NotFound(string id) => _tried.TryGetValue(id, out var t) && t.Status != "solved" && t.Tier >= 3;
    public void Play(string id, bool solution) => _play(id, solution);
    public void Back() => _back();
}
