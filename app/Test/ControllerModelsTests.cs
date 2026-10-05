using Godot;
using Lemmix.App.Shell;
using Lemmix.App.Xr;

namespace Lemmix.App.Test;

// The controllers drawn by the runtime's models: the sticker's logo, where it goes on a model
// (a part named as a sticker, else a decal on the handle's back), and the grip box hidden
// for a hand whose model is drawn.
public static class ControllerModelsTests
{
    static Node Root => ((SceneTree)Godot.Engine.GetMainLoop()).Root;

    [AppTest]
    public static void TheLogoIsCutOut()
    {
        var logo = ControllerSticker.Logo;
        Check.True(logo != null, "res://Xr/sticker.png loads");
        var img = logo!.GetImage();
        if (img.IsCompressed()) img.Decompress();
        Check.True(img.GetPixel(2, 2).A < 0.05f, "transparent in the corner");
        Check.True(img.GetPixel(30, 128).A < 0.05f, "transparent beside the character (the icon's background is gone)");
        Check.True(img.GetPixel(128, 128).A > 0.95f, "opaque on the character");
    }

    static void CheckDecal(bool left)
    {
        var model = ControllerShot.FakeController(left ? "left" : "right");
        Root.AddChild(model);
        model.GlobalTransform = new Transform3D(new Basis(new Vector3(1, 2, 3).Normalized(), 0.7f), new Vector3(0.3f, 1.2f, -0.4f));
        try
        {
            var placed = ControllerSticker.PlaceDecal(model, ControllerShot.GripInModel, ControllerModels.HandLayer[left ? 0 : 1]);
            Check.True(placed != null, "a decal placed");
            var decal = model.GetNode<Decal>("lemmix-sticker");
            Check.True(decal.TextureAlbedo == ControllerSticker.Logo, "with the logo");
            Check.Equal(ControllerModels.HandLayer[left ? 0 : 1], decal.CullMask, "on its own hand's layer only");
            // in the grip's frame: on the handle's back between the head and the status panel
            // (z -0.0275 .. -0.005), facing the player, its top towards the head
            var g = ControllerShot.GripInModel.AffineInverse() * decal.Transform;
            float half = decal.Size.X / 2;
            Check.True(g.Origin.Y > 0.012f && Mathf.Abs(g.Origin.X) < 0.004f, $"on the handle's back ({g.Origin})");
            Check.True(g.Origin.Z - half > -0.0275f && g.Origin.Z + half < -0.005f, $"by the grip, off the head and the status panel (z {g.Origin.Z}, side {decal.Size.X})");
            Check.True(g.Basis.Y.Normalized().Y > 0.6f, $"facing out of the back ({g.Basis.Y})");
            Check.True(g.Basis.Z.Normalized().Z > 0.6f, $"upright along the handle: the image's down away from the head ({g.Basis.Z})");
            Check.True(g.Basis.X.Normalized().X > 0.6f, $"the image's right the player's right ({g.Basis.X})");
        }
        finally { model.Free(); }
    }

    [AppTest] public static void DecalOnTheRightHandlesBack() => CheckDecal(false);
    [AppTest] public static void DecalOnTheLeftHandlesBack() => CheckDecal(true);

    [AppTest]
    public static void APartNamedAsTheStickerWearsTheLogo()
    {
        var model = ControllerShot.FakeController("right", namedSticker: true);
        Root.AddChild(model);
        try
        {
            string? part = ControllerSticker.ApplyToNamedPart(model);
            Check.Equal("Sticker_Area", part, "the named part found");
            var mi = model.GetNode<MeshInstance3D>("Sticker_Area");
            Check.True(mi.GetSurfaceOverrideMaterial(0) is StandardMaterial3D { AlbedoTexture: { } t } && t == ControllerSticker.Logo, "the logo as its texture");
            var plain = ControllerShot.FakeController("plain");
            try { Check.True(ControllerSticker.ApplyToNamedPart(plain) == null, "a model without one: none"); }
            finally { plain.Free(); }
        }
        finally { model.Free(); }
    }

