using System;
using Godot;

namespace Lemmix.App.Ui;

// A window in the scene: a Canvas2D of W x H canvas pixels painted into a SubViewport, shown on a
// quad `widthMetres` wide (the web version's CanvasTexture panels: the catalog, the settings, the
// modal, the status strip, the level text). The ray picks by UV, as there: Hit gives canvas pixels.
public partial class Panel3D : Node3D
{
    public readonly Canvas2D Canvas;
    readonly SubViewport _viewport;
    readonly CanvasHost _host;
    readonly MeshInstance3D _quad;
    public float WidthMetres { get; private set; }
    public float HeightMetres { get; private set; }

    public Panel3D(int width, int height, float widthMetres, bool transparent = true)
    {
        Canvas = new Canvas2D(width, height);
        WidthMetres = widthMetres;
        HeightMetres = widthMetres * height / width;
        _viewport = new SubViewport
        {
            Size = new Vector2I(width, height), TransparentBg = transparent, Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            // the canvas antialiases the edges of what it fills; so does this
            Msaa2D = Viewport.Msaa.Msaa4X,
        };
        _host = new CanvasHost { Canvas = Canvas, Size = new Vector2(width, height) };
        _viewport.AddChild(_host);
        AddChild(_viewport);
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = transparent ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
            AlbedoTexture = _viewport.GetTexture(),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _quad = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(WidthMetres, HeightMetres) }, MaterialOverride = mat };
        AddChild(_quad);
    }

    // after painting into Canvas: show it
    public void Commit()
    {
        _host.Sync();
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    // A canvas of a new size (the web's `canvas.width = w`, which clears it): the quad keeps its
    // width in metres and takes the new aspect.
    public void Resize(int width, int height)
    {
        if (width == Canvas.Width && height == Canvas.Height) return;
        Canvas.Resize(width, height);
        _viewport.Size = new Vector2I(width, height);
        _host.Size = new Vector2(width, height);
        HeightMetres = WidthMetres * height / width;
        ((QuadMesh)_quad.Mesh).Size = new Vector2(WidthMetres, HeightMetres);
    }

    public SubViewport Target => _viewport;

    public int RenderPriority { set { if (_quad.MaterialOverride is StandardMaterial3D m) m.RenderPriority = value; } }
    public bool NoDepthTest { set { if (_quad.MaterialOverride is StandardMaterial3D m) m.NoDepthTest = value; } }

    // Where a ray from `origin` along `dir` (world space) meets the quad, in canvas pixels, or null.
    public Vector2? Hit(Vector3 origin, Vector3 dir, out float distance)
    {
        distance = 0;
        if (!IsVisibleInTree()) return null;
        var inv = GlobalTransform.AffineInverse();
        var o = inv * origin;
        var d = inv.Basis * dir;
        if (Math.Abs(d.Z) < 1e-6f) return null;
        float t = -o.Z / d.Z;
        if (t <= 0) return null;
        var p = o + d * t;
        float u = p.X / WidthMetres + 0.5f, v = 0.5f - p.Y / HeightMetres;
        if (u < 0 || u > 1 || v < 0 || v > 1) return null;
        distance = (GlobalTransform * p - origin).Length();
        return new Vector2(u * Canvas.Width, v * Canvas.Height);
    }
}
