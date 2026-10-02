using System;
using System.Collections.Generic;
using Godot;
using Lemmix.Audio;
using Lemmix.Io;

namespace Lemmix.App.Audio;

// web/3d/js/audio.js, the Lemmix half: sound cues from neolemmix/sound, positioned at their
// emitter in the headset; level music from the candidates (MusicResolver), tracker modules
// through libopenmpt, anything else decoded and looped; one volume over everything, kept with
// the sound switch in the preferences (lem3d-sound, lem3d-volume). The web version's AdLib
// fallback (classic-engine data) is not part of the native app: no file, no sound.
public partial class GameAudio : Node
{
    // audio.js _panner: an "inverse" rolloff from 1.5 m, factor 0.5, out to 25 m - shallow on
    // purpose, so the far end of a 4 m board stays audible under the music.
    const float RefDistance = 1.5f, MaxDistance = 25f, Rolloff = 0.5f;
    const int MixRate = 48000;

    IFileSource? _io;
    SoundFiles? _sounds;
    readonly Dictionary<string, AudioStream?> _streams = new(StringComparer.Ordinal);
    readonly List<AudioStreamPlayer> _flat = new();
    readonly List<AudioStreamPlayer3D> _spatial = new();
    AudioStreamPlayer? _music;
    TrackerModule? _tracker;
    float[] _trackerBuf = new float[4096];
    Vector2[] _push = new Vector2[2048];

    public bool Enabled { get; private set; } = true;
    public float Volume { get; private set; } = 1;
    // VR only: the listener's place for positioned cues (desktop units are pixels, not metres)
    public Func<bool>? SpatialActive;

    public void Configure(IFileSource io, bool enabled, float volume)
    {
        _io = io;
        _sounds = new SoundFiles(io);
        _streams.Clear();
        Enabled = enabled;
        Volume = Math.Clamp(volume, 0, 1);
    }

    static float Db(float gain) => gain <= 0 ? -80f : Mathf.LinearToDb(gain);

