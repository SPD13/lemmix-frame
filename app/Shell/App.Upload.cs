using System;
using System.Collections.Concurrent;
using Godot;
using Lemmix.App.Ui.Pages;
using Lemmix.Setup;

namespace Lemmix.App.Shell;

// The level upload server (LevelServer): the levels do not ship with the app, so a browser on
// another computer of the network can put them in. Its switch is on the setup page and is kept
// in the store (on stays on at the next start); what a computer changed reloads the library on
// the frame (the server's threads queue it, Frame runs it).
public sealed partial class App
{
    public const string UploadServerKey = "lemmix-frame-upload-server";
    public LevelServer? UploadServer { get; private set; }
    string? _uploadError;
    string _uploadActivity = "";
    readonly ConcurrentQueue<Action> _uploadEvents = new();

    public UploadServerState UploadState() => new(
        UploadServer?.Running == true,
        UploadServer?.Running == true ? UploadServer.Urls() : Array.Empty<string>(),
        _uploadError, _uploadActivity);

    /** The setup page's switch: started or stopped, and remembered. */
    public void SetUploadServer(bool on)
    {
        Store.SetItem(UploadServerKey, on ? "on" : "off");
        if (on) StartUploadServer();
        else StopUploadServer();
    }

    // at start: on if the player left it on
    void StartUploadServerIfOn()
    {
        if (Store.GetItem(UploadServerKey) == "on") StartUploadServer();
    }

    void StartUploadServer()
    {
        _uploadError = null;
        if (UploadServer?.Running == true) return;
        var server = new LevelServer(new Installer(AssetRoot));
        server.LevelsChanged += () => _uploadEvents.Enqueue(AfterUpload);
        server.Activity += a => _uploadEvents.Enqueue(() => _uploadActivity = a);
        try
        {
            server.Start();
            UploadServer = server;
            GD.Print("[app] level upload server on: " + string.Join(", ", server.Urls()));
        }
        catch (Exception e)
        {
            _uploadError = "the web server could not start: " + e.Message;
            GD.PushWarning("[app] " + _uploadError);
            server.Dispose();
        }
    }

    void StopUploadServer()
    {
        UploadServer?.Dispose();
        UploadServer = null;
        _uploadError = null;
    }

    // once a frame: what the server's threads queued
    void DrainUploadEvents()
    {
        bool reload = false;
        while (_uploadEvents.TryDequeue(out var a))
        {
            if (a == AfterUpload) reload = true; // a batch of changes reloads once
            else a();
        }
        if (reload) AfterUpload();
    }

    // on the frame, after a computer changed the levels: the library and the setup page anew
    void AfterUpload()
    {
        if (!IsInsideTree()) return;
        ReloadLibrary();
        if (SetupPage.Root.Visible) { SetupPage.Refresh(); SetupPage.Paint(); }
    }
}
