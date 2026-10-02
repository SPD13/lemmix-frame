using System.Text.Json;

namespace Lemmix.Render;

// The depth profiles a level's pieces are tagged in: web/3d/js/profile-store.js (ProfileStore
// normalize / merge / urlsForGroundData, ProfileFiles.loadAll) over the files of
// web/3d/profiles/ (nx-<style>.json), the part depth.js reads (depth.js DepthProfiles).
// A missing or unreadable file is an empty profile, not an error.

/// <summary>A JSON value as the tag lookups see it: only `false`, `true` and strings mean anything.</summary>
public readonly struct ProfileValue
{
    public enum Kinds : byte { Undefined, Null, False, True, String, Other }
    public readonly Kinds Kind;
    public readonly string? Str;
    ProfileValue(Kinds kind, string? str = null) { Kind = kind; Str = str; }

    public static readonly ProfileValue Undefined = new(Kinds.Undefined);
    public static ProfileValue Of(bool b) => new(b ? Kinds.True : Kinds.False);
    public static ProfileValue Of(string s) => new(Kinds.String, s);

    public bool IsFalse => Kind == Kinds.False;
    public bool IsTrue => Kind == Kinds.True;
    public bool IsString => Kind == Kinds.String;
    // JS truthiness, for `!p.terrain.default`
    public bool Truthy => Kind == Kinds.True || Kind == Kinds.Other || (Kind == Kinds.String && Str!.Length > 0);

    public static ProfileValue FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => new(Kinds.True),
        JsonValueKind.False => new(Kinds.False),
        JsonValueKind.String => new(Kinds.String, e.GetString()),
        JsonValueKind.Null => new(Kinds.Null),
        // numbers: 0 is falsy, the rest truthy (only `default` truthiness cares)
        JsonValueKind.Number => e.GetDouble() != 0 && !double.IsNaN(e.GetDouble()) ? new(Kinds.Other) : new(Kinds.Null),
        _ => new(Kinds.Other),
    };
}

/// <summary>One tag kind of a profile: `{ default, byId }`.</summary>
public sealed class ProfileSection
{
    public ProfileValue Default = ProfileValue.Undefined;
    public Dictionary<string, ProfileValue> ById = new(StringComparer.Ordinal);
}

public sealed class DepthProfile
{
    public ProfileSection Terrain = new() { Default = ProfileValue.Of("terrain") };
    public ProfileSection Emboss = new(), Blend = new(), ColorBlend = new(), Sculpt = new();

    /// <summary>ProfileStore.emptyProfile.</summary>
    public static DepthProfile Empty() => new();

    /// <summary>A file's text brought to the page's shape (ProfileStore.normalize of the parsed JSON).</summary>
    public static DepthProfile Parse(string? json)
    {
        var p = new DepthProfile();
        if (json == null) return p;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.TrimStart('﻿')); }
        catch (JsonException) { return p; } // unreadable: defaults apply
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return p;
            p.Terrain.Default = ProfileValue.Undefined;
            Section(root, "terrain", p.Terrain);
            if (!p.Terrain.Default.Truthy) p.Terrain.Default = ProfileValue.Of("terrain");
            Section(root, "emboss", p.Emboss);
            Section(root, "blend", p.Blend);
            Section(root, "colorBlend", p.ColorBlend);
            Section(root, "sculpt", p.Sculpt);
        }
        return p;
    }

    static void Section(JsonElement root, string name, ProfileSection into)
    {
        if (!root.TryGetProperty(name, out var s) || s.ValueKind != JsonValueKind.Object) return;
        if (s.TryGetProperty("default", out var d)) into.Default = ProfileValue.FromJson(d);
        if (s.TryGetProperty("byId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            foreach (var kv in byId.EnumerateObject()) into.ById[kv.Name] = ProfileValue.FromJson(kv.Value);
    }

    /// <summary>ProfileStore.merge: byId maps unioned (a later file wins), defaults copied when present.</summary>
    public static DepthProfile Merge(IEnumerable<DepthProfile?> entries)
    {
        var o = Empty();
        foreach (var p in entries)
        {
            if (p == null) continue;
            foreach (var kv in p.Terrain.ById) o.Terrain.ById[kv.Key] = kv.Value; // the terrain default stays "terrain"
            MergeSection(o.Emboss, p.Emboss);
            MergeSection(o.Blend, p.Blend);
            MergeSection(o.ColorBlend, p.ColorBlend);
            MergeSection(o.Sculpt, p.Sculpt);
        }
        return o;
    }

    static void MergeSection(ProfileSection o, ProfileSection p)
    {
        foreach (var kv in p.ById) o.ById[kv.Key] = kv.Value;
        if (p.Default.Kind != ProfileValue.Kinds.Undefined) o.Default = p.Default;
    }

    /// <summary>
    /// The profile files a level needs, in first-seen order (ProfileStore.urlsForGroundData for a
    /// Lemmix level): one per style its pieces come from, as file names (nx-&lt;style&gt;.json).
    /// </summary>
    public static List<string> FilesForGroundData(GroundData? groundData)
    {
        var files = new List<string>();
        if (groundData == null) return files;
        foreach (var img in groundData.TerraImages)
        {
            if (img?.Name == null) continue;
            int colon = img.Name.IndexOf(':');
            if (colon <= 0) continue; // a DOS id: no style
            string style = img.Name.Substring(0, colon);
            if (!IsGalleryStyle(style)) continue;
            string file = "nx-" + style + ".json";
            if (!files.Contains(file)) files.Add(file);
        }
        return files;
    }

    // /^nx:[a-z0-9_]+$/ on "nx:" + style
    static bool IsGalleryStyle(string style)
    {
        if (style.Length == 0) return false;
        foreach (char c in style)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
        return true;
    }

    /// <summary>The merged profile of a level's styles, read from a profiles directory (app.js:2895).</summary>
    public static DepthProfile Load(string profileDir, GroundData? groundData) =>
        Merge(FilesForGroundData(groundData).Select(f =>
        {
            string path = Path.Combine(profileDir, f);
            string? text = null;
            try { if (File.Exists(path)) text = File.ReadAllText(path); } catch (IOException) { }
            return Parse(text);
        }));
}
