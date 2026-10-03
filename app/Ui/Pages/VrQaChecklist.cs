using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace Lemmix.App.Ui.Pages;

// The device sessions' checklist (docs/device-session-1.md): what only the headset can tell -
// the controls, the windows, the look, the feel - each marked pass or fail with an optional note
// typed on the VR keyboard, saved at once to user://qa.json for tools/frame-pull-report.sh. Not a
// web page: the web version is checked in a browser; this is how the native one is checked.
public sealed class VrQaChecklist : VrPage
{
    public const int PAGE_W = 1100, PAGE_H = 900;
    public const string FilePath = "user://qa.json";

    public sealed class Item
    {
        public required string Id, Section, Text;
        public string State = "";   // "", "pass", "fail"
        public string Note = "";
    }

    public static readonly (string Section, string Id, string Text)[] Checks =
    {
        ("Setup", "setup-install", "NeoLemmix, the styles and Lemmings Plus install from the setup window; progress and messages read like the web's"),
        ("Setup", "setup-first-run", "a first run with nothing installed opens the setup window"),
        ("Play", "play-beam", "the beam points where the hand points; the dot lands where expected"),
        ("Play", "play-assign", "a trigger on a lemming gives it the selected skill; the cursor shows the square over a lemming"),
        ("Play", "play-panel", "skill bar presses: left, and right/middle on frame skip; release rate holds; nuke needs two presses"),
        ("Play", "play-grip", "grip drags the board; both grips scale it; trigger-drag moves it"),
        ("Play", "play-sticks", "thumbsticks: pan, tilt, dolly; A recentres"),
        ("Play", "play-toolbar", "toolbar: pause, restart, prev/next, worlds, solution, mute/volume, lock/park/move"),
        ("Play", "play-levels", "one level of each Lemmings Plus difficulty and one Intro pack level play through"),
        ("Replay", "replay-solution", "watch a solution: it plays, markers stand on the board, a win records nothing"),
        ("Replay", "replay-rewind", "frame back/forward, rewind; save/load state; replay insert"),
        ("Replay", "replay-files", "save a replay, load it back from the replay list"),
        ("Windows", "win-catalog", "catalog: stick scrolling, favourites, recent, search with the VR keyboard"),
        ("Windows", "win-settings", "settings: each of the 8 effects switches as it does on the web"),
        ("Windows", "win-pages", "level text, controls dialog, setup page read well"),
        ("Look", "look-board", "the board looks like the web's diorama: terrain, doors, water, lemmings, room"),
        ("Look", "look-legibility", "windows and the skill bar are sharp enough to read at their distance"),
        ("Feel", "feel-comfort", "board size and distance are comfortable; nothing strains the eyes"),
        ("Feel", "feel-smooth", "no stutter while playing, digging, at x8, or while rewinding"),
        ("Audio", "audio-sfx", "sound effects come from where they happen on the board"),
        ("Audio", "audio-music", "level music plays and loops; mute and volume act on it"),
    };

    public readonly List<Item> Items;
    public string AppVersion = "";

    public VrQaChecklist() : base("qa", PAGE_W, PAGE_H, 1.6f)
    {
        Items = Checks.Select(c => new Item { Section = c.Section, Id = c.Id, Text = c.Text }).ToList();
        Load();
    }

    void Load()
    {
        if (!FileAccess.FileExists(FilePath)) return;
        try
        {
            var root = JsonNode.Parse(FileAccess.GetFileAsString(FilePath))?.AsObject();
            if (root?["items"] is not JsonArray arr) return;
            foreach (var n in arr)
            {
                var it = Items.FirstOrDefault(i => i.Id == n?["id"]?.GetValue<string>());
                if (it == null) continue;
                it.State = n!["state"]?.GetValue<string>() ?? "";
                it.Note = n["note"]?.GetValue<string>() ?? "";
            }
        }
        catch (Exception e) { GD.PushWarning("[qa] " + FilePath + " unreadable: " + e.Message); }
    }

    public void Save()
    {
        var root = new JsonObject
        {
            ["version"] = AppVersion,
            ["saved"] = DateTime.UtcNow.ToString("o"),
            ["passed"] = Items.Count(i => i.State == "pass"),
            ["failed"] = Items.Count(i => i.State == "fail"),
            ["open"] = Items.Count(i => i.State == ""),
            ["items"] = new JsonArray(Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id, ["section"] = i.Section, ["text"] = i.Text, ["state"] = i.State, ["note"] = i.Note,
            }).ToArray()),
        };
        using var f = FileAccess.Open(FilePath, FileAccess.ModeFlags.Write);
        f?.StoreString(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    protected override void OnPress(string id)
    {
        int sep = id.IndexOf(':');
        if (sep < 0) return;
        string what = id[..sep];
        var item = Items.FirstOrDefault(i => i.Id == id[(sep + 1)..]);
        if (item == null) return;
        switch (what)
        {
            case "pass": item.State = item.State == "pass" ? "" : "pass"; break;
            case "fail": item.State = item.State == "fail" ? "" : "fail"; break;
            case "note": Edit("note:" + item.Id, item.Note); return;
        }
        Save();
        Paint();
    }

    public override void TextChanged(string field, string text)
    {
        if (!field.StartsWith("note:", StringComparison.Ordinal)) return;
        var item = Items.FirstOrDefault(i => i.Id == field[5..]);
        if (item == null) return;
        item.Note = text;
        Save();
        Paint();
    }

    protected override void PaintPage()
    {
        float x0 = U(20);
        cx.fillStyle = Css.Green;
        cx.font = Font(18, true);
        cx.fillText("QA checklist", x0, U(26));
        cx.fillStyle = Css.Dim;
        cx.font = Font(11);
        int pass = Items.Count(i => i.State == "pass"), fail = Items.Count(i => i.State == "fail");
        cx.fillText($"{pass} passed · {fail} failed · {Items.Count - pass - fail} to check · saved to qa.json", x0 + U(170), U(27));
        View = new Rect2(x0, U(50), W - x0 - U(40), H - U(70));
        BeginScroll();
        float y = 0, rowH = U(46);
        string? section = null;
        foreach (var it in Items)
        {
            if (it.Section != section)
            {
                section = it.Section;
                if (InView(y, U(26))) { cx.fillStyle = Css.Yellow; cx.font = Font(13, true); cx.fillText(section, 0, y + U(13)); }
                y += U(26);
            }
            if (InView(y, rowH))
            {
                cx.fillStyle = Css.Row;
                cx.beginPath(); cx.roundRect(0, y, View.Size.X - U(24), rowH - U(4), U(4)); cx.fill();
                float bx = U(8);
                bx += Button("pass:" + it.Id, "pass", bx, y + U(8), true, it.State == "pass" ? "on" : "") + U(6);
                bx += Button("fail:" + it.Id, "fail", bx, y + U(8), true, it.State == "fail" ? "warn" : "") + U(6);
                bx += Button("note:" + it.Id, it.Note == "" ? "note…" : "edit note", bx, y + U(8), true, "bare") + U(10);
                cx.fillStyle = it.State == "pass" ? Css.Green : it.State == "fail" ? Css.Red : Css.Text;
                cx.font = Font(11);
                cx.fillText(it.Text, bx, y + U(it.Note == "" ? 20 : 13), View.Size.X - bx - U(30));
                if (it.Note != "")
                {
                    cx.fillStyle = Css.Dim;
                    cx.fillText("note: " + it.Note, bx, y + U(30), View.Size.X - bx - U(30));
                }
            }
            y += rowH;
        }
        EndScroll(y);
    }
}
