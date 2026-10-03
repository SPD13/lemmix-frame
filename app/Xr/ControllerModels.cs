using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Lemmix.App.Xr;

// The controllers as they are: Valve's way for the Frame (Steam Frame compatibility review: "VR
// games that render controller models must show Frame's controllers", through OpenXR's
// XR_EXT_render_model / XR_EXT_interaction_render_model). The runtime hands over each
// controller's glTF and the poses of its moving parts (trigger, buttons, stick), and Godot's
// OpenXRRenderModelManager shows and animates them - one manager per hand under the origin. Each
// model carries the app's sticker (ControllerSticker). Where the runtime has no render models,
// nothing loads and PointerView keeps its box at the grip. What each model turned out to be is
// written to user://controller-models.json (pulled with the device reports).
public sealed partial class ControllerModels : Node3D
{
    // the models' own render layers (19 left, 20 right): their lights and their sticker reach
    // nothing else
    public static readonly uint[] HandLayer = { 1u << 18, 1u << 19 };
    public const string ReportFile = "user://controller-models.json";

    sealed class HandModel
    {
        public OpenXRRenderModel? Model;
        public string Method = "";            // how the sticker went on: "mesh", "decal" or "" (pending)
        public Transform3D? LastGripInModel;  // the previous frame's, to wait for a steady hand
        public Dictionary<string, object?>? Report;
    }

    readonly IXrInput _input;
    readonly HandModel[] _hands = { new(), new() };

    public ControllerModels(IXrInput input)
    {
        _input = input;
        Name = "controller-models";
        for (int i = 0; i < 2; i++)
        {
            int hand = i;
            var m = new OpenXRRenderModelManager
            {
                Name = i == 0 ? "left" : "right",
                Tracker = i == 0 ? OpenXRRenderModelManager.RenderModelTracker.LeftHand : OpenXRRenderModelManager.RenderModelTracker.RightHand,
            };
            m.RenderModelAdded += model => Added(hand, model);
            m.RenderModelRemoved += model => { if (_hands[hand].Model == model) _hands[hand] = new HandModel(); };
            AddChild(m);
        }
        AddLights(this);
    }

    // lit for the models only (the rest of the scene is unshaded): a key from above the player's
    // shoulder, a dim fill from below
    public static void AddLights(Node parent)
    {
        parent.AddChild(new DirectionalLight3D
        {
            Name = "controller-key", LightEnergy = 1.1f, LightCullMask = HandLayer[0] | HandLayer[1], ShadowEnabled = false,
            Transform = new Transform3D(new Basis(Quaternion.FromEuler(new Vector3(-0.9f, 0.5f, 0))), Vector3.Zero),
        });
        parent.AddChild(new DirectionalLight3D
        {
            Name = "controller-fill", LightEnergy = 0.35f, LightCullMask = HandLayer[0] | HandLayer[1], ShadowEnabled = false,
            Transform = new Transform3D(new Basis(Quaternion.FromEuler(new Vector3(0.7f, -2.6f, 0))), Vector3.Zero),
        });
    }

    /** A controller model is drawn for this hand (0 left, 1 right). */
    public bool Shown(int hand) => _hands[hand].Model is { } m && IsInstanceValid(m) && m.IsInsideTree();

