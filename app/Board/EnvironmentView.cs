using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Render;
using Bitmap = Lemmix.Engine.Bitmap;

namespace Lemmix.App.Board;

// web/3d/js/environment.js, the scene half: the room's planes (rings of floor and ceiling, a wall
// drum and a band drum per ring, the bowl under the first floor, the fog sphere) and the standing
// pieces, under a root that is a sibling of the diorama's (1 unit = 1 board pixel, y up, the slab's
// back at z = 0); the scene's background (the fog past the last ring) and the backdrop material
// the board puts behind its slab. There is no THREE.Fog: the haze is painted into the pictures
// (EnvGen.FogBlend) and the background is the fog colour, so Godot needs no fog either.
//
// The pictures are EnvGen's, built per gallery (the level's theme style) and kept for the next
// level of the style (the last three galleries). With pictures made offline for the style
// (3d/env/<style>/*.png, listed in 3d/env/index.json, when the asset root has them) the floors
// and walls are those and the rest the collage; without, the gallery is the fog alone, as the web
// page does. Built on a worker (it reads the style's pieces through its own StyleManager) and put
// up on the main thread when ready, as the web puts each picture up as it comes.
//
// Native only: a gallery with a scenery (3d/env/<style>/scenery/, made by tools/scenery-gen) is
// shown as that instead of the rings (SceneryView: a ground, strips to the horizon, a sky); the
// rings' pictures are then not built at all. SceneryEnabled (--scenery=off) puts the rings back.
public sealed partial class EnvironmentView : Node3D
{
    public const int ENV_SCENE_COLOR = EnvironmentLayout.ENV_SCENE_COLOR;
    public const int ENV_BACKDROP_COLOR = EnvironmentLayout.ENV_BACKDROP_COLOR;
    // the web's none and full, and the native fog between them: the haze alone (envgen's fog
    // pictures: sky, first floor, bowl, ceiling), no scenery, no rings of pieces
    public static readonly string[] Modes = { "none", "fog", "full" };

    // the planes' materials: MeshBasicMaterial({color, side: DoubleSide}), alphaTest 0.5 on cut-outs;
    // the sky BackSide (seen from inside)
    static BasicKey PlaneKey(bool cutout, bool smooth, bool sky) => new(false, true, true, sky ? Side.Back : Side.Double,
        smooth ? Filter.LinearMipmap : Filter.NearestMipmap, true);

    sealed class Plane
    {
        public required MeshInstance3D Mesh;
        public required ShaderMaterial Material;
        public bool Cutout, Sky;
        public Texture2D? Map;
    }

    // A gallery's pictures, kept across levels of the style
    public sealed class Gallery
    {
        public required string Key;
        public EnvPalette? Palette;
        public int? Fog;
        public bool FogOnly;
        public volatile bool Done;            // set by the worker once every picture is in
        public string Source = "collage";
        public string? CollageMode;
        public readonly Dictionary<string, (Bitmap Bitmap, bool Fog)> Pictures = new(StringComparer.Ordinal);
        public List<EnvProp> Props = new();
        public readonly Dictionary<string, ImageTexture> Textures = new(StringComparer.Ordinal);
        public List<(EnvProp Prop, ImageTexture Tex, ArrayMesh? Mesh)>? PropMeshes;
        public Task? Build;
        public SceneryView.Data? Scenery;     // the gallery's scenery, shown instead of the rings
    }
    static readonly List<Gallery> Galleries = new(); // the last few, oldest first
    static readonly Bitmap Released = new(0, 0);       // a picture's place once its texture is made

    readonly Dictionary<string, Plane> _planes = new(StringComparer.Ordinal);
    readonly List<MeshInstance3D> _props = new();
    public readonly ShaderMaterial BackdropMaterial;
    public string Mode { get; private set; } = "none";
    public Godot.Environment? SceneEnvironment;   // its background is the fog past the last ring
    public int SceneColor { get; private set; } = ENV_SCENE_COLOR;
    public bool Shown = true;
    public bool BuildInBackground = true;          // the pictures on a worker (off: built at once)
    public static bool SceneryEnabled = true;      // a gallery's scenery when it has one (--scenery=off: the rings)
    public readonly SceneryView Scenery = new();