    [AppTest]
    public static void LayersKeepTheModelsLightsAndStickerToThemselves()
    {
        var model = ControllerShot.FakeController("left");
        ControllerSticker.SetLayers(model, ControllerModels.HandLayer[0]);
        foreach (var n in model.GetChildren())
            if (n is GeometryInstance3D g)
            {
                Check.Equal(ControllerModels.HandLayer[0], g.Layers, n.Name + " on the left hand's layer");
                Check.Equal(GeometryInstance3D.ShadowCastingSetting.Off, g.CastShadow, n.Name + " casts no shadow");
            }
        model.Free();
    }

    [AppTest]
    public static void TheModelsDrawOverTheWindowsAndUnderTheBeam()
    {
        var model = ControllerShot.FakeController("right", namedSticker: true);
        ControllerSticker.ApplyToNamedPart(model);
        int n = ControllerOnTop.Apply(model);
        try
        {
            Check.Equal(4, n, "every part (three overridden, the sticker's surface)");
            foreach (var c in model.GetChildren())
                if (c is MeshInstance3D mi)
                {
                    var m = mi.GetActiveMaterial(0) as ShaderMaterial;
                    Check.True(m != null && m.Shader == ControllerOnTop.Shader, mi.Name + " drawn on top");
                    Check.True(m!.RenderPriority > Lemmix.App.Ui.Windows.IconButton.GUI_ORDER_MODAL_BTN + 1, mi.Name + " after the windows and the tooltip");
                    Check.True(m.RenderPriority < Lemmix.App.Shell.PointerView.MARK_PRIORITY, mi.Name + " under the beam and its cursor");
                }
            var sticker = (ShaderMaterial)model.GetNode<MeshInstance3D>("Sticker_Area").GetActiveMaterial(0);
            Check.True(sticker.GetShaderParameter("albedo_tex").As<Texture2D>() == ControllerSticker.Logo, "the sticker keeps its logo");
            Check.True(sticker.GetShaderParameter("alpha_cut").AsSingle() > 0, "and its cut-out edge");
            Check.Equal(0, ControllerOnTop.Apply(model), "applied twice: nothing more to do");
        }
        finally { model.Free(); }
    }

    [AppTest]
    public static void TheGripBoxAndTipSphereGiveWayToTheModel()
    {
        var input = new ScriptedXrInput();
        var root = new Node3D();
        var vr = new VrManager(input, new NoHooks(), root);
        vr.Update(0);
        var pv = new PointerView(null);
        Root.AddChild(pv);
        try
        {
            pv.Update(vr, input, false, 0, 0.001f);
            Check.True(pv.GetNode<Node3D>("grip0").Visible && pv.GetNode<Node3D>("grip1").Visible, "no models: both boxes");
            pv.HandModelShown = i => i == 1;
            pv.Update(vr, input, false, 0, 0.001f);
            Check.True(pv.GetNode<Node3D>("grip0").Visible, "left box kept (no model)");
            Check.True(!pv.GetNode<Node3D>("grip1").Visible, "right box hidden (its model is drawn)");
            Check.True(pv.GetNode<Node3D>("aim0/tip").Visible, "left marker sphere kept (no model)");
            Check.True(pv.GetNode<Node3D>("aim1").Visible && pv.GetNode<Node3D>("aim1/beam").Visible, "the right hand's beam stays");
            Check.True(!pv.GetNode<Node3D>("aim1/tip").Visible, "right marker sphere hidden (its model is drawn)");
            pv.HandModelShown = null;
            pv.Update(vr, input, false, 0, 0.001f);
            Check.True(pv.GetNode<Node3D>("grip1").Visible && pv.GetNode<Node3D>("aim1/tip").Visible, "the model gone: box and sphere back");
        }
        finally { pv.Free(); root.Free(); }
    }

    sealed class NoHooks : IVrHooks
    {
        public VrPick? Pick(Vector3 o, Vector3 d) => null;
        public VrHit? RaycastHit(Vector3 o, Vector3 d) => null;
        public void OnSelectPick(VrPick pick) { }
        public void OnHoverPick(VrPick? pick) { }
        public void OnVrButton(string code, bool down) { }
        public void OnStick(string code, float x, float y, double dt) { }
        public bool PlaceDiorama(Transform3D? head) => true;
    }
}
