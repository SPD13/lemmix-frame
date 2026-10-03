using Godot;
using Lemmix.App.Xr;

namespace Lemmix.App.Test;

// The "controllers" shot: two stand-in controllers (a handle and a rounded head, dark like the
// Frame's) wearing the app's sticker as ControllerSticker places it on a runtime model, lit by
// ControllerModels' lights, each turned to show its outer side. The real models only come from
// the runtime on the device; this checks the sticker's side, size, uprightness and transparency.
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
        Part("handle", new CylinderMesh { TopRadius = 0.017f, BottomRadius = 0.015f, Height = 0.11f, RadialSegments = 24 },
            new Transform3D(new Basis(Vector3.Right, 0.35f), new Vector3(0, -0.01f, 0.012f)));
        Part("head", new SphereMesh { Radius = 1, Height = 2, RadialSegments = 32, Rings = 16 },
            new Transform3D(Basis.FromScale(new Vector3(0.03f, 0.025f, 0.045f)), new Vector3(0, 0.045f, -0.035f)));
        if (namedSticker)
        {
            var sticker = new QuadMesh { Size = new Vector2(0.025f, 0.025f) };
            model.AddChild(new MeshInstance3D { Name = "Sticker_Area", Mesh = sticker, Transform = GripInModel * new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(0.031f, 0.045f, -0.035f)) });
        }
        return model;
    }

    public static Viewport Make(Node root)
    {
        var vp = new SubViewport { Size = new Vector2I(960, 540), OwnWorld3D = true, TransparentBg = false, Msaa3D = Viewport.Msaa.Msaa4X, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        vp.AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("2b3a4a"), TonemapMode = Godot.Environment.ToneMapper.Linear } });
        root.AddChild(vp);
        ControllerModels.AddLights(vp);
        for (int i = 0; i < 2; i++)
        {
            bool left = i == 0;
            var model = FakeController(left ? "left" : "right");
            // the outer side towards the camera
            var gripAt = new Transform3D(new Basis(Vector3.Up, left ? 1.25f : -1.25f), new Vector3(left ? -0.07f : 0.07f, 0, 0));
            model.Transform = gripAt * GripInModel.AffineInverse();
            vp.AddChild(model);
            ControllerSticker.SetLayers(model, ControllerModels.HandLayer[i]);
            var placed = ControllerSticker.PlaceDecal(model, GripInModel, left, ControllerModels.HandLayer[i]);
            GD.Print($"[lemmix] controllers: {(left ? "left" : "right")} sticker {(placed == null ? "none" : $"size {placed.Value.Size} flatness {placed.Value.Flatness:0.0000}")}");
        }
        var cam = new Camera3D { Fov = 40, Near = 0.01f, Far = 10, Position = new Vector3(0, 0.05f, 0.24f), CullMask = 0xFFFFF };
        vp.AddChild(cam);
        cam.LookAt(new Vector3(0, 0.025f, 0), Vector3.Up);
        cam.MakeCurrent();
        return vp;
    }
}