    // the level
    EnvContext? _ctx;
    Room? _room;
    StyleManager? _styles;
    IFileSource? _io;
    Gallery? _gallery;
    EnvWallpaper? _wallpaper;
    int _token;
    bool _applied;
    readonly Dictionary<string, ArrayMesh> _layoutMeshes = new(StringComparer.Ordinal);

    // placement
    string _placed = "desktop";
    double _yFloor = double.NaN, _yCeil;
    (double X, double Z)? _center;
    double _wallHeight;
    public double PxPerMetre = EnvironmentLayout.PxPerMetre;

    public EnvironmentView()
    {
        Name = "environment";
        Visible = false;
        void Add(string name, bool cutout, bool sky = false)
        {
            var mat = BasicShader.Material(PlaneKey(cutout, false, sky), ENV_SCENE_COLOR, alphaTest: cutout ? 0.5 : 0);
            var mesh = new MeshInstance3D { Name = "env-" + name, Visible = false, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1e6f };
            AddChild(mesh);
            _planes[name] = new Plane { Mesh = mesh, Material = mat, Cutout = cutout, Sky = sky };
        }
        int n = EnvGen.ROOM.RINGS.Length;
        Add("sky", false, sky: true);
        for (int i = n - 1; i >= 0; i--)
        {
            Add("wall" + i, true);
            Add("band" + i, true);
            Add("floor" + i, i == 0);
            Add("ceiling" + i, false);
        }
        Add("bowl0", false);
        AddChild(Scenery);
        BackdropMaterial = BasicShader.Material(new BasicKey(false, true, true, Side.Front, Filter.Nearest, false), ENV_BACKDROP_COLOR);
    }

    public bool Active => Mode != "none" && _ctx != null;
    public EnvironmentView.Gallery? CurrentGallery => _gallery;
    public bool Ready => _ctx == null || Mode == "none" || (_gallery != null && _gallery.Done && _applied);
    public double YFloor => _yFloor;

    // setMode: none, fog or full; a level already up is rebuilt for it
    public void SetMode(string mode)
    {
        if (!Modes.Contains(mode)) mode = "full";
        if (mode == Mode) return;
        Mode = mode;
        ApplyVisibility();
        if (_ctx == null) return;
        DisposeSet(); // the last mode's room down (full's scenery or fog's planes) before the next is put up
        if (mode == "none") { ApplyBackdrop(); ApplyScene(); return; }
        _ = BuildAsync(BuildInBackground);
    }

    public void SetShown(bool v) { Shown = v; ApplyVisibility(); }

    void ApplyVisibility() => Visible = Active && Shown;

    // setLevel(ctx, styles): `io` is where the style's pieces and any offline pictures are read;
    // `background` builds on a worker (the page's off-the-critical-path build)
    public Task SetLevel(EnvContext ctx, IFileSource io, bool? background = null)
    {
        BuildInBackground = background ?? BuildInBackground;
        DisposeSet();
        _ctx = ctx;
        _io = io;
        _styles = new StyleManager(io); // its own: the worker reads through it
        _room = EnvGen.RoomFor(ctx.Width, ctx.Height, PxPerMetre);
        Layout();
        ApplyVisibility();
        if (Mode == "none") { ApplyBackdrop(); return Task.CompletedTask; }
        return BuildAsync(BuildInBackground);
    }

    public void ClearLevel()
    {
        _token++;
        DisposeSet();
        _ctx = null;
        foreach (var p in _planes.Values) p.Mesh.Mesh = null;
        ApplyBackdrop();
        ApplyScene();
        ApplyVisibility();
    }

    static string GalleryKey(EnvContext ctx, string mode) => EnvironmentLayout.GalleryKey(ctx) + "|" + mode + (SceneryEnabled ? "" : "|rings");