    void Added(int hand, OpenXRRenderModel model)
    {
        var h = _hands[hand] = new HandModel { Model = model };
        GD.Print($"[xr] render model {(hand == 0 ? "left" : "right")}: {model.GetTopLevelPath()}");
        ControllerSticker.SetLayers(model, HandLayer[hand]);
        // the parts' own materials drawn over everything but the beam (the sticker part's too)
        h.Report = new Dictionary<string, object?>
        {
            ["hand"] = hand == 0 ? "left" : "right",
            ["topLevelPath"] = model.GetTopLevelPath(),
            ["nodes"] = ControllerSticker.Describe(model),
        };
        // a part the model names as its sticker takes the logo as it is; otherwise a decal,
        // once the hand is held still long enough to know where the grip is on the model
        if (ControllerSticker.ApplyToNamedPart(model) is { } part)
        {
            h.Method = "mesh";
            h.Report["sticker"] = new Dictionary<string, object?> { ["method"] = "mesh", ["part"] = part };
            WriteReport();
        }
        ControllerOnTop.Apply(model);
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < 2; i++)
        {
            var h = _hands[i];
            if (h.Method != "" || h.Model == null || !Shown(i)) continue;
            var src = _input.Hands[i];
            if (!src.Connected) { h.LastGripInModel = null; continue; }
            var gripInModel = h.Model.GlobalTransform.AffineInverse() * src.Grip.Orthonormalized();
            bool steady = h.LastGripInModel is { } last && last.Origin.DistanceTo(gripInModel.Origin) < 0.001f
                && last.Basis.GetRotationQuaternion().AngleTo(gripInModel.Basis.GetRotationQuaternion()) < 0.01f;
            h.LastGripInModel = gripInModel;
            if (!steady) continue;
            var placed = ControllerSticker.PlaceDecal(h.Model, gripInModel, left: i == 0, HandLayer[i]);
            h.Method = "decal";
            h.Report!["gripInModel"] = Describe(gripInModel);
            h.Report["sticker"] = placed == null
                ? new Dictionary<string, object?> { ["method"] = "none", ["why"] = "no outward face found" }
                : new Dictionary<string, object?> { ["method"] = "decal", ["at"] = Describe(placed.Value.Transform), ["size"] = placed.Value.Size, ["flatness"] = placed.Value.Flatness };
            WriteReport();
        }
    }

    static object Describe(Transform3D t) => new
    {
        origin = new[] { t.Origin.X, t.Origin.Y, t.Origin.Z },
        x = new[] { t.Basis.Column0.X, t.Basis.Column0.Y, t.Basis.Column0.Z },
        y = new[] { t.Basis.Column1.X, t.Basis.Column1.Y, t.Basis.Column1.Z },
        z = new[] { t.Basis.Column2.X, t.Basis.Column2.Y, t.Basis.Column2.Z },
    };

    void WriteReport()
    {
        var all = new List<object?>();
        foreach (var h in _hands) if (h.Report != null) all.Add(h.Report);
        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["profiles"] = new[]
                {
                    XRServer.GetTracker("left_hand") is XRControllerTracker l ? l.Profile : null,
                    XRServer.GetTracker("right_hand") is XRControllerTracker r ? r.Profile : null,
                },
                ["models"] = all,
            }, new JsonSerializerOptions { WriteIndented = true });
            using var f = FileAccess.Open(ReportFile, FileAccess.ModeFlags.Write);
            f?.StoreString(json);
        }
        catch (Exception e) { GD.PrintErr("[lemmix] controller report: " + e.Message); }
    }
}

// The app's sticker on a controller model: the Lemmix logo (its character cut out of the icon,
// transparent around it). A mesh part the runtime's model names as a sticker (or badge, decal,
// logo, label) wears it as its texture; otherwise it is a decal on the outer side of the
// controller's head - the side facing away from the other hand, where it shows when the player
// looks at their hands - found by casting rays at the model's own triangles in the grip pose's
// frame (OpenXR grip: +X to the player's right for both hands, +Y up, -Z ahead).
public static class ControllerSticker
{
    public const string LogoPath = "res://Xr/sticker.png";
    static readonly string[] PartNames = { "sticker", "badge", "decal", "logo", "label", "emblem" };
    // the decal's side and how far it reaches into the surface
    public const float MaxSize = 0.03f, MinSize = 0.014f;

    static Texture2D? _logo;
    public static Texture2D? Logo => _logo ??= ResourceLoader.Exists(LogoPath) ? GD.Load<Texture2D>(LogoPath) : null;

    public static void SetLayers(Node root, uint layer)
    {
        foreach (var n in Walk(root))
            if (n is VisualInstance3D v)
            {
                v.Layers = layer;
                if (n is GeometryInstance3D g) g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }
    }

