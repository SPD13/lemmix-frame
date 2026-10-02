using Lemmix.Util;

namespace Lemmix.Audio;

// web/3d/js/app.js lemmixEngine.musicCandidates: the files a level's music may be, in the order
// they are tried - each name of the level's MUSIC line (a ';' list; a leading '!' dropped, '?'
// entries skipped), then the pack's rotation picked by the level's ordinal in the pack; each name
// looked for in the pack's music folder, then in neolemmix/music, with the files known to be
// there (an index) or every extension when unknown.
public static class MusicResolver
{
    public static readonly string[] Extensions = { "ogg", "wav", "mp3", "it", "mod", "xm", "s3m" };

    public sealed record MusicDir(string Dir, IReadOnlyList<string>? Files);

    public static List<string> Names(string? levelMusic, IReadOnlyList<string>? rotation, int ordinal)
    {
        var names = new List<string>();
        string music = levelMusic ?? "";
        if (music != "")
            foreach (string raw in music.Split(';'))
            {
                string part = JsString.Trim(raw);
                if (part.StartsWith('!')) part = part[1..];
                if (part == "" || part.StartsWith('?')) continue;
                names.Add(part);
            }
        if (rotation != null && rotation.Count > 0)
            names.Add(rotation[((ordinal % rotation.Count) + rotation.Count) % rotation.Count]);
        return names;
    }

    public static List<string> Candidates(IEnumerable<string> names, IReadOnlyList<MusicDir> dirs)
    {
        var paths = new List<string>();
        foreach (string name in names)
            foreach (var d in dirs)
            {
                if (d.Files != null)
                {
                    string stem = name.ToLowerInvariant() + ".";
                    var there = d.Files.Where(f => f.ToLowerInvariant().StartsWith(stem, StringComparison.Ordinal)
                            && Array.IndexOf(Extensions, f[stem.Length..].ToLowerInvariant()) >= 0)
                        .OrderBy(f => Array.IndexOf(Extensions, f[stem.Length..].ToLowerInvariant())) // stable, as Array.sort
                        .ToList();
                    foreach (string f in there) paths.Add(d.Dir + "/" + f);
                }
                else
                {
                    foreach (string ext in Extensions) paths.Add(d.Dir + "/" + name + "." + ext);
                }
            }
        return paths;
    }
}
