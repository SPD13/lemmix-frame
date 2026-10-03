using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lemmix.Setup;

// The levels folder served to a browser on another computer of the same network, since the app
// ships without levels: a page (upload.html) to browse <assets>/levels, upload files or whole
// folders into it, delete a folder or a file, and install an uploaded zip (a level pack, or
// NeoLemmix and its styles) the way the setup page does. Off unless the player turns it on.
//
// Only addresses of a local network are answered (loopback, 10/8, 172.16/12, 192.168/16,
// link-local, IPv6 unique-local): the headset's storage is not for the internet. Every path is
// relative to the levels folder and checked to stay in it; the folder's index.json is the app's
// own (rebuilt after every change) and is neither listed nor touched.
//
//   GET    /                       the page
//   GET    /api/list?path=a/b      {path, dirs:[{name, files, bytes}], files:[{name, size, modified}], free}
//   PUT    /api/file?path=a/b/c    the body becomes that file (folders made as needed)
//   DELETE /api/entry?path=a/b     a folder with everything in it, or a file
//   POST   /api/plan?path=x.zip    what installing that zip would do
//   POST   /api/install?path=x.zip unpacks it (Installer), then removes the zip
//   POST   /api/rescan             the indexes rebuilt after a batch of uploads
//
// Requests are served off the frame; LevelsChanged and Activity are raised from those threads.
public sealed class LevelServer : IDisposable
{
    public const int DefaultPort = 8642;
    public const long MaxFileBytes = 2L << 30;      // 2 GB a file

    readonly Installer _installer;
    readonly string _root;                          // <assets>/levels, absolute
    readonly object _diskLock = new();              // installs, deletes and index rebuilds one at a time
    HttpListener? _listener;
    CancellationTokenSource? _stop;

    public int Port { get; private set; }
    public bool Running => _listener?.IsListening == true;

    /** The levels on disk changed (and the indexes were rebuilt): the library should reload. */
    public event Action? LevelsChanged;
    /** A line for the player: what a computer just did. */
    public event Action<string>? Activity;

    public LevelServer(Installer installer)
    {
        _installer = installer;
        _root = Path.Combine(installer.Root, "levels");
    }

    public string LevelsDir => _root;

    /** Listens on `port`, or the next free one of the following `tries`; throws when none is. */
    public void Start(int port = DefaultPort, int tries = 10)
    {
        if (Running) return;
        Directory.CreateDirectory(_root);
        Exception? last = null;
        for (int p = port; p < port + tries; p++)
        {
            var l = new HttpListener();
            l.Prefixes.Add("http://*:" + p + "/");
            try { l.Start(); }
            catch (Exception e) when (e is HttpListenerException or SocketException) { last = e; l.Close(); continue; }
            _listener = l;
            Port = p;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _ = Task.Run(() => Loop(l, token));
            return;
        }
        throw new IOException("no free port from " + port + " to " + (port + tries - 1) + (last != null ? ": " + last.Message : ""));
    }

    public void Stop()
    {
        _stop?.Cancel();
        try { _listener?.Stop(); } catch (ObjectDisposedException) { }
        try { _listener?.Close(); } catch (ObjectDisposedException) { }
        _listener = null;
    }

    public void Dispose() => Stop();

    // ---- the address to type