    Task BuildAsync(bool background)
    {
        int token = ++_token;
        var ctx = _ctx!;
        string key = GalleryKey(ctx, Mode);
        var g = Galleries.FirstOrDefault(x => x.Key == key);
        if (g == null)
        {
            g = new Gallery { Key = key };
            Galleries.Add(g);
            while (Galleries.Count > 3) Galleries.RemoveAt(0);
            var styles = _styles!; var io = _io!; string mode = Mode;
            if (background) g.Build = Task.Run(() => BuildGallery(g, ctx, styles, io, mode));
            else { BuildGallery(g, ctx, styles, io, mode); g.Build = Task.CompletedTask; }
        }
        _gallery = g;
        _wallpaper = EnvironmentLayout.LevelWallpaper(ctx);
        _applied = false;
        if (!background) { g.Build?.Wait(); PollBuild(); }
        return g.Build ?? Task.CompletedTask;
    }

    // _buildGallery: the sky, floor0, bowl0 and ceiling0 in the haze, then (pictures made for the
    // style) every plane, the offline picture or the collage, then the standing pieces
    static void BuildGallery(Gallery g, EnvContext ctx, StyleManager styles, IFileSource io, string mode)
    {
        try
        {
            var index = EnvironmentLayout.ReadStylesIndex(io);
            var gctx = EnvironmentLayout.GalleryContext(ctx, styles, index);
            var room = EnvGen.CanonicalRoom(EnvironmentLayout.PxPerMetre);
            var palette = EnvGen.DerivePalette(gctx);
            g.Palette = palette;
            if (SceneryEnabled && mode == "full" && SceneryView.Load(io, gctx.Dir) is { } scenery)
            {
                g.Scenery = scenery;
                g.Source = "scenery";
                g.Fog = scenery.Horizon;
                g.Done = true;
                return;
            }
            var wallpaper = EnvironmentLayout.GalleryWallpaper(gctx, styles);
            var files = mode == "fog" ? null : Files(io, gctx, room);
            if (files != null) g.Source = "file";
            var collected = EnvGen.CollectPieces(gctx);
            g.FogOnly = mode == "fog" || files == null || !collected.Pieces.Any(p => !p.Excluded);
            bool full = mode == "full" && !g.FogOnly;
            bool IsFog(string name) => name == "sky" || EnvGen.ParsePlane(name).Kind == "ceiling" || g.FogOnly;
            foreach (string name in EnvironmentLayout.FirstPlanes)
            {
                var ambient = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = false, SmoothFog = g.FogOnly }, new[] { name });
                g.Fog = ambient.Fog;
                lock (g) g.Pictures[name] = (ambient.Planes[name], IsFog(name));
            }
            if (!g.FogOnly)
            {
                EnvCollected? pieces = null;
                foreach (string name in EnvGen.PlaneNames(room))
                {
                    if (name == "backdrop" || name == "sky") continue;
                    if (!full && EnvironmentLayout.FirstPlanes.Contains(name)) continue;
                    Bitmap? bitmap = files != null && files.TryGetValue(name, out var f) ? f : null;
                    if (bitmap == null)
                    {
                        var built = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = full, Pieces = pieces ?? collected }, new[] { name });
                        pieces = built.Pieces;
                        bitmap = built.Planes[name];
                        if (built.Mode != null) g.CollageMode = built.Mode;
                    }
                    lock (g) g.Pictures[name] = (bitmap, IsFog(name));
                }
                if (full)
                {
                    var built = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = true, Pieces = pieces ?? collected }, new[] { "props" });
                    g.Props = built.Props ?? new List<EnvProp>();
                }
            }
        }
        catch (Exception e) { GD.PushWarning("[env] gallery: " + e); }
        g.Done = true;
    }

    // _files: the pictures made offline for a gallery's style, or null
    static Dictionary<string, Bitmap>? Files(IFileSource io, EnvContext gctx, Room room)
    {
        if (string.IsNullOrEmpty(gctx.Dir)) return null;
        string? index = io.Text("3d/env/index.json");
        if (index == null) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(index);
            if (!doc.RootElement.TryGetProperty("styles", out var styles)) return null;
            bool listed = styles.EnumerateArray().Any(s => string.Equals(s.GetString(), gctx.Dir, StringComparison.OrdinalIgnoreCase));
            if (!listed) return null;
        }
        catch (System.Text.Json.JsonException) { return null; }
        var output = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        foreach (string name in EnvGen.PlaneNames(room))
        {
            var (kind, i) = EnvGen.ParsePlane(name);
            if ((kind != "floor" && kind != "wall") || i >= room.Layers.Count) continue;
            var bmp = io.Image("3d/env/" + gctx.Dir + "/" + EnvironmentLayout.FileFor(name) + ".png");
            if (bmp != null) output[name] = bmp;
        }
        return output.Count > 0 ? output : null;
    }

    // Per frame on the main thread: put up whatever the gallery has once it is built
    public void PollBuild()
    {
        var g = _gallery;
        if (g == null || _applied || !g.Done) return;
        if (g.Scenery != null)
        {
            Scenery.Show(g.Scenery);
            PlaceScenery();
            ApplyScene();
            ApplyBackdrop();
            _applied = true;
            return;
        }
        List<string>? made = null;
        foreach (var (name, pic) in g.Pictures)
        {
            if (!g.Textures.TryGetValue(name, out var tex))
            {
                // _textureFor: flipY, repeat round, nearest (the haze linear), mipmapped
                tex = BoardMaterials.TextureOf(pic.Bitmap.Data, pic.Bitmap.Width, pic.Bitmap.Height, flipY: true, mipmaps: true);
                g.Textures[name] = tex;
                (made ??= new()).Add(name);
            }
            ApplyTexture(name, tex, pic.Fog);
        }
        // a picture made into a texture is the texture from then on (the gallery is kept for the
        // style's next level): its pixels let go, megabytes each
        if (made != null) foreach (string name in made) g.Pictures[name] = (Released, g.Pictures[name].Fog);
        if (g.PropMeshes == null)
        {
            g.PropMeshes = new();
            foreach (var p in g.Props)
                g.PropMeshes.Add((p, BoardMaterials.TextureOf(p.Bitmap.Data, p.Bitmap.Width, p.Bitmap.Height, false, true),
                    EnvironmentLayout.PieceGeometry(p) is { } geo ? BoardMaterials.ToMesh(geo) : null));
        }
        ApplyProps(g.PropMeshes);
        ApplyScene();
        ApplyBackdrop();
        _applied = true;
    }

    void ApplyTexture(string name, Texture2D tex, bool smooth)
    {
        if (!_planes.TryGetValue(name, out var p)) return;
        p.Map = tex;
        p.Material.Shader = BasicShader.Get(PlaneKey(p.Cutout, smooth, p.Sky));
        BasicShader.SetMap(p.Material, tex);
        bool wall = EnvGen.ParsePlane(name).Kind == "wall";
        p.Material.SetShaderParameter("uv_repeat", new Vector2(1, wall ? (float)WallRepeat() : 1));
        p.Material.SetShaderParameter("clamp_v", true);
        BasicShader.SetColor(p.Material, 0xffffff);
        p.Mesh.Visible = true;
    }

    double WallRepeat() => _room == null || _wallHeight == 0 ? 1 : _wallHeight / _room.WallPx;

    void ApplyProps(List<(EnvProp Prop, ImageTexture Tex, ArrayMesh? Mesh)> list)
    {
        ClearProps();
        foreach (var (p, tex, mesh) in list)
        {
            var mat = BasicShader.Material(new BasicKey(false, true, true, Side.Double, Filter.NearestMipmap, false), 0xffffff, 1, tex, vertexColors: true, alphaTest: 0.5);
            var mi = new MeshInstance3D { Name = "env-prop", Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            mi.SetMeta("w", p.W); mi.SetMeta("h", p.H);
            AddChild(mi);
            _props.Add(mi);
            _propData.Add(p);
        }
        PlaceProps();
    }
    readonly List<EnvProp> _propData = new();

    void PlaceProps()
    {
        if (_room == null) return;
        var c = _center ?? _room.Center;
        for (int i = 0; i < _props.Count; i++)
        {
            var (x, y, z, rotY, k) = EnvironmentLayout.PlaceProp(_propData[i], c, _yFloor);
            var basis = new Basis(Vector3.Up, (float)rotY) * Basis.FromScale(new Vector3((float)k, (float)-k, (float)k));
            _props[i].Transform = new Transform3D(basis, new Vector3((float)x, (float)y, (float)z));
        }
    }

    void ClearProps()
    {
        foreach (var m in _props) { RemoveChild(m); m.QueueFree(); }
        _props.Clear();
        _propData.Clear();
    }

    // _applyScene: past the last ring there is only the fog
    void ApplyScene()
    {
        var g = Active ? _gallery : null;
        int hex = g?.Palette != null ? (g.Fog ?? EnvGen.Scale(g.Palette.Dark, 0.35)) : ENV_SCENE_COLOR;
        SceneColor = hex;
        if (SceneEnvironment != null) SceneEnvironment.BackgroundColor = BasicShader.Rgb(hex);
    }

    // _applyBackdrop: the page's colour, the gallery's, or the level's wallpaper tiled 1:1
    void ApplyBackdrop()
    {
        var m = BackdropMaterial;
        var g = _gallery;
        BasicShader.SetMap(m, null);
        m.SetShaderParameter("uv_repeat", Vector2.One);
        if (!Active || g == null || g.Palette == null || _ctx == null) { BasicShader.SetColor(m, ENV_BACKDROP_COLOR); return; }
        var b = EnvironmentLayout.BackdropFor(_ctx, new EnvironmentLayout.Gallery
        {
            Room = _room!, Palette = g.Palette, Collected = new EnvCollected { Pieces = new(), Decor = new() },
        }, _wallpaper);
        if (b.Tiled != null)
        {
            m.Shader = BasicShader.Get(new BasicKey(false, true, true, Side.Front, Filter.NearestMipmap, true));
            BasicShader.SetMap(m, BoardMaterials.TextureOf(b.Tiled.Image.Data, b.Tiled.Image.Width, b.Tiled.Image.Height, false, true));
            m.SetShaderParameter("uv_repeat", new Vector2((float)b.RepeatX, (float)b.RepeatY));
        }
        else if (b.Prop != null)
        {
            m.Shader = BasicShader.Get(new BasicKey(false, true, true, Side.Front, Filter.NearestMipmap, false));
            BasicShader.SetMap(m, BoardMaterials.TextureOf(b.Prop.Bitmap.Data, b.Prop.Bitmap.Width, b.Prop.Bitmap.Height, false, true));
        }
        BasicShader.SetColor(m, b.Colour);
    }

    void DisposeSet()
    {
        _token++;
        ClearProps();
        Scenery.Clear();
        if (_gallery == null) return;
        _gallery = null;
        _applied = false;
        foreach (var p in _planes.Values)
        {
            BasicShader.SetMap(p.Material, null);
            p.Map = null;
            p.Mesh.Visible = false;
        }
        BasicShader.SetMap(BackdropMaterial, null);
    }

    // ------------------------------------------------------------ placement
    // placeForXR: the room takes the diorama's placement as it is now and keeps it; the floor at
    // the physical floor, the rings round the head
    // The floor's height in the world (native: the VR window's floor height; 0 is the headset's own
    // floor). A change re-lays the room on the next frame.
    public double FloorY { get; private set; }
    public void SetFloorY(double y)
    {
        if (y == FloorY) return;
        FloorY = y;
        if (_placed == "xr") _placed = null;
    }

    public void PlaceForXR(Transform3D diorama, Vector3? headPos)
    {
        _placed = "xr";
        Transform = diorama;
        double s = diorama.Basis.Scale.Y;
        if (s == 0) s = 1;
        _yFloor = (FloorY - diorama.Origin.Y) / s;
        _yCeil = (FloorY + EnvGen.ROOM.CEIL_M - diorama.Origin.Y) / s;
        if (headPos is Vector3 h)
        {
            var local = diorama.AffineInverse() * h;
            _center = (local.X, local.Z);
        }
        else _center = null;
        Layout();
        ApplyVisibility();
    }

    // placeDesktop: identity, the floor a little below the board
    public void PlaceDesktop()
    {
        _placed = "desktop";
        Transform = Transform3D.Identity;
        (_yFloor, _yCeil) = EnvironmentLayout.DesktopHeights(PxPerMetre);
        _center = null;
        Layout();
        ApplyVisibility();
    }

    // floorWorldY: the room's floor in world units
    public double FloorWorldY()
    {
        if (double.IsNaN(_yFloor)) PlaceDesktop();
        double s = Transform.Basis.Scale.Y;
        return Transform.Origin.Y + (s == 0 ? 1 : s) * _yFloor;
    }

    void Layout()
    {
        if (_room == null) return;
        if (double.IsNaN(_yFloor)) { PlaceDesktop(); return; }
        var geos = EnvironmentLayout.Layout(_room, _center, _yFloor, _yCeil);
        foreach (var (name, g) in geos)
            if (_planes.TryGetValue(name, out var p)) p.Mesh.Mesh = BoardMaterials.ToMesh(g);
        _wallHeight = _yCeil - _yFloor;
        foreach (var (name, p) in _planes)
            if (EnvGen.ParsePlane(name).Kind == "wall" && p.Map != null)
                p.Material.SetShaderParameter("uv_repeat", new Vector2(1, (float)WallRepeat()));
        PlaceProps();
        PlaceScenery();
    }

    // the scenery round the room's centre on its floor, clear of the first ring
    void PlaceScenery()
    {
        if (_room == null || Scenery.Shown == null) return;
        Scenery.Place(_center ?? _room.Center, _yFloor, PxPerMetre, _room.Layers[0].ROut / PxPerMetre);
    }

    // ------------------------------------------------------------ every frame
    // update(camera, dioramaRoot, presenting): the eye kept inside the first ring on the desktop
    // (returns the eye moved, or null), the board kept inside the room in a session, and whatever
    // stands between the eye and the board hidden
    public Vector3? Update(Vector3 eyeWorld, Node3D dioramaRoot, bool presenting)
    {
        PollBuild();
        if (!Active || _room == null) return null;
        if (presenting && (_placed != "xr" || Math.Abs(Transform.Basis.Scale.Y - dioramaRoot.Transform.Basis.Scale.Y) > 1e-6))
            PlaceForXR(dioramaRoot.Transform, eyeWorld);
        else if (!presenting && _placed != "desktop") PlaceDesktop();
        var room = _room;
        var c = _center ?? room.Center;
        var inv = BoardMaterials.WorldOf(this).AffineInverse();
        var eye = inv * eyeWorld;
        if (Scenery.Shown != null) Scenery.SetEye(eye);
        Vector3? moved = null;
        if (!presenting)
        {
            double margin = 0.3 * PxPerMetre;
            double dx = eye.X - c.X, dz = eye.Z - c.Z, d = Math.Sqrt(dx * dx + dz * dz), rMax = room.Layers[0].ROut - margin;
            bool m = false;
            if (d > rMax) { eye.X = (float)(c.X + dx * rMax / d); eye.Z = (float)(c.Z + dz * rMax / d); m = true; }
            double yLo = _yFloor + margin * 0.5, yHi = _yCeil - margin * 0.5;
            if (eye.Y < yLo) { eye.Y = (float)yLo; m = true; }
            if (eye.Y > yHi) { eye.Y = (float)yHi; m = true; }
            if (m) moved = BoardMaterials.WorldOf(this) * eye;
        }
        if (presenting) KeepBoardInside(dioramaRoot, inv, room, c);
        var board = inv * (BoardMaterials.WorldOf(dioramaRoot) * new Vector3((float)(room.W / 2), (float)(room.H / 2), 8));
        double ex = eye.X - c.X, ez = eye.Z - c.Z, bx = board.X - c.X, bz = board.Z - c.Z;
        double dEye = Math.Sqrt(ex * ex + ez * ez), dBoard = Math.Sqrt(bx * bx + bz * bz);
        double vx = bx - ex, vz = bz - ez, len2 = vx * vx + vz * vz;
        double t = len2 > 0 ? Math.Max(0, Math.Min(1, -(ex * vx + ez * vz) / len2)) : 0;
        double ax = ex + vx * t, az = ez + vz * t;
        double dMin = Math.Sqrt(ax * ax + az * az);
        foreach (var l in room.Layers)
        {
            var wall = _planes["wall" + l.I]; var band = _planes["band" + l.I];
            wall.Mesh.Visible = wall.Map != null && !((dEye > l.ROut || dBoard > l.ROut) && dMin < l.ROut);
            band.Mesh.Visible = band.Map != null && !((dEye > l.RBand || dBoard > l.RBand) && dMin < l.RBand);
        }
        double pad = 0.1 * PxPerMetre;
        var seg = board - eye;
        float segLen2 = seg.LengthSquared();
        for (int i = 0; i < _props.Count; i++)
        {
            var mesh = _props[i];
            var p = mesh.Position;
            double tp = segLen2 > 0 ? Math.Max(0, Math.Min(1, (p - eye).Dot(seg) / segLen2)) : 0;
            var near = eye + seg * (float)tp;
            double horiz = Math.Sqrt((near.X - p.X) * (near.X - p.X) + (near.Z - p.Z) * (near.Z - p.Z));
            var pr = _propData[i];
            mesh.Visible = !(tp > 0 && tp < 1 && horiz < pr.W / 2 + pad && Math.Abs(near.Y - (p.Y + pr.H / 2)) < pr.H / 2 + pad);
        }
        return moved;
    }

    // _keepBoardInside: the board's box in the room's frame pushed back inside the first ring,
    // between floor and ceiling, and never behind the player
    void KeepBoardInside(Node3D dioramaRoot, Transform3D inv, Room room, (double X, double Z) c)
    {
        var m = inv * BoardMaterials.WorldOf(dioramaRoot);
        double W = room.W, H = room.H;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var (x, y, z) in new[] { (0.0, 0.0, 0.0), (W, 0, 0), (0, H, 0), (W, H, 0), (0, 0, 16), (W, 0, 16), (0, H, 16), (W, H, 16) })
        {
            var v = m * new Vector3((float)x, (float)y, (float)z);
            minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
            minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
        }
        double margin = 0.15 * PxPerMetre;
        var shift = Vector3.Zero;
        double yLo = _yFloor + margin, yHi = _yCeil - margin;
        if (maxY - minY < yHi - yLo)
        {
            if (minY < yLo) shift.Y = (float)(yLo - minY);
            else if (maxY > yHi) shift.Y = (float)(yHi - maxY);
        }
        double rMax = room.Layers[0].ROut - margin;
        double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
        double half = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxZ - minZ) * (maxZ - minZ)) / 2;
        if (half < rMax)
        {
            double dx = cx - c.X, dz = cz - c.Z, d = Math.Sqrt(dx * dx + dz * dz);
            double reach = rMax - half;
            if (d > reach && d > 0) { shift.X = (float)(dx * (reach / d) - dx); shift.Z = (float)(dz * (reach / d) - dz); }
        }
        double zFront = c.Z - 0.4 * PxPerMetre;
        if (maxZ + shift.Z > zFront) shift.Z = (float)(zFront - maxZ);
        if (shift.LengthSquared() == 0) return;
        var world = Transform.Basis.GetRotationQuaternion() * shift * Transform.Basis.Scale;
        dioramaRoot.Position += world;
    }
}
