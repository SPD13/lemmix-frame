using System.Collections.Generic;
using Godot;
using Lemmix.App.Xr;
using Lemmix.Engine;
using Lemmix.Ui;

namespace Lemmix.App.Shell;

// web/3d/js/vr.js, what the controllers draw: per hand a lightsaber beam (a bright core inside a
// soft additive halo, stretched to where the ray lands), the hand marker at the aim pose and a
// box at the grip pose (green, over everything), the impact dot where the beam lands; NeoLemmix's
// cursor (cursor.js: a cross, a square over a lemming, an arrow with the direction filter) at the
// landing on the board in place of the dot; the dim floor grid shown in a session until the
// room's floor takes over. Updated once a frame after VrManager.Update, allocation-free.
public sealed partial class PointerView : Node3D
{
    public const float CORE_RADIUS = 0.0025f, GLOW_RADIUS = 0.008f, DOT_RADIUS = 0.009f, TIP_RADIUS = 0.014f;
    public const int MARK_PRIORITY = VrManager.VR_MARK_ORDER; // the windows' orders are raw (55..57): the marks over them

    sealed class Hand
    {
        public Node3D Aim = null!, Beam = null!, Grip = null!;
        public MeshInstance3D Dot = null!;
        public Sprite3D? Cursor;
    }

    readonly Hand[] _hands = new Hand[2];
    public readonly MeshInstance3D Floor;
    readonly CursorImages? _cursor;
    readonly Dictionary<string, ImageTexture> _cursorTex = new(System.StringComparer.Ordinal);

