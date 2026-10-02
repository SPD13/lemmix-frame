using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Lemmix.App.Ui.Windows;

namespace Lemmix.App.Ui.Pages;

// A page's question before it does something that cannot be undone (setup.js askConfirm: a
// title, the verb on the yes, a line of explanation): `yes` runs when the player says yes, nothing
// happens on a no. The headset's answer is the windows' question (VrModal through
// VrWindows.AskConfirm: WindowsPageConfirm); tests answer it themselves.
public interface IPageConfirm
{
    void Ask(string title, string verb, string body, Action yes);
}

// a file the player put in a folder the app reads (the import folder, the replays)
public sealed record ShelfFile(string Name, string Path, long Size, long Modified);

// The folders the native app reads and writes in place of the browser's file pickers and
// downloads: <user data>/import (zips and configuration files the player copies onto the
// headset), <user data>/export (the configuration files it saves).
public interface IPageFiles
{
    string ImportFolder { get; }
    string ExportFolder { get; }
    IReadOnlyList<ShelfFile> List(string folder, string extension);
    string ReadText(string path);
    string WriteText(string folder, string name, string text);  // the path written
}

public sealed class PageFiles : IPageFiles
{
    public string ImportFolder { get; }
    public string ExportFolder { get; }

    public PageFiles(string userData)
    {
        ImportFolder = Path.Combine(userData, "import");
        ExportFolder = Path.Combine(userData, "export");
    }

    public IReadOnlyList<ShelfFile> List(string folder, string extension)
    {
        if (!Directory.Exists(folder)) return Array.Empty<ShelfFile>();
        return Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith('.'))
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ShelfFile(f.Name, f.FullName, f.Length, new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds()))
            .ToList();
    }

    public string ReadText(string path) => File.ReadAllText(path);

    public string WriteText(string folder, string name, string text)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true);
        return path;
    }
}

// The pages' questions as the windows' own: the VrModal in front of the eyes, its yes and no the
// only things the ray can hit while it is up. The modal is one line of title and one of body at
// its size; PaintModal fits the page's longer texts into it (smaller type, the body wrapped), and
// the yes runs the action.
public sealed class WindowsPageConfirm : IPageConfirm
{
    readonly VrWindows _windows;
    public WindowsPageConfirm(VrWindows windows) { _windows = windows; }

    public void Ask(string title, string verb, string body, Action yes) =>
        _windows.AskConfirm(title, yes, m => PaintModal(m, title, verb, body));

    /** The question on the modal's 512 x 192 canvas, in its colours: title, body, "yes: <verb>". */
    public static void PaintModal(VrModal m, string title, string verb, string body)
    {
        m.Title = title;
        m.Body = body;
        var cx = m.Panel.Canvas;
        cx.clearRect(0, 0, VrModal.W, VrModal.H);
        cx.fillStyle = "rgba(10, 14, 22, 0.95)";
        cx.beginPath();
        cx.roundRect(2, 2, 508, 188, 16);
        cx.fill();
        cx.strokeStyle = "#ffd866";
        cx.lineWidth = 4;
        cx.stroke();
        cx.textAlign = "center";
        cx.textBaseline = "alphabetic";
        cx.fillStyle = "#f0f3f8";
        cx.font = "bold 30px monospace";
        cx.fillText(title, 256, 50, 470);
        cx.fillStyle = "#8fa1bb";
        cx.font = "19px monospace";
        var lines = PageText.Flow(cx, new[] { new Run(body, "19px monospace", "#8fa1bb") }, 470);
        int n = Math.Min(lines.Lines, 4);
        float top = 66 + (4 - n) * 11;
        foreach (var part in lines.Parts)
        {
            if (part.Line >= 4) continue;
            // each line centred: the line's parts are one run here
            cx.fillText(part.Text.TrimEnd(), 256, top + part.Line * 23 + 17);
        }
        cx.fillStyle = "#ffd866";
        cx.font = "bold 18px monospace";
        cx.fillText("yes: " + verb, 256, 178);
        m.Panel.Commit();
    }
}
