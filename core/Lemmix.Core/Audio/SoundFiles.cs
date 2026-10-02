using Lemmix.Engine;
using Lemmix.Io;

namespace Lemmix.Audio;

// audio.js _fileSfxBuffer: a cue's file is neolemmix/sound/<name>.wav, .ogg or .mp3, the first
// found; kept once looked up (found or not).
public sealed class SoundFiles
{
    readonly IFileSource _io;
    readonly Dictionary<string, (string Ext, byte[] Data)?> _cache = new(StringComparer.Ordinal);

    public SoundFiles(IFileSource io) { _io = io; }

    public (string Ext, byte[] Data)? Find(string name)
    {
        if (_cache.TryGetValue(name, out var hit)) return hit;
        (string, byte[])? found = null;
        foreach (string ext in new[] { "wav", "ogg", "mp3" })
        {
            var bytes = _io.Bytes(StyleManager.AssetDir + "sound/" + name + "." + ext);
            if (bytes != null) { found = (ext, bytes); break; }
        }
        _cache[name] = found;
        return found;
    }
}