    static StandardMaterial3D Mat(uint rgb, float alpha, bool additive, bool depthTest, bool doubleSided = false) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
        NoDepthTest = !depthTest,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        CullMode = doubleSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
        RenderPriority = MARK_PRIORITY,
    };

    // an open cylinder along -Z from 0 to -1 (scale.z = the beam's length)
    static MeshInstance3D Cylinder(string name, float radius, Material mat)
    {
        var m = new MeshInstance3D
        {
            Name = name,
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 1, RadialSegments = 10, Rings = 1, CapTop = false, CapBottom = false },
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        // the cylinder stands on Y: laid along -Z, its middle half a metre out
        m.Transform = new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2), new Vector3(0, 0, -0.5f));
        return m;
    }

    public PointerView(CursorImages? cursor)
    {
        Name = "vr-pointers";
        _cursor = cursor is { Ok: true } ? cursor : null;
        var core = Mat(0xd8ffe2, 0.95f, true, true);
        var glow = Mat(0x36e06c, 0.28f, true, true, doubleSided: true);
        var dot = Mat(0xb9ffcb, 0.9f, true, false);
        var tip = Mat(0x6fce7e, 1f, false, false);
        var dotMesh = new SphereMesh { Radius = DOT_RADIUS, Height = DOT_RADIUS * 2, RadialSegments = 12, Rings = 8 };
        var tipMesh = new SphereMesh { Radius = TIP_RADIUS, Height = TIP_RADIUS * 2, RadialSegments = 12, Rings = 8 };
        var boxMesh = new BoxMesh { Size = new Vector3(0.03f, 0.03f, 0.06f) };
        for (int i = 0; i < 2; i++)
        {
            var h = new Hand
            {
                Aim = new Node3D { Name = "aim" + i, Visible = false },
                Beam = new Node3D { Name = "beam" },
                Grip = new MeshInstance3D { Name = "grip" + i, Mesh = boxMesh, MaterialOverride = tip, Visible = false },
                Dot = new MeshInstance3D { Name = "dot" + i, Mesh = dotMesh, MaterialOverride = dot, Visible = false },
            };
            h.Beam.AddChild(Cylinder("core", CORE_RADIUS, core));
            h.Beam.AddChild(Cylinder("glow", GLOW_RADIUS, glow));
            h.Beam.Scale = new Vector3(1, 1, 4);
            h.Aim.AddChild(h.Beam);
            h.Aim.AddChild(new MeshInstance3D { Name = "tip", Mesh = tipMesh, MaterialOverride = tip });
            AddChild(h.Aim);
            AddChild(h.Grip);
            AddChild(h.Dot);
            _hands[i] = h;
        }
        Floor = Grid(8, 16, 0x2e5f46, 0x1c2733);
        Floor.Visible = false;
        AddChild(Floor);
    }

    // THREE.GridHelper(size, divisions, centre line colour, grid colour), on the floor - its lines
    // as strips 4 mm wide (a line primitive crossing the near plane rasterises badly on some
    // drivers, and a one-pixel line shimmers in a headset)
    const float GRID_LINE_W = 0.004f;
    static MeshInstance3D Grid(float size, int divisions, uint centre, uint grid)
    {
        static Color C(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float half = size / 2, step = size / divisions, w = GRID_LINE_W / 2;
        void Strip(Vector3 a, Vector3 b, Vector3 side, Color col)
        {
            foreach (var v in new[] { a - side, b - side, b + side, a - side, b + side, a + side }) { st.SetColor(col); st.AddVertex(v); }
        }
        for (int i = 0; i <= divisions; i++)
        {
            float k = -half + i * step;
            var col = i == divisions / 2 ? C(centre) : C(grid);
            Strip(new Vector3(-half, 0, k), new Vector3(half, 0, k), new Vector3(0, 0, w), col);
            Strip(new Vector3(k, 0, -half), new Vector3(k, 0, half), new Vector3(w, 0, 0), col);
        }
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        return new MeshInstance3D { Name = "vr-floor", Mesh = st.Commit(), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    ImageTexture? CursorTexture(bool focused, int selectDx)
    {
        if (_cursor == null) return null;
        string key = CursorImages.Key(focused, selectDx);
        if (_cursorTex.TryGetValue(key, out var t)) return t;
        var pic = _cursor.Picture(key);
        if (pic == null) return null;
        t = ImageTexture.CreateFromImage(Image.CreateFromData(pic.Width, pic.Height, false, Image.Format.Rgba8, pic.Data));
        _cursorTex[key] = t;
        return t;
    }

    Sprite3D MakeCursor() => new()
    {
        Name = "vr-cursor", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Shaded = false,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, RenderPriority = MARK_PRIORITY, Visible = false,
        AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
    };

    public bool CursorShown(int hand) => _hands[hand].Cursor is { Visible: true };
    public Vector3? DotAt(int hand) => _hands[hand].Dot.Visible ? _hands[hand].Dot.GlobalPosition : null;

    // after vr.Update: the beams to their hits, the dot, the board cursor (focused: a lemming is
    // hovered; selectDx: the direction filter; pixelScale: the board's metres per level pixel)
    public void Update(VrManager vr, IXrInput input, bool focused, int selectDx, float pixelScale)
    {
        bool presenting = vr.Presenting;
        bool hasControllers = false;
        for (int i = 0; i < 2; i++) hasControllers |= input.Hands[i].Connected;
        int? aiming = vr.AimingHand;
        for (int i = 0; i < 2; i++)
        {
            var h = _hands[i];
            var src = input.Hands[i];
            bool shown = presenting && hasControllers && src.Connected;
            h.Aim.Visible = shown;
            h.Grip.Visible = shown;
            if (!shown)
            {
                h.Dot.Visible = false;
                if (h.Cursor != null) h.Cursor.Visible = false;
                continue;
            }
            h.Aim.GlobalTransform = src.Aim.Orthonormalized();
            h.Grip.GlobalTransform = src.Grip.Orthonormalized();
            // only the pointing hand carries a beam; the other keeps its marker and its grip
            bool pointer = vr.BeamVisible(i);
            h.Beam.Visible = pointer;
            var hit = pointer ? vr.LastHit(i) : null;
            h.Beam.Scale = new Vector3(1, 1, vr.BeamLength(i));
            h.Dot.Visible = hit != null;
            if (hit != null) h.Dot.GlobalPosition = hit.Point;
            // NeoLemmix's cursor where the aiming beam lands on the board, in place of the dot
            bool onBoard = aiming == i && hit != null && hit.OnBoard;
            if (onBoard && h.Cursor == null && _cursor != null)
            {
                h.Cursor = MakeCursor();
                AddChild(h.Cursor);
            }
            if (h.Cursor == null) continue;
            h.Cursor.Visible = onBoard;
            if (!onBoard) continue;
            var tex = CursorTexture(focused, selectDx);
            if (h.Cursor.Texture != tex) h.Cursor.Texture = tex;
            // CURSOR_LEVEL_PX level pixels wide, whatever the zoom
            int w = tex?.GetWidth() ?? CursorImages.LevelPx;
            h.Cursor.PixelSize = CursorImages.LevelPx * pixelScale / w;
            h.Cursor.GlobalPosition = hit!.Point;
            h.Dot.Visible = false;
        }
    }

    // the grid proves rendering works when there is nothing else; the room's floor does that better
    public void SetFloor(bool presenting, bool roomShown) => Floor.Visible = presenting && !roomShown;

    public void HideCursors()
    {
        foreach (var h in _hands) if (h.Cursor != null) h.Cursor.Visible = false;
    }
}