    public void SetVolume(float v)
    {
        Volume = Math.Clamp(v, 0, 1);
        if (_music != null) _music.VolumeDb = Db(Volume);
    }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        if (!on) StopAll();
    }

    AudioStream? StreamFor(string name)
    {
        if (_streams.TryGetValue(name, out var s)) return s;
        var file = _sounds?.Find(name);
        s = file == null ? null : Decode(file.Value.Ext, file.Value.Data, loop: false);
        _streams[name] = s;
        return s;
    }

    // What the bytes are, whatever the name says: the browser's decodeAudioData sniffs the content,
    // and the styles ship .wav files that are Ogg or MP3 inside (common_slam1, spring, ...).
    static string Sniff(string ext, byte[] d)
    {
        if (d.Length >= 4 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F') return "wav";
        if (d.Length >= 4 && d[0] == 'O' && d[1] == 'g' && d[2] == 'g' && d[3] == 'S') return "ogg";
        if (d.Length >= 3 && d[0] == 'I' && d[1] == 'D' && d[2] == '3') return "mp3";
        if (d.Length >= 2 && d[0] == 0xFF && (d[1] & 0xE0) == 0xE0) return "mp3";
        return ext;
    }

    // WAVE format tag (1 PCM, 3 float: what Godot reads). Others - GSM 6.10 (49) in two of the
    // teleporter sounds - are not decoded, as the browser's decoder does not either: silent.
    static int WaveFormat(byte[] d)
    {
        for (int i = 12; i + 8 <= d.Length;)
        {
            int size = BitConverter.ToInt32(d, i + 4);
            if (d[i] == 'f' && d[i + 1] == 'm' && d[i + 2] == 't' && d[i + 3] == ' ' && i + 10 <= d.Length) return BitConverter.ToUInt16(d, i + 8);
            if (size < 0) break;
            i += 8 + size + (size & 1);
        }
        return -1;
    }

    static AudioStream? Decode(string ext, byte[] data, bool loop)
    {
        ext = Sniff(ext, data);
        if (ext == "wav" && WaveFormat(data) is not (1 or 3)) return null;
        try
        {
            switch (ext)
            {
                case "wav":
                {
                    var w = AudioStreamWav.LoadFromBuffer(data, new Godot.Collections.Dictionary());
                    if (w != null && loop) { w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopEnd = (int)(w.GetLength() * w.MixRate); }
                    return w;
                }
                case "ogg":
                {
                    var o = AudioStreamOggVorbis.LoadFromBuffer(data);
                    if (o != null) o.Loop = loop;
                    return o;
                }
                case "mp3":
                    return new AudioStreamMP3 { Data = data, Loop = loop };
            }
        }
        catch (Exception e) { GD.PushWarning("[audio] cannot decode a ." + ext + ": " + e.Message); }
        return null;
    }

    // A NeoLemmix sound cue by name, at a world position in the headset (null: not positioned).
    public void PlayCue(string name, Vector3? worldPos = null, Vector3? listener = null)
    {
        if (!Enabled || string.IsNullOrEmpty(name)) return;
        var stream = StreamFor(name);
        if (stream == null) return;
        if (worldPos is Vector3 p && SpatialActive?.Invoke() == true)
        {
            var player = Take(_spatial, () => new AudioStreamPlayer3D
            {
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
                MaxDistance = MaxDistance, PanningStrength = 1,
            });
            player.GlobalPosition = p;
            // the web panner's inverse distance model, computed here at play time as it is there
            float d = listener is Vector3 l ? Math.Max(l.DistanceTo(p), RefDistance) : RefDistance;
            float gain = d > MaxDistance ? 0 : RefDistance / (RefDistance + Rolloff * (d - RefDistance));
            player.VolumeDb = Db(Volume * gain);
            player.Stream = stream;
            player.Play();
        }
        else
        {
            var player = Take(_flat, () => new AudioStreamPlayer());
            player.VolumeDb = Db(Volume);
            player.Stream = stream;
            player.Play();
        }
    }

    // a free player from the pool, or a new one (every cue plays to its end, as the web's sources do)
    T Take<T>(List<T> pool, Func<T> make) where T : Node
    {
        foreach (var p in pool)
            if (p is AudioStreamPlayer a && !a.Playing || p is AudioStreamPlayer3D b && !b.Playing) return p;
        var n = make();
        AddChild(n);
        pool.Add(n);
        return n;
    }

    // A level's music: the first candidate that exists (audio.js playLevelMusic).
    public void PlayLevelMusic(IEnumerable<string> candidates)
    {
        if (!Enabled || _io == null) return;
        foreach (string path in candidates)
        {
            var bytes = _io.Bytes(path);
            if (bytes == null) continue;
            PlayMusicData(path, bytes);
            return;
        }
    }

    void PlayMusicData(string path, byte[] bytes)
    {
        StopMusic();
        string ext = path[(path.LastIndexOf('.') + 1)..].ToLowerInvariant();
        _music = new AudioStreamPlayer { VolumeDb = Db(Volume) };
        AddChild(_music);
        if (OpenMpt.TrackerExtensions.Contains(ext))
        {
            try { _tracker = new TrackerModule(bytes); }
            catch (Exception e) { GD.PushWarning("[audio] tracker: " + e.Message); return; }
            _music.Stream = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 0.25f };
            _music.Play();
            Feed();
        }
        else
        {
            var s = Decode(ext, bytes, loop: true);
            if (s == null) return;
            _music.Stream = s;
            _music.Play();
        }
    }

    // libopenmpt into the generator, as much as it has room for
    void Feed()
    {
        if (_tracker == null || _music?.GetStreamPlayback() is not AudioStreamGeneratorPlayback pb) return;
        int room = pb.GetFramesAvailable();
        while (room > 0)
        {
            int n = _tracker.Read(MixRate, _trackerBuf, Math.Min(room, _trackerBuf.Length / 2));
            if (n <= 0) break;
            if (_push.Length != n) _push = new Vector2[n];
            for (int i = 0; i < n; i++) _push[i] = new Vector2(_trackerBuf[2 * i], _trackerBuf[2 * i + 1]);
            pb.PushBuffer(_push);
            room -= n;
        }
    }

    public override void _Process(double delta) => Feed();

    public void StopMusic()
    {
        _tracker?.Dispose();
        _tracker = null;
        if (_music != null) { _music.Stop(); _music.QueueFree(); _music = null; }
    }

    public void StopAll()
    {
        StopMusic();
        foreach (var p in _flat) p.Stop();
        foreach (var p in _spatial) p.Stop();
    }

    public override void _ExitTree() => StopAll();
}
