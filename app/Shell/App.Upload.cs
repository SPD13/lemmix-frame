using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Lemmix.App.Ui.Pages;
using Lemmix.Setup;
using Lemmix.Store;

namespace Lemmix.App.Shell;

// The level upload server (LevelServer): the levels do not ship with the app, so a browser on
// another computer of the network can put them in. Its switch is on the setup page and is kept
// in the store (on stays on at the next start); what a computer changed reloads the library on
// the frame (the server's threads queue it, Frame runs it). The settings files (controls,
// preferences, progress) are saved and read back through it too, by the setup backend on the
// frame while the server's thread waits.
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
        server.ExportConfig = kind => OnFrame(() => SetupPage.Backend.Export(kind));
        server.ImportConfig = (kind, text, name) => OnFrame(() => ImportConfig(kind, text, name));
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

    // a call from a server thread, run on the frame; the thread waits for its answer
    T OnFrame<T>(Func<T> f)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uploadEvents.Enqueue(() =>
        {
            try { done.SetResult(f()); }
            catch (Exception e) { done.SetException(e); }
        });
        if (!done.Task.Wait(10_000)) throw new IOException("the app did not answer");
        return done.Task.Result;
    }

    // a settings file from a computer: read in as the setup page did, then shown at once
    ConfigMessage ImportConfig(string kind, string text, string name)
    {
        var m = SetupPage.Backend.Import(kind, text, name);
        if (m.Bad) return m;
        if (kind == "progress") ReloadLibrary();       // the catalog's cleared marks and best times
        if (kind == "controls") RefreshKeyHints();
        // the 3D effects are read at start (as the web's page reload)
        return m with { Text = m.Text.Replace("when the game page reloads", "when Lemmix starts again", StringComparison.Ordinal) };
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
