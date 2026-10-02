namespace Lemmix.Setup;

// setup.js OFFICIAL: the three downloads, straight from neolemmix.com (the native app has no
// CORS to work around). The server sends the zip's name in Content-Disposition and ignores
// Range (checked 2 Oct 2026), so a failed download restarts from the beginning.
public static class Downloads
{
    public sealed record Official(string Key, string Url, string Name, string Size);

    public static readonly Official Engine = new("engine", "https://www.neolemmix.com/download.php?program=16", "NeoLemmix V12.14.0", "7 MB");
    public static readonly Official Styles = new("styles", "https://www.neolemmix.com/download.php?program=52", "the styles package", "92 MB");
    public static readonly Official Packs = new("packs", "https://www.neolemmix.com/download.php?program=47", "Lemmings Plus (every pack)", "22 MB");
    public const string Site = "https://www.neolemmix.com/?page=neolemmix";
    public const string PacksSite = "https://www.neolemmix.com/?page=level_packs";

    // Download to `folder`; returns the saved file's path (named as the server names it).
    public static async Task<string> FetchAsync(HttpClient http, Official what, string folder, Action<long, long?>? progress = null,
        int attempts = 3, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        Exception? last = null;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            string tmp = Path.Combine(folder, what.Key + ".part");
            try
            {
                using var resp = await http.GetAsync(what.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
                resp.EnsureSuccessStatusCode();
                string name = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"') is { Length: > 0 } n
                    ? Path.GetFileName(n) : what.Key + ".zip";
                long? total = resp.Content.Headers.ContentLength;
                await using (var body = await resp.Content.ReadAsStreamAsync(cancel))
                await using (var file = File.Create(tmp))
                {
                    var buf = new byte[1 << 16];
                    long got = 0;
                    int n2;
                    while ((n2 = await body.ReadAsync(buf, cancel)) > 0)
                    {
                        await file.WriteAsync(buf.AsMemory(0, n2), cancel);
                        got += n2;
                        progress?.Invoke(got, total);
                    }
                }
                // a zip, or an error page that came back as 200
                using (var f = File.OpenRead(tmp))
                {
                    var sig = new byte[4];
                    if (f.Read(sig, 0, 4) != 4 || sig[0] != 'P' || sig[1] != 'K') throw new InvalidDataException("the download is not a zip file");
                }
                string dest = Path.Combine(folder, name);
                File.Move(tmp, dest, true);
                return dest;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                last = e;
                if (File.Exists(tmp)) File.Delete(tmp);
                await Task.Delay(1000 * (attempt + 1), cancel);
            }
        }
        throw new IOException("download of " + what.Name + " failed: " + last?.Message, last);
    }
}
