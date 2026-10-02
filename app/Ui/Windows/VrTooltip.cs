using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Lemmix.App.Ui.Windows;

// web/3d/js/app.js "tooltips (VR)": the icon buttons carry no words, so a beam that rests on one
// for VR_TIP_DELAY gets a label - a small strip just above the button, in the button's own plane.
// It goes the moment the beam leaves. The skills bar's buttons get the same, from the panel's own
// hover tip (SkillTip).
public sealed class VrTooltip
{
    public const double VR_TIP_DELAY = 1500;             // ms of rest before the label shows
    public const int VR_TIP_H = 56, VR_TIP_PAD = 22;     // canvas px
    public const float VR_TIP_HEIGHT = 0.03f;            // metres

    public readonly Panel3D Panel;
    public string? TipName;   // the icon button under the beam, by bare name
    public double TipSince;   // when the beam arrived on it
    public string TipText = "";
    // vrTipTexts: what a button's label says now, or null when it has none
    public Func<string, string?> Texts;
    // the skills bar's hover tip: (text, since, the raised tile to hang it over), or null
    public Func<(string Text, double Since, Node3D Tile)?>? SkillTip;

    public VrTooltip(Func<string, string?> texts)
    {
        Texts = texts;
        Panel = new Panel3D(256, VR_TIP_H, 1f) { Name = "vr-tip", Visible = false, TopLevel = true };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_MODAL_BTN + 1; // over every window and button
    }

    /** The strip, as wide as its text. */
    public void Paint(string text)
    {
        var cx = Panel.Canvas;
        cx.font = "bold 28px monospace";
        int w = (int)Math.Ceiling(cx.measureText(text).width) + 2 * VR_TIP_PAD;
        if (Panel.Canvas.Width != w) Panel.Resize(w, VR_TIP_H);        // resizing clears the canvas
        cx = Panel.Canvas;
        cx.clearRect(0, 0, w, VR_TIP_H);
        cx.fillStyle = "rgba(10, 14, 22, 0.96)";
        cx.beginPath();
        cx.roundRect(2, 2, w - 4, VR_TIP_H - 4, 12);
        cx.fill();
        cx.strokeStyle = "#ffd866";
        cx.lineWidth = 3;
        cx.stroke();
        cx.fillStyle = "#f0f3f8";
        cx.font = "bold 28px monospace";
        cx.textAlign = "center";
        cx.textBaseline = "middle";
        cx.fillText(text, w / 2f, VR_TIP_H / 2f + 1);
        Panel.Commit();
        WebScale = new Vector2(VR_TIP_HEIGHT * w / VR_TIP_H, VR_TIP_HEIGHT);
    }

    // mesh.scale as the web sets it: (VR_TIP_HEIGHT * w / VR_TIP_H, VR_TIP_HEIGHT)
    public Vector2 WebScale { get; private set; }

    /** noteVrTipHover: the beam is on this button (or, null, on none) - restart the wait. */
    public void NoteHover(string? name, double now)
    {
        if (name == TipName) return;
        TipName = name != null && Texts(name) != null ? name : null;
        TipSince = now;
        Panel.Visible = false;
    }

    /** restedTip: the label the beam has earned, if any, and what to hang it over. */
    public (string Text, Node3D Anchor)? Rested(IEnumerable<IconButton> buttons, double now)
    {
        var button = TipName != null ? buttons.FirstOrDefault(b => b.BarTool == TipName) : null;
        if (button != null && button.IsVisibleInTree())
            return now - TipSince < VR_TIP_DELAY ? null : (Texts(TipName!) ?? "", button);
        var tip = SkillTip?.Invoke();
        if (tip == null || now - tip.Value.Since < VR_TIP_DELAY) return null;
        return (tip.Value.Text, tip.Value.Tile);
    }

    /** Where the label goes over an anchor: just above it, in its parent's plane (updateVrTip). */
    public static Transform3D PlaceOver(Transform3D anchorWorld, Quaternion parentWorldRotation, Vector2 webScale)
    {
        var up = parentWorldRotation * Vector3.Up;
        float size = anchorWorld.Basis.Scale.Y;
        var pos = anchorWorld.Origin + up * (size / 2 + VR_TIP_HEIGHT / 2 + 0.012f);
        return new Transform3D(new Basis(parentWorldRotation) * Basis.FromScale(new Vector3(webScale.X, webScale.Y, 1)), pos);
    }

    /** Per frame in a session: show the label once the beam has rested, just above its button. */
    public void Update(IEnumerable<IconButton> buttons, double now)
    {
        var tip = Rested(buttons, now);
        if (tip == null)
        {
            Panel.Visible = false;
            return;
        }
        if (tip.Value.Text != TipText || !Panel.Visible)
        {
            TipText = tip.Value.Text;
            Paint(tip.Value.Text);
        }
        var anchor = tip.Value.Anchor;
        var parent = anchor.GetParent() as Node3D;
        var q = parent != null ? parent.GlobalBasis.GetRotationQuaternion() : Quaternion.Identity;
        var xf = PlaceOver(anchor.GlobalTransform, q, WebScale);
        // the web's plane is 1 x 1; this quad is 1 x (h / w) - the same size once scaled to it
        Panel.GlobalTransform = new Transform3D(xf.Basis * Basis.FromScale(new Vector3(1 / Panel.WidthMetres, 1 / Panel.HeightMetres, 1)), xf.Origin);
        Panel.Visible = true;
    }
}