    /** The model's nodes, meshes and materials as names (for the device report). */
    public static List<string> Describe(Node root)
    {
        var lines = new List<string>();
        foreach (var n in Walk(root))
        {
            string line = n.GetPath().ToString().Replace(root.GetPath().ToString(), "") + " " + n.GetClass();
            if (n is MeshInstance3D { Mesh: { } mesh })
            {
                line += " aabb " + mesh.GetAabb();
                for (int s = 0; s < mesh.GetSurfaceCount(); s++) line += " [" + (mesh.SurfaceGetMaterial(s)?.ResourceName ?? "") + "]";
            }
            lines.Add(line);
        }
        return lines;
    }

    static bool IsStickerName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var p in PartNames) if (name.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static StandardMaterial3D StickerMaterial() => new()
    {
        ResourceName = "lemmix-sticker",
        AlbedoTexture = Logo,
        Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
        AlphaScissorThreshold = 0.5f,
        AlphaAntialiasingMode = BaseMaterial3D.AlphaAntiAliasing.AlphaToCoverage,
        Roughness = 0.6f,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
    };

    /**
     * A mesh (or a mesh's material) named as a sticker gets the logo on those surfaces. Returns
     * the part's name, or null when the model has none.
     */
    public static string? ApplyToNamedPart(Node model)
    {
        if (Logo == null) return null;
        foreach (var n in Walk(model))
        {
            if (n is not MeshInstance3D { Mesh: { } mesh } mi) continue;
            bool whole = IsStickerName(mi.Name);
            string? hit = whole ? mi.Name.ToString() : null;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                string? matName = mesh.SurfaceGetMaterial(s)?.ResourceName;
                if (!whole && !IsStickerName(matName)) continue;
                mi.SetSurfaceOverrideMaterial(s, StickerMaterial());
                hit ??= mi.Name + " [" + matName + "]";
            }
            if (hit != null) return hit;
        }
        return null;
    }

    public readonly record struct Placement(Transform3D Transform, Vector3 Size, float Flatness);

    /**
     * The decal on the outer side of the head. gripInModel: the grip pose in the model's frame.
     * Searches the upper, forward part of the model for the largest square patch facing out and
     * flat enough to take it (rays cast at the model's triangles along the outward axis, over a
     * 3x3 grid on the square). Returns where it went (in the model's frame), or null.
     */
    public static Placement? PlaceDecal(Node3D model, Transform3D gripInModel, bool left, uint layer)
    {
        if (Logo == null) return null;
        var rays = OutwardRays.Of(model, gripInModel, left);
        if (rays == null) return null;
        var (lo, hi) = (rays.Lo, rays.Hi);
        var size = hi - lo;
        float outward = left ? -1 : 1;
        for (float side = MaxSize; side >= MinSize - 1e-6f; side -= 0.004f)
        {
            Transform3D? best = null;
            float bestDev = float.MaxValue;
            for (float fy = 0.45f; fy <= 0.951f; fy += 0.05f)
                for (float fz = 0.05f; fz <= 0.651f; fz += 0.05f)
                {
                    float y = lo.Y + fy * size.Y, z = lo.Z + fz * size.Z;
                    if (rays.Cast(y, z) is not { } c || c.Normal.X * outward < 0.6f) continue;
                    // the square on the tangent plane, upright with the grip's +Y
                    var n = c.Normal.Normalized();
                    var down = (Vector3.Down - n * Vector3.Down.Dot(n)).Normalized();
                    var right = n.Cross(down).Normalized();
                    float dev = 0;
                    bool fits = true;
                    for (int i = -1; i <= 1 && fits; i++)
                        for (int j = -1; j <= 1 && fits; j++)
                        {
                            var q = c.Point + right * (i * side / 2) + down * (j * side / 2);
                            // where the plane is at q against where the surface is, along the outward axis
                            if (rays.Cast(q.Y, q.Z) is not { } h || h.Normal.Normalized().Dot(n) < 0.7f) { fits = false; break; }
                            dev = Mathf.Max(dev, Mathf.Abs(h.Point.X - q.X));
                        }
                    if (!fits || dev > MaxDeviation || dev >= bestDev) continue;
                    bestDev = dev;
                    best = new Transform3D(new Basis(right, n, down), c.Point);
                }
            if (best is not { } grip) continue;
            var t = gripInModel * grip;
            var decal = new Decal
            {
                Name = "lemmix-sticker",
                TextureAlbedo = Logo,
                Size = new Vector3(side, 2 * bestDev + 0.004f, side),
                UpperFade = 0, LowerFade = 0, NormalFade = 0.2f,
                CullMask = layer,
                Transform = t,
            };
            model.AddChild(decal);
            return new Placement(t, decal.Size, bestDev);
        }
        return null;
    }

    // how far the surface may stray from the sticker's plane (a sticker bends, a decal box is thin)
    public const float MaxDeviation = 0.004f;

    public readonly record struct RayHit(Vector3 Point, Vector3 Normal, float Distance);

    // The model's triangles in the grip's frame, binned by their extent in (y, z): a ray along
    // the outward axis (x) only meets those in its cell.
    public sealed class OutwardRays
    {
        const int Bins = 32;
        readonly List<Vector3> _tris;
        readonly List<int>[] _bins = new List<int>[Bins * Bins];
        public readonly Vector3 Lo, Hi;
        readonly float _outward;

        OutwardRays(List<Vector3> tris, float outward)
        {
            _tris = tris;
            _outward = outward;
            Lo = Hi = tris[0];
            foreach (var v in tris) { Lo = Lo.Min(v); Hi = Hi.Max(v); }
            for (int k = 0; k < _bins.Length; k++) _bins[k] = new List<int>();
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                var a = tris[i]; var b = tris[i + 1]; var c = tris[i + 2];
                int y0 = Bin(Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y)), Lo.Y, Hi.Y), y1 = Bin(Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)), Lo.Y, Hi.Y);
                int z0 = Bin(Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)), Lo.Z, Hi.Z), z1 = Bin(Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)), Lo.Z, Hi.Z);
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++) _bins[y * Bins + z].Add(i);
            }
        }

        static int Bin(float v, float lo, float hi) => hi <= lo ? 0 : Mathf.Clamp((int)((v - lo) / (hi - lo) * Bins), 0, Bins - 1);

        public static OutwardRays? Of(Node3D model, Transform3D gripInModel, bool left)
        {
            var toGrip = gripInModel.AffineInverse();
            var modelInv = model.GlobalTransform.AffineInverse();
            var tris = new List<Vector3>();
            foreach (var n in Walk(model))
            {
                if (n is not MeshInstance3D { Mesh: { } mesh } mi || !mi.IsVisibleInTree() || n.Name == "lemmix-sticker") continue;
                var meshToGrip = toGrip * (modelInv * mi.GlobalTransform);
                foreach (var v in mesh.GetFaces()) tris.Add(meshToGrip * v);
            }
            return tris.Count < 3 ? null : new OutwardRays(tris, left ? -1 : 1);
        }

        /** From outside the model at (y, z), inwards along the outward axis: the nearest surface. */
        public RayHit? Cast(float y, float z)
        {
            if (y < Lo.Y || y > Hi.Y || z < Lo.Z || z > Hi.Z) return null;
            var from = new Vector3(_outward > 0 ? Hi.X + 0.01f : Lo.X - 0.01f, y, z);
            return Raycast(_tris, _bins[Bin(y, Lo.Y, Hi.Y) * Bins + Bin(z, Lo.Z, Hi.Z)], from, new Vector3(-_outward, 0, 0));
        }
    }

    /** The nearest triangle along the ray (Möller-Trumbore), its normal turned to face the ray. */
    public static RayHit? Raycast(List<Vector3> tris, IEnumerable<int> which, Vector3 from, Vector3 dir)
    {
        RayHit? best = null;
        foreach (int i in which)
        {
            Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
            Vector3 e1 = b - a, e2 = c - a, p = dir.Cross(e2);
            float det = e1.Dot(p);
            if (Mathf.Abs(det) < 1e-12f) continue;
            float inv = 1 / det;
            var s = from - a;
            float u = s.Dot(p) * inv;
            if (u < 0 || u > 1) continue;
            var q = s.Cross(e1);
            float v = dir.Dot(q) * inv;
            if (v < 0 || u + v > 1) continue;
            float t = e2.Dot(q) * inv;
            if (t <= 0 || (best != null && t >= best.Value.Distance)) continue;
            var n = e1.Cross(e2).Normalized();
            if (n.Dot(dir) > 0) n = -n;
            best = new RayHit(from + dir * t, n, t);
        }
        return best;
    }

    static IEnumerable<Node> Walk(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.GetChildCount() - 1; i >= 0; i--) stack.Push(n.GetChild(i));
        }
    }
}