    /** This device's IPv4 addresses on its networks (Wi-Fi first), for the URL shown. */
    public static List<string> LocalAddresses()
    {
        var found = new List<(int Rank, string Addr)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                int rank = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || ni.Name.StartsWith("wl", StringComparison.Ordinal) ? 0
                    : ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 2;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                    var b = ip.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) rank += 10; // link-local: only when there is nothing else
                    found.Add((rank, ip.ToString()));
                }
            }
        }
        catch (NetworkInformationException) { }
        return found.OrderBy(f => f.Rank).Select(f => f.Addr).Distinct().ToList();
    }

    public List<string> Urls() => LocalAddresses().Select(a => "http://" + a + ":" + Port + "/").ToList();

    /** A local network's address (or this device's own). */
    public static bool IsLocalNetwork(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || (b[0] & 0xfe) == 0xfc;
        return false;
    }

    // ---- paths

    /**
     * A path from the page ("a/b/c", '/'-separated, relative to the levels folder) to the disk;
     * null for one that leaves the folder or names something hidden. "" is the folder itself.
     */
    public string? Resolve(string? rel)
    {
        rel ??= "";
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p == "." || p == ".." || p.StartsWith('.') || p.Contains('\\') || p.Contains(':')) return null;
            if (p.Any(c => c < 32 || c == 127)) return null;
        }
        string full = Path.GetFullPath(Path.Combine(new[] { _root }.Concat(parts).ToArray()));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return full;
    }

    static string Clean(string? rel) => string.Join('/', (rel ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries));

    // ---- serving

    async Task Loop(HttpListener l, CancellationToken token)
    {
        while (!token.IsCancellationRequested && l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (token.IsCancellationRequested || !l.IsListening) { return; }
            catch (HttpListenerException) { continue; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    sealed class HttpError : Exception
    {
        public readonly int Status;
        public HttpError(int status, string message) : base(message) { Status = status; }
    }

    async Task Serve(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!IsLocalNetwork(req.RemoteEndPoint.Address)) throw new HttpError(403, "only from the local network");
            string route = req.Url!.AbsolutePath;
            string? path = Query(req.Url, "path");
            switch (req.HttpMethod + " " + route)
            {
                case "GET /":
                case "GET /index.html":
                    await Send(res, 200, "text/html; charset=utf-8", Page());
                    return;
                case "GET /api/list":
                    await Json(res, 200, List(path));
                    return;
                case "PUT /api/file":
                    await Json(res, 200, await Upload(path, req));
                    return;
                case "DELETE /api/entry":
                    await Json(res, 200, Delete(path));
                    return;
                case "POST /api/plan":
                    await Json(res, 200, Plan(path));
                    return;
                case "POST /api/install":
                    await Json(res, 200, Install(path));
                    return;
                case "POST /api/rescan":
                    Rescan(Query(req.Url, "note"));
                    await Json(res, 200, new JsonObject { ["ok"] = true });
                    return;
                default:
                    throw new HttpError(404, "no such page: " + req.HttpMethod + " " + route);
            }
        }
        catch (HttpError e) { await Error(res, e.Status, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Error(res, 500, e.Message);
        }
        catch (Exception e) { await Error(res, 500, e.GetType().Name + ": " + e.Message); }
        finally
        {
            try { res.Close(); } catch (Exception) { }
        }
    }

    static string? Query(Uri url, string key)
    {
        foreach (var kv in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=');
            string k = Uri.UnescapeDataString((eq < 0 ? kv : kv[..eq]).Replace('+', ' '));
            if (k == key) return eq < 0 ? "" : Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    static async Task Send(HttpListenerResponse res, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = type;
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    static Task Json(HttpListenerResponse res, int status, JsonNode body) => Send(res, status, "application/json; charset=utf-8", body.ToJsonString());

    static async Task Error(HttpListenerResponse res, int status, string message)
    {
        try { await Json(res, status, new JsonObject { ["error"] = message }); } catch (Exception) { }
    }

    string Must(string? rel, bool rootOk = true)
    {
        var abs = Resolve(rel);
        if (abs == null) throw new HttpError(400, "not a path in the levels folder: " + rel);
        if (!rootOk && abs == _root) throw new HttpError(400, "the levels folder itself");
        if (abs == IndexFile) throw new HttpError(400, "index.json is the app's own index of the levels");
        return abs;
    }

    string IndexFile => Path.Combine(_root, "index.json");

    static string? _page;
    static string Page()
    {
        if (_page != null) return _page;
        using var s = typeof(LevelServer).Assembly.GetManifestResourceStream("Lemmix.Setup.upload.html")
            ?? throw new IOException("upload.html is not in the build");
        using var r = new StreamReader(s, Encoding.UTF8);
        return _page = r.ReadToEnd();
    }

    // ---- the actions (public for the tests)

    public JsonObject List(string? rel)
    {
        string abs = Must(rel);
        if (!Directory.Exists(abs)) throw new HttpError(404, "no folder " + Clean(rel));
        var dirs = new JsonArray();
        foreach (var d in Index.NaturalCompare.Sort(Directory.EnumerateDirectories(abs).Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.'))))
        {
            long files = 0, bytes = 0;
            foreach (var f in Directory.EnumerateFiles(Path.Combine(abs, d), "*", SearchOption.AllDirectories))
            {
                files++;
                try { bytes += new FileInfo(f).Length; } catch (IOException) { }
            }
            dirs.Add(new JsonObject { ["name"] = d, ["files"] = files, ["bytes"] = bytes });
        }
        var fileList = new JsonArray();
        foreach (var n in Index.NaturalCompare.Sort(Directory.EnumerateFiles(abs).Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.'))))
        {
            if (Path.Combine(abs, n) == IndexFile) continue;
            var fi = new FileInfo(Path.Combine(abs, n));
            fileList.Add(new JsonObject { ["name"] = n, ["size"] = fi.Length, ["modified"] = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() });
        }
        long? free = null;
        try { free = new DriveInfo(_root).AvailableFreeSpace; } catch (Exception) { }
        return new JsonObject { ["path"] = Clean(rel), ["dirs"] = dirs, ["files"] = fileList, ["free"] = free };
    }

    async Task<JsonObject> Upload(string? rel, HttpListenerRequest req)
    {
        string abs = Must(rel, rootOk: false);
        if (Directory.Exists(abs)) throw new HttpError(409, Clean(rel) + " is a folder");
        if (req.ContentLength64 > MaxFileBytes) throw new HttpError(413, "larger than " + (MaxFileBytes >> 20) + " MB");
        long written = await Write(abs, req.InputStream);
        return new JsonObject { ["path"] = Clean(rel), ["size"] = written };
    }

    /** The body into the file: through a hidden part file beside it, moved over it at the end. */
    public async Task<long> Write(string abs, Stream body)
    {
        string dir = Path.GetDirectoryName(abs)!;
        for (string d = dir; d.Length > _root.Length; d = Path.GetDirectoryName(d)!)
            if (File.Exists(d)) throw new HttpError(409, Path.GetRelativePath(_root, d).Replace('\\', '/') + " is a file");
        Directory.CreateDirectory(dir);
        string part = Path.Combine(dir, "." + Path.GetFileName(abs) + "." + Guid.NewGuid().ToString("N")[..8] + ".part");
        long n = 0;
        try
        {
            await using (var f = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                int k;
                while ((k = await body.ReadAsync(buf)) > 0)
                {
                    n += k;
                    if (n > MaxFileBytes) throw new HttpError(413, "larger than " + (MaxFileBytes >> 20) + " MB");
                    await f.WriteAsync(buf.AsMemory(0, k));
                }
            }
            File.Move(part, abs, true);
            return n;
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }
    }

    public JsonObject Delete(string? rel)
    {
        string abs = Must(rel, rootOk: false);
        string name = Clean(rel);
        lock (_diskLock)
        {
            if (Directory.Exists(abs))
            {
                // a top folder is a unit the setup page knows: the installer forgets it too
                if (!name.Contains('/')) _installer.DeleteDir(name);
                else { Directory.Delete(abs, true); _installer.RebuildIndexes(); }
            }
            else if (File.Exists(abs))
            {
                File.Delete(abs);
                _installer.RebuildIndexes();
            }
            else throw new HttpError(404, "nothing at " + name);
        }
        Activity?.Invoke(name + " deleted from a computer");
        LevelsChanged?.Invoke();
        return new JsonObject { ["deleted"] = name };
    }

    string Zip(string? rel)
    {
        string abs = Must(rel, rootOk: false);
        if (!File.Exists(abs)) throw new HttpError(404, "no file " + Clean(rel));
        if (!abs.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new HttpError(400, Clean(rel) + " is not a zip");
        return abs;
    }

    public JsonObject Plan(string? rel)
    {
        InstallPlan plan;
        try { plan = _installer.Plan(Zip(rel)); }
        catch (InvalidDataException e) { throw new HttpError(422, e.Message); }
        return new JsonObject
        {
            ["kind"] = plan.Kind,
            ["label"] = plan.Label,
            ["dirs"] = new JsonArray(plan.Dirs.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["clashing"] = new JsonArray(plan.ClashingDirs.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["replaces"] = plan.Replaces?.Version is { } v ? (v == "" ? "installed" : v) : null,
        };
    }

    public JsonObject Install(string? rel)
    {
        string zip = Zip(rel);
        UnitInfo unit;
        InstallPlan plan;
        lock (_diskLock)
        {
            try { plan = _installer.Plan(zip); }
            catch (InvalidDataException e) { throw new HttpError(422, e.Message); }
            unit = _installer.Install(zip, plan, Path.GetFileName(zip));
            File.Delete(zip);
            _installer.RebuildIndexes(); // the zip itself is gone from the folder now
        }
        string what = plan.Kind == "levels" ? string.Join(", ", plan.Dirs) : plan.Label;
        Activity?.Invoke(what + " installed from a computer");
        LevelsChanged?.Invoke();
        return new JsonObject { ["kind"] = plan.Kind, ["installed"] = what, ["files"] = unit.Files, ["bytes"] = unit.Bytes };
    }

    public void Rescan(string? note = null)
    {
        lock (_diskLock) _installer.RebuildIndexes();
        if (!string.IsNullOrEmpty(note)) Activity?.Invoke(note);
        LevelsChanged?.Invoke();
    }
}
