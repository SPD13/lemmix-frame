// The off-device half of the Frame probe (plan, phase 0): what can be checked
// in the Steam Linux Runtime sniper arm64 container without a GPU or a headset.
// Writes one JSON report to stdout. Usage: ProbeVirtual <module.it> [--no-net]
using System.Diagnostics;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

var report = new Dictionary<string, object?>
{
    ["os"] = RuntimeInformation.OSDescription,
    ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
    ["dotnet"] = RuntimeInformation.FrameworkDescription,
};
string? module = args.FirstOrDefault(a => !a.StartsWith("--"));
bool net = !args.Contains("--no-net");

report["openmpt"] = Probe("openmpt", () => OpenMpt.Render(module));
report["caseSensitive"] = Probe("case", CaseSensitivity);
report["gc"] = Probe("gc", GcPauses);
if (net) report["download"] = Probe("download", () => Downloads().GetAwaiter().GetResult());

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

static object Probe(string name, Func<object> f)
{
    try { return f(); }
    catch (Exception e) { return new { error = e.GetType().Name + ": " + e.Message }; }
}

// The styles package has folders like dex_rust/Objects while the engine asks for objects/:
// fine on macOS, a miss on the Frame's filesystem. Records which kind this one is.
static object CaseSensitivity()
{
    string dir = Path.Combine(Path.GetTempPath(), "lemmix-case-" + Environment.ProcessId);
    Directory.CreateDirectory(Path.Combine(dir, "Objects"));
    File.WriteAllText(Path.Combine(dir, "Objects", "A.png"), "x");
    bool lowerFound = File.Exists(Path.Combine(dir, "objects", "a.png"));
    Directory.Delete(dir, true);
    return new { caseSensitive = !lowerFound };
}

// Rewind keeps a state every 170 frames, about 3.6 MB each on a 1600x320 level (large object heap).
// Simulate ×8 play (136 sim frames a second) for 10 s with a 90 Hz frame loop and record the gaps.
static object GcPauses()
{
    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
    var states = new List<byte[]>();
    var sw = Stopwatch.StartNew();
    var gaps = new List<double>();
    double last = 0;
    int simFrame = 0;
    var rnd = new Random(1);
    TimeSpan pause0 = GC.GetTotalPauseDuration();
    while (sw.Elapsed.TotalSeconds < 10)
    {
        for (int i = 0; i < 2; i++) // ×8 at 17 fps ≈ 1.5 sim frames per 90 Hz frame
        {
            simFrame++;
            var scratch = new byte[4096 + rnd.Next(4096)]; // per-frame garbage
            scratch[0] = 1;
            if (simFrame % 170 == 0)
            {
                states.Add(new byte[1600 * 320 * 7]);
                if (states.Count > 40) states.RemoveAt(rnd.Next(states.Count / 2)); // thinning
            }
        }
        while (sw.Elapsed.TotalMilliseconds - last < 11.1) Thread.SpinWait(50);
        double now = sw.Elapsed.TotalMilliseconds;
        gaps.Add(now - last);
        last = now;
    }
    gaps.Sort();
    return new
    {
        frames = gaps.Count,
        p50Ms = Math.Round(gaps[gaps.Count / 2], 2),
        p99Ms = Math.Round(gaps[(int)(gaps.Count * 0.99)], 2),
        maxMs = Math.Round(gaps[^1], 2),
        totalGcPauseMs = Math.Round((GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, 2),
        gen2 = GC.CollectionCount(2),
    };
}

// setup.js OFFICIAL: the three downloads the Setup panel makes. Asks for the first KB only.
static async Task<object> Downloads()
{
    var urls = new Dictionary<string, string>
    {
        ["engine"] = "https://www.neolemmix.com/download.php?program=16",
        ["styles"] = "https://www.neolemmix.com/download.php?program=52",
        ["packs"] = "https://www.neolemmix.com/download.php?program=47",
    };
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
    http.DefaultRequestHeaders.UserAgent.ParseAdd("LemmixFrame/0.1 (+https://lemmix.spd13.us)");
    var result = new Dictionary<string, object>();
    foreach (var (name, url) in urls)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            // neolemmix.com ignores Range (2 Oct 2026: 200, full body), so read the first KB and stop
            byte[] head = new byte[1024];
            int got = 0;
            using (var body = await resp.Content.ReadAsStreamAsync())
                while (got < head.Length) { int n = await body.ReadAsync(head.AsMemory(got)); if (n == 0) break; got += n; }
            result[name] = new
            {
                status = (int)resp.StatusCode,
                finalUrl = resp.RequestMessage?.RequestUri?.ToString(),
                rangeHonoured = resp.StatusCode == HttpStatusCode.PartialContent,
                totalLength = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength,
                fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'),
                zipSignature = got >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4,
            };
        }
        catch (Exception e) { result[name] = new { error = e.GetType().Name + ": " + e.Message }; }
    }
    return result;
}

static class OpenMpt
{
    const string Lib = "openmpt";
    [DllImport(Lib)] static extern IntPtr openmpt_get_string(string key);
    [DllImport(Lib)] static extern void openmpt_free_string(IntPtr s);
    [DllImport(Lib)] static extern IntPtr openmpt_module_create_from_memory2(byte[] data, UIntPtr size,
        IntPtr logfunc, IntPtr loguser, IntPtr errfunc, IntPtr erruser, IntPtr error, IntPtr errmsg, IntPtr ctls);
    [DllImport(Lib)] static extern void openmpt_module_destroy(IntPtr mod);
    [DllImport(Lib)] static extern UIntPtr openmpt_module_read_interleaved_float_stereo(IntPtr mod, int rate, UIntPtr count, float[] buf);
    [DllImport(Lib)] static extern double openmpt_module_get_duration_seconds(IntPtr mod);
    [DllImport(Lib)] static extern int openmpt_module_set_repeat_count(IntPtr mod, int count);

    public static object Render(string? path)
    {
        IntPtr v = openmpt_get_string("library_version");
        string version = Marshal.PtrToStringUTF8(v) ?? "?";
        openmpt_free_string(v);
        if (path == null) return new { version };
        byte[] data = File.ReadAllBytes(path);
        IntPtr mod = openmpt_module_create_from_memory2(data, (UIntPtr)data.Length,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (mod == IntPtr.Zero) return new { version, error = "module_create failed" };
        openmpt_module_set_repeat_count(mod, -1); // loop forever, as the game's music does
        const int rate = 48000, block = 1024, seconds = 10;
        var buf = new float[block * 2];
        var sw = Stopwatch.StartNew();
        ulong hash = 1469598103934665603UL;
        long frames = 0;
        while (frames < rate * seconds)
        {
            int n = (int)openmpt_module_read_interleaved_float_stereo(mod, rate, (UIntPtr)block, buf);
            if (n == 0) break;
            for (int i = 0; i < n * 2; i++) { hash ^= (ulong)BitConverter.SingleToInt32Bits(buf[i]); hash *= 1099511628211UL; }
            frames += n;
        }
        double ms = sw.Elapsed.TotalMilliseconds;
        double duration = openmpt_module_get_duration_seconds(mod);
        openmpt_module_destroy(mod);
        return new { version, file = Path.GetFileName(path), durationS = Math.Round(duration, 1), renderedS = frames / (double)rate,
                     renderMs = Math.Round(ms, 1), realtimeFactor = Math.Round(frames / (double)rate * 1000 / ms, 1), hash = hash.ToString("x16") };
    }
}