// The controllers over everything in the scene but the beam and its cursor: the windows, the bar
// and the status strip draw without a depth test, so a model drawn as the board is drawn would
// disappear under them. Each part's material becomes one that squeezes its depth into the band
// nearest the eye (it passes the depth test against everything, and its own parts still hide one
// another) and draws in the transparent pass after the windows (OnTopPriority). Same textures,
// colours and lights; the sticker's decal still lands (decals use the fragment's position).
public static class ControllerOnTop
{
    public const int OnTopPriority = VrManager.VR_MARK_ORDER - 1; // over the windows (56..58), under the beam (60)

    static Shader? _shader;
    public static Shader Shader => _shader ??= new Shader
    {
        Code = """
shader_type spatial;
render_mode depth_draw_always, cull_back, fog_disabled;
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, hint_default_white;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform float roughness = 0.5;
uniform float metallic = 0.0;
uniform float alpha_cut = 0.0;   // above 0: an alpha-scissored surface (the sticker part)
void vertex() {
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	// reverse Z (1 at the near plane): every depth squeezed into [0.999, 1], its order kept
	POSITION.z = POSITION.w * (1.0 - 0.001 * (1.0 - POSITION.z / POSITION.w));
}
void fragment() {
	vec4 c = albedo_color * texture(albedo_tex, UV);
	if (alpha_cut > 0.0 && c.a < alpha_cut) discard;
	ALBEDO = c.rgb;
	ROUGHNESS = roughness;
	METALLIC = metallic;
	ALPHA = 1.0;   // the transparent pass, ordered by render priority
}
""",
    };

