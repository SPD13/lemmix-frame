using System;
using System.IO;
using System.Linq;
using Godot;
using Lemmix.App.Audio;
using Lemmix.Io;

namespace Lemmix.App.Test;

public static class AudioTests
{
    static string Assets => System.Environment.GetEnvironmentVariable("WEB_ASSETS")
        ?? Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "..", "LemmingsJS"));

    // every NeoLemmix sound decodes through Godot's runtime loaders (the WAVs vary in format)
    [AppTest]
    public static void EverySoundEffectDecodes()
    {
        string dir = Path.Combine(Assets, "neolemmix", "sound");
        if (!Directory.Exists(dir)) { GD.Print("[test] (no assets: skipped)"); return; }
        var audio = new GameAudio();
        audio.Configure(new DiskFileSource(Assets), true, 1);
        var names = Directory.EnumerateFiles(dir).Select(Path.GetFileNameWithoutExtension).Distinct().ToList();
        // GSM 6.10 WAVs: not decoded by the browser either (GameAudio.WaveFormat)
        var known = new[] { "teleporter_02", "teleporter_03" };
        var failed = names.Where(n => n != null && !known.Contains(n) && Decodes(audio, n) == false).ToList();
        audio.Free();
        Check.True(names.Count > 10, "found the sound folder's files");
        Check.True(failed.Count == 0, "not decoded: " + string.Join(", ", failed));
    }

    static bool Decodes(GameAudio audio, string name)
    {
        var m = typeof(GameAudio).GetMethod("StreamFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return m.Invoke(audio, new object[] { name }) is AudioStream;
    }
}
