using Godot;
using Lemmix.App.Xr;

namespace Lemmix.App.Test;

// The "controllers" shot: two stand-in controllers (a handle along the grip's Z, a rounded head
// ahead of it and a status panel on the handle's back about the grip, dark like the Frame's)
// wearing the app's sticker as ControllerSticker places it on a runtime model, lit by
// ControllerModels' lights, each held as the player sees it: the handle's back towards them, the
// head up. The real models only come from the runtime on the device; this checks the sticker's
// side, size, uprightness and transparency.
public static class ControllerShot
{
    // where the grip pose sits in the stand-in model's frame (a runtime's model need not be
    // authored about the grip, so the tests use a frame that is not)
    public static readonly Transform3D GripInModel = new(new Basis(Vector3.Up, 0.3f), new Vector3(0.01f, -0.02f, 0.03f));

    /** A stand-in controller model for one hand, its parts placed in the grip's frame. */
    public static Node3D FakeController(string name, bool namedSticker = false)
    {
        var model = new Node3D { Name = name };
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.16f, 0.17f), Roughness = 0.45f };
        void Part(string partName, Mesh mesh, Transform3D inGrip) =>
            model.AddChild(new MeshInstance3D { Name = partName, Mesh = mesh, MaterialOverride = mat, Transform = GripInModel * inGrip });
        Part("handle", new CylinderMesh { TopRadius = 0.015f, BottomRadius = 0.017f, Height = 0.095f, RadialSegments = 32 },
            new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(0, 0, 0.01f)));
        Part("head", new SphereMesh { Radius = 1, Height = 2, RadialSegments = 32, Rings = 16 },
            new Transform3D(Basis.FromScale(new Vector3(0.03f, 0.022f, 0.032f)), new Vector3(0, 0.012f, -0.058f)));
        Part("status", new BoxMesh { Size = new Vector3(0.022f, 0.003f, 0.035f) }, new Transform3D(Basis.Identity, new Vector3(0, 0.0165f, 0.0125f)));
        if (namedSticker)
        {
            var sticker = new QuadMesh { Size = new Vector2(0.025f, 0.025f) };
            model.AddChild(new MeshInstance3D { Name = "Sticker_Area", Mesh = sticker, Transform = GripInModel * new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(0.018f, 0, 0.01f)) });
        }
        return model;
    }

    public static Viewport Make(Node root) => Make(root, false);

    // overScene: a window drawn without depth (as the catalog is) and a solid block (as the board)
    // both nearer the camera than the controllers, which must still show over them
    public static Viewport Make(Node root, bool overScene)
    {
        var vp = new SubViewport { Size = new Vector2I(960, 540), OwnWorld3D = true, TransparentBg = false, Msaa3D = Viewport.Msaa.Msaa4X, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        vp.AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("2b3a4a"), TonemapMode = Godot.Environment.ToneMapper.Linear } });
        root.AddChild(vp);
        ControllerModels.AddLights(vp);
        for (int i = 0; i < 2; i++)
        {
            bool left = i == 0;
            var model = FakeController(left ? "left" : "right");
            // the handle's back (the grip's +Y) towards the camera, the head (its -Z) up
            var gripAt = new Transform3D(new Basis(Vector3.Up, left ? 0.3f : -0.3f) * new Basis(Vector3.Right, 1.25f), new Vector3(left ? -0.05f : 0.05f, 0.02f, 0));
            model.Transform = gripAt * GripInModel.AffineInverse();
            vp.AddChild(model);
            ControllerSticker.SetLayers(model, ControllerModels.HandLayer[i]);
            var placed = ControllerSticker.PlaceDecal(model, GripInModel, ControllerModels.HandLayer[i]);
            if (overScene) ControllerOnTop.Apply(model);
            GD.Print($"[lemmix] controllers: {(left ? "left" : "right")} sticker {(placed == null ? "none" : $"size {placed.Value.Size} flatness {placed.Value.Flatness:0.0000}")}");
        }
        if (overScene)
        {
            var window = new Lemmix.App.Ui.Panel3D(256, 128, 0.07f) { Name = "window", Position = new Vector3(-0.07f, 0.03f, 0.06f) };
            window.NoDepthTest = true;
            window.RenderPriority = Lemmix.App.Ui.Windows.IconButton.GUI_ORDER_MODAL;
            window.Canvas.fillStyle = "#1f6feb"; window.Canvas.fillRect(0, 0, 256, 128);
            window.Canvas.fillStyle = "white"; window.Canvas.font = "bold 40px monospace"; window.Canvas.fillText("WINDOW", 40, 70);
            window.Commit();
            vp.AddChild(window);
            vp.AddChild(new MeshInstance3D
            {
                Name = "block", Mesh = new BoxMesh { Size = new Vector3(0.025f, 0.06f, 0.01f) }, Position = new Vector3(0.06f, 0.02f, 0.06f),
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("c2410c") },
            });
        }
        var cam = new Camera3D { Fov = 40, Near = 0.01f, Far = 10, Position = new Vector3(0, 0.05f, 0.24f), CullMask = 0xFFFFF };
        vp.AddChild(cam);
        cam.LookAt(new Vector3(0, 0.025f, 0), Vector3.Up);
        cam.MakeCurrent();
        return vp;
    }
}