    /** A part's material as one drawn on top. */
    public static ShaderMaterial From(Material? src)
    {
        var m = new ShaderMaterial { Shader = Shader, RenderPriority = OnTopPriority, ResourceName = (src?.ResourceName ?? "") + "-ontop" };
        if (src is BaseMaterial3D b)
        {
            if (b.AlbedoTexture != null) m.SetShaderParameter("albedo_tex", b.AlbedoTexture);
            m.SetShaderParameter("albedo_color", b.AlbedoColor);
            m.SetShaderParameter("roughness", b.Roughness);
            m.SetShaderParameter("metallic", b.Metallic);
            if (b.Transparency is BaseMaterial3D.TransparencyEnum.AlphaScissor or BaseMaterial3D.TransparencyEnum.Alpha or BaseMaterial3D.TransparencyEnum.AlphaHash)
                m.SetShaderParameter("alpha_cut", b.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? b.AlphaScissorThreshold : 0.5f);
        }
        return m;
    }

    /** Every surface of the model: its active material (an override the sticker set included) on top. */
    public static int Apply(Node model)
    {
        int n = 0;
        var stack = new Stack<Node>();
        stack.Push(model);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var c in node.GetChildren()) stack.Push(c);
            if (node is not MeshInstance3D { Mesh: { } mesh } mi) continue;
            // a material override outranks the surfaces' materials: replaced in its place
            if (mi.MaterialOverride != null)
            {
                if (!(mi.MaterialOverride is ShaderMaterial { Shader: var so } && so == Shader)) { mi.MaterialOverride = From(mi.MaterialOverride); n++; }
                continue;
            }
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mi.GetActiveMaterial(s) is ShaderMaterial { Shader: var sh } && sh == Shader) continue;
                mi.SetSurfaceOverrideMaterial(s, From(mi.GetActiveMaterial(s)));
                n++;
            }
        }
        return n;
    }
}
