using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Lemmix.Input;
using Lemmix.Setup;
using Lemmix.Store;

namespace Lemmix.Tests.Setup;

// The level upload server, over HTTP on this machine: the page, the listing, uploads into nested
// folders, paths that would leave the levels folder, deletes, zips installed, the library told.
public class LevelServerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "lemmix-server-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Installer _installer;
    readonly LevelServer _server;
    readonly HttpClient _http = new();
    int _changed;
    readonly List<string> _activity = new();

    const string Level = "# NeoLemmix level\nTITLE A test level\nAUTHOR Test\nTHEME orig_dirt\nLEMMINGS 1\nSAVE_REQUIREMENT 1\nWIDTH 320\nHEIGHT 160\n";

    public LevelServerTests()
    {
        _installer = new Installer(_root);
        _server = new LevelServer(_installer);
        _server.LevelsChanged += () => Interlocked.Increment(ref _changed);
        _server.Activity += a => { lock (_activity) _activity.Add(a); };
        _server.Start(FreePort());
        _http.BaseAddress = new Uri("http://127.0.0.1:" + _server.Port + "/");
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static string Q(string p) => Uri.EscapeDataString(p);

    async Task<HttpResponseMessage> Put(string path, byte[] body) =>
        await _http.PutAsync("api/file?path=" + Q(path), new ByteArrayContent(body), TestContext.Current.CancellationToken);

    async Task<JsonObject> Json(HttpResponseMessage r) =>
        (JsonObject)JsonNode.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

    async Task<JsonObject> List(string path = "")
    {
        var r = await _http.GetAsync("api/list?path=" + Q(path), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    [Fact]
    public async Task ServesThePage()
    {
        var r = await _http.GetAsync("", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/html", r.Content.Headers.ContentType!.ToString());
        string html = await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("LEVELS ON THE HEADSET", html);
        Assert.Contains("webkitdirectory", html);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("nope", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task UploadsLandInNestedFoldersAndList()
    {
        var bytes = Encoding.UTF8.GetBytes(Level);
        Assert.Equal(HttpStatusCode.OK, (await Put("My Pack/Fun/01 First é.nxlv", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put("My Pack/info.nxmi", Encoding.UTF8.GetBytes("TITLE My Pack\n"))).StatusCode);
        Assert.Equal(Level, File.ReadAllText(Path.Combine(_root, "levels", "My Pack", "Fun", "01 First é.nxlv")));
        // no part file left beside it
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "levels", "My Pack", "Fun"), ".*"));

        var top = await List();
        var dir = Assert.Single(top["dirs"]!.AsArray());
        Assert.Equal("My Pack", (string)dir!["name"]!);
        Assert.Equal(2, (long)dir["files"]!);
        Assert.Equal(bytes.Length + 14, (long)dir["bytes"]!);
        var fun = await List("My Pack/Fun");
        Assert.Equal("My Pack/Fun", (string)fun["path"]!);
        Assert.Equal("01 First é.nxlv", (string)fun["files"]![0]!["name"]!);

        // a second upload replaces the first
        Assert.Equal(HttpStatusCode.OK, (await Put("My Pack/info.nxmi", Encoding.UTF8.GetBytes("TITLE Renamed\n"))).StatusCode);
        Assert.Equal("TITLE Renamed\n", File.ReadAllText(Path.Combine(_root, "levels", "My Pack", "info.nxmi")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData(".hidden/x.txt")]
    [InlineData("a/.x")]
    [InlineData("a\\..\\..\\x")]
    [InlineData("C:/x")]
    [InlineData("")]
    public async Task PathsOutsideTheLevelsFolderAreRefused(string path)
    {
        var r = await Put(path, new byte[] { 1 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.Null(_server.Resolve(path == "" ? "x/../.." : path));
    }

    [Fact]
    public async Task AFileWhereAFolderIsAndTheOtherWayAroundAreRefused()
    {
        await Put("a/b.txt", new byte[] { 1 });
        Assert.Equal(HttpStatusCode.Conflict, (await Put("a", new byte[] { 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Put("a/b.txt/c.txt", new byte[] { 1 })).StatusCode);
    }

    [Fact]
    public async Task DeletingAFolderRemovesItAndTellsTheLibrary()
    {
        await Put("Pack/a.nxlv", Encoding.UTF8.GetBytes(Level));
        await Put("Pack/Sub/b.nxlv", Encoding.UTF8.GetBytes(Level));
        int before = _changed;
        var r = await _http.DeleteAsync("api/entry?path=" + Q("Pack/Sub"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "levels", "Pack", "Sub")));
        Assert.True(File.Exists(Path.Combine(_root, "levels", "Pack", "a.nxlv")));
        r = await _http.DeleteAsync("api/entry?path=Pack", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "levels", "Pack")));
        Assert.Equal(before + 2, _changed);
        Assert.Contains("Pack deleted from a computer", _activity);
        Assert.True(File.Exists(Path.Combine(_root, "levels", "index.json")), "the index rebuilt");
        // the app's own index: not listed, not to be replaced or deleted
        Assert.Empty((await List())["files"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.DeleteAsync("api/entry?path=index.json", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put("index.json", new byte[] { 1 })).StatusCode);
        Assert.True(File.Exists(Path.Combine(_root, "levels", "index.json")));
        // not the folder itself, not what is not there
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.DeleteAsync("api/entry?path=", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync("api/entry?path=Gone", TestContext.Current.CancellationToken)).StatusCode);
    }

    static byte[] PackZip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
            Add("Zipped Pack/levels.nxmi", "TITLE Zipped Pack\n");
            Add("Zipped Pack/Fun/a.nxlv", Level);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task AnUploadedZipInstallsAsTheSetupPageWould()
    {
        Assert.Equal(HttpStatusCode.OK, (await Put("pack.zip", PackZip())).StatusCode);
        var plan = await Json(await _http.PostAsync("api/plan?path=pack.zip", null, TestContext.Current.CancellationToken));
        Assert.Equal("levels", (string)plan["kind"]!);
        Assert.Equal("Zipped Pack", (string)plan["dirs"]![0]!);
        Assert.Empty(plan["clashing"]!.AsArray());

        int before = _changed;
        var r = await _http.PostAsync("api/install?path=pack.zip", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Zipped Pack", (string)(await Json(r))["installed"]!);
        Assert.True(File.Exists(Path.Combine(_root, "levels", "Zipped Pack", "Fun", "a.nxlv")));
        Assert.False(File.Exists(Path.Combine(_root, "levels", "pack.zip")), "the zip removed");
        Assert.Equal(before + 1, _changed);
        Assert.Contains(_installer.Units().Keys, k => k == "levels/Zipped Pack");

        // again: it now clashes with what is installed
        await Put("pack.zip", PackZip());
        plan = await Json(await _http.PostAsync("api/plan?path=pack.zip", null, TestContext.Current.CancellationToken));
        Assert.Equal("Zipped Pack", (string)plan["clashing"]![0]!);
        // a file that is not a zip of anything known
        await Put("junk.zip", Encoding.UTF8.GetBytes("not a zip"));
        Assert.NotEqual(HttpStatusCode.OK, (await _http.PostAsync("api/install?path=junk.zip", null, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsync("api/install?path=" + Q("Zipped Pack/Fun/a.nxlv"), null, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task ARescanAfterABatchRebuildsTheIndexAndSays()
    {
        await Put("Batch/a.nxlv", Encoding.UTF8.GetBytes(Level));
        int before = _changed;
        var r = await _http.PostAsync("api/rescan?path=&note=" + Q("1 file uploaded from a computer"), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(before + 1, _changed);
        Assert.Contains("1 file uploaded from a computer", _activity);
        Assert.Contains("Batch", File.ReadAllText(Path.Combine(_root, "levels", "index.json")));
    }

    // the settings files through the app's export and import (here: a store and a key table)
    (LocalStore Store, HotkeyManager Keys) ServeConfig()
    {
        var store = new LocalStore();
        var keys = new HotkeyManager(store);
        _server.ExportConfig = kind => kind switch
        {
            "controls" => ConfigFiles.ExportControls(keys),
            "prefs" => ConfigFiles.ExportPrefs(store),
            _ => ConfigFiles.ExportProgress(store),
        };
        _server.ImportConfig = (kind, text, name) => kind switch
        {
            "controls" => ConfigFiles.ImportControls(keys, text, name),
            "prefs" => ConfigFiles.ImportPrefs(store, text, name),
            _ => ConfigFiles.ImportProgress(store, text, name),
        };
        return (store, keys);
    }

    async Task<JsonObject> PostConfig(string kind, string name, string text) =>
        await Json(await _http.PostAsync("api/config?kind=" + kind + "&name=" + Q(name), new StringContent(text), TestContext.Current.CancellationToken));

    [Fact]
    public async Task SettingsFilesDownloadAndComeBack()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("api/config?kind=progress", ct)).StatusCode);
        var (store, keys) = ServeConfig();
        store.SetItem(ConfigFiles.ClearedKey, "{\"a\":{\"best\":50,\"clears\":1}}");
        // download: the web's file, under its name
        var r = await _http.GetAsync("api/config?kind=progress", ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("lemmings-3d-progress.json", r.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Contains("\"best\": 50", await r.Content.ReadAsStringAsync(ct));
        // upload: progress merges, the best of both
        var m = await PostConfig("progress", "backup.json", "{\"format\":\"lemmings-3d-progress\",\"version\":1,\"cleared\":{\"a\":{\"best\":40,\"clears\":3},\"b\":{\"best\":10,\"clears\":1}},\"talismans\":{}}");
        Assert.Equal("backup.json: 2 levels merged", (string)m["text"]!);
        Assert.False((bool)m["bad"]!);
        Assert.Contains("\"best\":40", store.GetItem(ConfigFiles.ClearedKey));
        Assert.Contains("backup.json: 2 levels merged (from a computer)", _activity);
        // controls into the live table; a file that is not JSON says so
        m = await PostConfig("controls", "mine.json", "{\"format\":\"lemmings-3d-controls\",\"version\":1,\"keys\":{\"KeyP\":{\"action\":\"pause\",\"mod\":0}}}");
        Assert.False((bool)m["bad"]!);
        Assert.Equal("pause", keys.Get("KeyP")?.Action);
        m = await PostConfig("prefs", "notes.json", "hello");
        Assert.True((bool)m["bad"]!);
        Assert.Equal("notes.json: notes.json is not a JSON file", (string)m["text"]!);
        // only the three kinds
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("api/config?kind=secrets", ct)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.9", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.0.2", true)]
    [InlineData("::ffff:8.8.4.4", false)]
    public void OnlyLocalNetworksAreAnswered(string ip, bool local) =>
        Assert.Equal(local, LevelServer.IsLocalNetwork(IPAddress.Parse(ip)));

    [Fact]
    public void ABusyPortMovesToTheNext()
    {
        using var other = new LevelServer(_installer);
        other.Start(_server.Port);
        Assert.True(other.Running);
        Assert.NotEqual(_server.Port, other.Port);
        other.Stop();
        Assert.False(other.Running);
    }
}
