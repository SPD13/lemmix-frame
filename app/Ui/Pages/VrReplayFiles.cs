using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using Lemmix.Engine;

namespace Lemmix.App.Ui.Pages;

// What the replay files window needs of the game (app.js saveReplayFile and the replay-file
// input's change handler).
public interface IReplayFilesBackend
{
    string Folder { get; }               // <user data>/replays
    bool CanSave { get; }                // a Lemmix level is being played
    string LevelName { get; }            // hud.name: what the file is named after
    string? LevelReplayId { get; }       // game.level.info.id: the replay's level check
    string SaveText();                   // Lemmix.Replay.serialize(game.sim, {})
    void Load(ParsedReplay replay, string name);   // game.loadReplayFile: plays from the start
}

// The headset's stand-in for the browser's replay file picker and download: the .nxrp files of
// <user data>/replays, newest first, each with a "load"; "save replay" writes the attempt there
// under the web's file name. A replay that names another level asks first, as the web does.
public sealed class VrReplayFiles : VrPage
{
    public const int PAGE_W = 1100, PAGE_H = 760;
    public readonly IReplayFilesBackend Backend;
    public readonly IPageConfirm Confirm;
    public List<ShelfFile> Files = new();
    public string Message = "";
    public bool MessageBad;

    public VrReplayFiles(IReplayFilesBackend backend, IPageConfirm confirm) : base("replays", PAGE_W, PAGE_H, 1f)
    {
        Backend = backend;
        Confirm = confirm;
    }

    public override void Opened() { Refresh(); Paint(); }

    public void Refresh()
    {
        Files = new PageFiles(Path.GetTempPath()).List(Backend.Folder, ".nxrp").OrderByDescending(f => f.Modified).ToList();
    }

    void Say(string text, bool bad = false) { Message = text; MessageBad = bad; }

    /** saveReplayFile's name: the level's name, safe for a file, ".nxrp"; a name taken gets a number. */
    public static string FileName(string levelName, Func<string, bool> taken)
    {
        string b = Regex.Replace(levelName.Trim(), @"[^\w.-]+", "_", RegexOptions.ECMAScript);
        if (b.Length > 60) b = b[..60];
        if (b == "") b = "level";
        string name = b + ".nxrp";
        for (int i = 1; taken(name); i++) name = b + " (" + i + ").nxrp";
        return name;
    }

    public string? Save()
    {
        if (!Backend.CanSave) { Say("no level is being played", true); return null; }
        try
        {
            Directory.CreateDirectory(Backend.Folder);
            string name = FileName(Backend.LevelName, n => File.Exists(Path.Combine(Backend.Folder, n)));
            File.WriteAllText(Path.Combine(Backend.Folder, name), Backend.SaveText());
            Say("saved as " + name);
            Refresh();
            return name;
        }
        catch (Exception e) { Say("save failed: " + e.Message, true); return null; }
    }

    public void Load(string path)
    {
        string name = Path.GetFileName(path);
        ParsedReplay parsed;
        try { parsed = Replay.Parse(File.ReadAllText(path)); }
        catch (Exception e) { Say(name + ": not a replay file (" + e.Message + ")", true); return; }
        void Go() { Backend.Load(parsed, name); Closed?.Invoke(); }
        string own = (Backend.LevelReplayId ?? "").ToUpperInvariant(), theirs = (parsed.Meta.Id ?? "").ToUpperInvariant();
        if (theirs != "" && own != "" && theirs != own)
            Confirm.Ask("Replay from another level?", "load it anyway",
                "This replay appears to be from a different level. Whatever it does here will make little sense.", Go);
        else Go();
    }

    protected override void OnPress(string id)
    {
        if (id == "save") { Save(); return; }
        if (id.StartsWith("load:", StringComparison.Ordinal)) { Load(id[5..]); return; }
    }

    // ---- painting: the VR windows' look
    protected override void PaintPage()
    {
        float pad = 28;
        cx.font = "bold 34px monospace";
        cx.fillStyle = "#f0f3f8";
        cx.fillText("REPLAYS", pad, 54);
        // save replay, left of the window's close
        string save = "save replay";
        cx.font = "22px monospace";
        float sw = cx.measureText(save).width + 40, sx = W - pad - 80 - sw;
        cx.font = "18px monospace";
        cx.fillStyle = "#8fa1bb";
        cx.fillText(PageText.Fit(cx, Backend.Folder, sx - 16 - (pad + 170)), pad + 170, 56);
        bool can = Backend.CanSave, hot = can && Hot("save");
        cx.globalAlpha = can ? 1 : 0.4f;
        cx.fillStyle = hot ? "#2b3548" : "#19202c";
        cx.beginPath();
        cx.roundRect(sx, 30, sw, 46, 10);
        cx.fill();
        cx.strokeStyle = hot ? "#ffffff" : "#33405a";
        cx.lineWidth = 2;
        cx.stroke();
        cx.fillStyle = "#f0f3f8";
        cx.textAlign = "center";
        cx.fillText(save, sx + sw / 2, 53);
        cx.textAlign = "left";
        cx.globalAlpha = 1;
        Hit("save", sx, 30, sw, 46, can);
        float y = 96;
        if (Message != "")
        {
            cx.font = "20px monospace";
            cx.fillStyle = MessageBad ? "#e07a6a" : "#6fce7e";
            cx.fillText(PageText.Fit(cx, Message, W - 2 * pad), pad, y + 4);
        }
        y += 24;
        View = new Rect2(pad - 4, y, W - 2 * pad + 8 - 30, H - y - pad);
        BeginScroll();
        float rh = 64;
        if (Files.Count == 0)
        {
            cx.font = "22px monospace";
            cx.fillStyle = "#8fa1bb";
            cx.fillText("no replay saved yet - save one, or copy .nxrp files into the folder", pad, 30);
        }
        for (int i = 0; i < Files.Count; i++)
        {
            float ry = i * rh;
            if (!InView(ry, rh)) continue;
            var f = Files[i];
            string id = "load:" + f.Path;
            bool rowHot = Hot(id);
            cx.fillStyle = rowHot ? "#2b3548" : "#19202c";
            cx.beginPath();
            cx.roundRect(pad, ry + 4, W - 2 * pad - 30, rh - 10, 10);
            cx.fill();
            if (rowHot) { cx.strokeStyle = "#ffffff"; cx.lineWidth = 3; cx.stroke(); }
            float mid = ry + 4 + (rh - 10) / 2;
            cx.font = "bold 22px monospace";
            cx.fillStyle = "#f0f3f8";
            cx.fillText(PageText.Fit(cx, f.Name, W - 2 * pad - 520), pad + 16, mid);
            cx.font = "18px monospace";
            cx.fillStyle = "#8fa1bb";
            cx.textAlign = "right";
            cx.fillText(PageText.Mb(f.Size) + " · " + PageText.Date(f.Modified), W - pad - 150, mid);
            cx.fillStyle = "#6fce7e";
            cx.font = "bold 20px monospace";
            cx.fillText("load ▶", W - pad - 50, mid);
            cx.textAlign = "left";
            Hit(id, pad, ry + 4, W - 2 * pad - 30, rh - 10);
        }
        EndScroll(Files.Count * rh + 8);
    }
}
