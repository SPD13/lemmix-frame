using System;
using System.IO;
using System.Net.Http;
using System.Text;
using Godot;
using Lemmix.App.Ui.Pages;
using Rig = Lemmix.App.Test.ShellTests.Rig;

namespace Lemmix.App.Test;

// The level upload server through the app: the setup page's switch starts it (remembered in the
// store) and shows where to browse; an upload from a computer reloads the library on the frame;
// off stops it.
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
            PressOnPage(page, "upload-server");
            var state = app.UploadState();
            Check.True(state.On && app.UploadServer!.Running, "the switch starts it");
            Check.Equal("on", app.Store.GetItem(Lemmix.App.Shell.App.UploadServerKey), "remembered");
            Check.True(page.Upload.On, "the page shows it on");
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

            // off
            PressOnPage(page, "upload-server");
            Check.True(app.UploadServer == null && !app.UploadState().On, "the switch stops it");
            Check.Equal("off", app.Store.GetItem(Lemmix.App.Shell.App.UploadServerKey), "remembered off");
        }
        finally
        {
            app.SetUploadServer(false);
            try { Directory.Delete(assets, true); } catch (IOException) { }
        }
    }
}
