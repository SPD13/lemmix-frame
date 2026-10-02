using System.Reflection;
using System.Runtime.InteropServices;

namespace Lemmix.Audio;

// libopenmpt's C API (the web version plays tracker music through chiptune3, libopenmpt built
// for an AudioWorklet). The native library ships next to the app: libopenmpt.so on the Frame
// (native/libopenmpt/build.sh, Steam Runtime sniper arm64), libopenmpt.dylib on the Mac.
public static class OpenMpt
{
    const string Lib = "openmpt";

    static OpenMpt()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(OpenMpt).Assembly, Resolve); }
        catch (InvalidOperationException) { /* a resolver is already set for this assembly */ }
    }

    // Extra places to look: OPENMPT_DIR, the assembly's folder, and native/libopenmpt/out/<rid>
    // above it (the repo layout, for the tests and the editor).
    static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string file = OperatingSystem.IsMacOS() ? "libopenmpt.dylib" : OperatingSystem.IsWindows() ? "openmpt.dll" : "libopenmpt.so";
        string rid = (OperatingSystem.IsMacOS() ? "macos-" : "linux-") + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
        var dirs = new List<string?> { Environment.GetEnvironmentVariable("OPENMPT_DIR"), Path.GetDirectoryName(asm.Location), AppContext.BaseDirectory };
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            dirs.Add(Path.Combine(d.FullName, "native", "libopenmpt", "out", rid));
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string p = Path.Combine(dir, file);
            if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;
        }
        return IntPtr.Zero;
    }

    [DllImport(Lib)] static extern IntPtr openmpt_get_string([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Lib)] static extern void openmpt_free_string(IntPtr s);
    [DllImport(Lib)] internal static extern IntPtr openmpt_module_create_from_memory2(byte[] data, UIntPtr size,
        IntPtr logfunc, IntPtr loguser, IntPtr errfunc, IntPtr erruser, IntPtr error, IntPtr errmsg, IntPtr ctls);
    [DllImport(Lib)] internal static extern void openmpt_module_destroy(IntPtr mod);
    [DllImport(Lib)] internal static extern UIntPtr openmpt_module_read_interleaved_float_stereo(IntPtr mod, int rate, UIntPtr count, float[] buf);
    [DllImport(Lib)] internal static extern double openmpt_module_get_duration_seconds(IntPtr mod);
    [DllImport(Lib)] internal static extern int openmpt_module_set_repeat_count(IntPtr mod, int count);

    public static string LibraryVersion
    {
        get
        {
            IntPtr v = openmpt_get_string("library_version");
            string s = Marshal.PtrToStringUTF8(v) ?? "";
            openmpt_free_string(v);
            return s;
        }
    }

    // The extensions handed to the tracker player (audio.js playMusicUrl).
    public static readonly HashSet<string> TrackerExtensions = new(StringComparer.Ordinal) { "it", "xm", "mod", "s3m", "mtm", "umx", "mo3" };
}

// One module, looping forever as the game's music does (chiptune3 repeatCount -1).
public sealed class TrackerModule : IDisposable
{
    IntPtr _mod;

    public TrackerModule(byte[] data, bool loop = true)
    {
        _mod = OpenMpt.openmpt_module_create_from_memory2(data, (UIntPtr)data.Length,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_mod == IntPtr.Zero) throw new InvalidDataException("libopenmpt cannot read this module");
        OpenMpt.openmpt_module_set_repeat_count(_mod, loop ? -1 : 0);
    }

    public double DurationSeconds => OpenMpt.openmpt_module_get_duration_seconds(_mod);

    // Interleaved stereo floats into `buffer` (2 per frame); returns the frames written (0 at the end).
    public int Read(int sampleRate, float[] buffer, int frames)
    {
        if (_mod == IntPtr.Zero) return 0;
        return (int)OpenMpt.openmpt_module_read_interleaved_float_stereo(_mod, sampleRate, (UIntPtr)Math.Min(frames, buffer.Length / 2), buffer);
    }

    public void Dispose()
    {
        if (_mod != IntPtr.Zero) { OpenMpt.openmpt_module_destroy(_mod); _mod = IntPtr.Zero; }
    }
}
