using System;
using System.IO;
using System.Net.Http;
using System.Text;
using Godot;
using Lemmix.App.Ui.Pages;
using Rig = Lemmix.App.Test.ShellTests.Rig;

namespace Lemmix.App.Test;

// The level upload server through the app: the setup page's three-way switch starts it (only
// "on always" remembered in the store) and shows where to browse; an upload from a computer
// reloads the library on the frame; off stops it; the next start turns it on only if it was on always.
public static class UploadServerTests
{
    const string Level = "# NeoLemmix level\nTITLE Uploaded level\nAUTHOR Test\nTHEME orig_dirt\nLEMMINGS 1\nSAVE_REQUIREMENT 1\nWIDTH 320\nHEIGHT 160\n";

    static void PressOnPage(VrPage page, string id)
    {
        page.Paint();
        for (float s = 0; page.RegionRect(id) == null && s <= page.ContentHeight; s += page.View.Size.Y / 2) { page.ScrollTo(s); page.Paint(); }
        Check.True(page.RegionRect(id) != null, "the region " + id + " is on the page");
        page.Press(new PagePick(id));
    }

    [AppTest]
    public static void TheSetupPagesSwitchServesTheLevelsFolder()
    {
        string assets = Path.Combine(Path.GetTempPath(), "lemmix-upload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(assets);
        using var rig = new Rig(assets);
        var app = rig.App;
        try
        {
            Check.True(app.UploadServer == null, "off at first");
            app.OpenSetup();
            var page = app.SetupPage;
            Check.Equal(UploadMode.Off, app.UploadState().Mode, "the switch is off by default");
            PressOnPage(page, "upload:session");
            var state = app.UploadState();
            Check.True(state.On && app.UploadServer!.Running, "on for this session starts it");
            Check.Equal("off", app.Store.GetItem(Lemmix.App.Shell.App.UploadServerKey), "not remembered: the next start is off");
            Check.True(page.Upload.On && page.Upload.Mode == UploadMode.Session, "the page shows it on for this session");
            foreach (var url in state.Urls) Check.True(url.StartsWith("http://", StringComparison.Ordinal) && url.EndsWith(":" + app.UploadServer.Port + "/", StringComparison.Ordinal), "an address to type: " + url);

            // a computer uploads a pack's folder, then says it is done
            using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + app.UploadServer.Port + "/") };
            var put = http.PutAsync("api/file?path=" + Uri.EscapeDataString("Uploaded Pack/a.nxlv"), new ByteArrayContent(Encoding.UTF8.GetBytes(Level))).Result;
            Check.True(put.IsSuccessStatusCode, "the upload is taken: " + put.StatusCode);
            var rescan = http.PostAsync("api/rescan?path=&note=" + Uri.EscapeDataString("1 file uploaded from a computer"), null).Result;
            Check.True(rescan.IsSuccessStatusCode, "the rescan is taken");
            // the library reloads on the frame (queued from the server's thread)
            for (int i = 0; i < 50 && app.Tree.ById.Count == 0; i++) { System.Threading.Thread.Sleep(20); rig.Frame(); }
            Check.True(app.Tree.ById.Count == 1, "the uploaded level is in the library (" + app.Tree.ById.Count + ")");
            Check.Equal("1 file uploaded from a computer", app.UploadState().Activity, "what the computer did, for the page");
            Check.True(page.LevelDirs.Count == 1 && page.LevelDirs[0].Dir == "Uploaded Pack", "the setup page lists the folder");

            // the settings files, answered on the frame while the server's thread waits
            T Pumped<T>(System.Threading.Tasks.Task<T> t)
            {
                for (int i = 0; i < 500 && !t.IsCompleted; i++) { rig.Frame(); System.Threading.Thread.Sleep(10); }
                Check.True(t.IsCompleted, "answered within the frames");
                return t.Result;
            }
            app.Store.SetItem(Lemmix.Store.ConfigFiles.ClearedKey, "{\"a\":{\"best\":50,\"clears\":1}}");
            var dl = Pumped(http.GetAsync("api/config?kind=progress"));
            Check.True(dl.IsSuccessStatusCode, "the progress file served: " + dl.StatusCode);
            Check.True(dl.Content.ReadAsStringAsync().Result.Contains("\"best\": 50"), "with the headset's progress");
            var up = Pumped(http.PostAsync("api/config?kind=prefs&name=p.json", new StringContent("{\"format\":\"lemmings-3d-preferences\",\"version\":1,\"values\":{\"lem3d-emboss\":\"off\"}}")));
            string reply = up.Content.ReadAsStringAsync().Result;
            Check.True(reply.Contains("when Lemmix starts again"), "the headset's words for when preferences apply: " + reply);
            Check.Equal("off", app.Store.GetItem("lem3d-emboss"), "the preference stored");

            // on always: the same server keeps running, and the next start turns it on
            var running = app.UploadServer;
            PressOnPage(page, "upload:always");
            Check.True(app.UploadServer == running && app.UploadState().Mode == UploadMode.Always, "on always keeps the running server");
            Check.Equal("always", app.Store.GetItem(Lemmix.App.Shell.App.UploadServerKey), "remembered for the next start");

            // off
            PressOnPage(page, "upload:off");
            Check.True(app.UploadServer == null && !app.UploadState().On, "the switch stops it");
            Check.Equal("off", app.Store.GetItem(Lemmix.App.Shell.App.UploadServerKey), "remembered off");
        }
        finally
        {
            app.SetUploadServer(UploadMode.Off);
            try { Directory.Delete(assets, true); } catch (IOException) { }
        }
    }

    // the next start: off unless the switch was left on always ("on" is the old two-way switch's)
    [AppTest]
    public static void OnlyOnAlwaysTurnsItOnAtTheNextStart()
    {
        string assets = Path.Combine(Path.GetTempPath(), "lemmix-upload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(assets);
        try
        {
            foreach (var (stored, mode) in new[] { ((string?)null, UploadMode.Off), ("off", UploadMode.Off), ("always", UploadMode.Always), ("on", UploadMode.Always) })
            {
                var store = new Lemmix.Store.LocalStore();
                if (stored != null) store.SetItem(Lemmix.App.Shell.App.UploadServerKey, stored);
                using var rig = new Rig(assets, store);
                var app = rig.App;
                try
                {
                    Check.Equal(mode, app.UploadState().Mode, "the switch at start, stored " + (stored ?? "nothing"));
                    Check.Equal(mode == UploadMode.Always, app.UploadServer?.Running == true, "the server at start, stored " + (stored ?? "nothing"));
                }
                finally { app.SetUploadServer(UploadMode.Off); }
            }
        }
        finally { try { Directory.Delete(assets, true); } catch (IOException) { } }
    }
}
