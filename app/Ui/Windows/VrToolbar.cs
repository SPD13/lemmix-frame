using System.Linq;
using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// web/3d/js/app.js, the VR bar's own controls (guiRoot's icon buttons): the row above the skills
// bar - quit (the native app's own: the game ends, asking first), lock, move, park, settings, VR
// (the native app's own: foveated rendering) at the left end, pause in the middle, worlds, prev, restart,
// solution, next at the right - and the sound column off its right end, a mute switch with the
// volume slider over it (shown while the beam is on either, and VR_SOUND_LINGER after).
// GuiRoot is the web's guiRoot: the skills bar hangs in it too (the panel is another port's).
public sealed class VrToolbar
{
    public readonly Node3D GuiRoot = new() { Name = "vr-guiroot" };
    public readonly IconButton Quit, Lock, Move, Park, Settings, VrButton, Pause, Restart, Solution, Prev, Next, Worlds, Mute;
    public readonly Panel3D Volume;
    public readonly IconButton[] LeftTools, RightTools, Buttons;
    public bool VolumeHovered;
    public float VolumeLevel = 1;
    public double SoundPanelUntil;

    public VrToolbar()
    {
        IconButton Make(string name) { var b = new IconButton(name, BarIcons.ByName(name)!); GuiRoot.AddChild(b); return b; }
        Lock = Make("lock"); Move = Make("move"); Park = Make("park");
        Pause = Make("pause"); Restart = Make("restart"); Solution = Make("solution");
        Prev = Make("prev"); Next = Make("next"); Worlds = Make("worlds");
        Volume = new Panel3D(64, 256, 1f) { Name = "vr-volume", Visible = false };
        Volume.NoDepthTest = true;
        Volume.RenderPriority = IconButton.GUI_ORDER_BAR_TOOL;
        GuiRoot.AddChild(Volume);
        Mute = Make("mute");
        Settings = Make("settings");
        Quit = Make("quit");
        VrButton = Make("vr");
        LeftTools = new[] { Quit, Lock, Move, Park, Settings, VrButton };
        RightTools = new[] { Worlds, Prev, Restart, Solution, Next };
        Buttons = LeftTools.Concat(new[] { Pause }).Concat(RightTools).ToArray();
        PaintVolume(1, false);
    }

    // the slider: one surface that draws its own track, fill and knob, so the hit's V is the value
    public void PaintVolume(float level, bool hovered)
    {
        VolumeLevel = level;
        var cx = Volume.Canvas;
        cx.clearRect(0, 0, 64, 256);
        float top = 14, bot = 242, span = bot - top;
        cx.fillStyle = "rgba(16, 20, 28, 0.92)";
        cx.beginPath();
        cx.roundRect(2, 2, 60, 252, 14);
        cx.fill();
        if (hovered)
        {
            cx.strokeStyle = "#ffffff"; cx.lineWidth = 3;
            cx.beginPath();
            cx.roundRect(4, 4, 56, 248, 12);
            cx.stroke();
        }
        cx.fillStyle = "#2a3446";                       // the groove
        cx.beginPath();
        cx.roundRect(26, top, 12, span, 6);
        cx.fill();
        float y = bot - span * level;                   // filled from the bottom
        cx.fillStyle = "#6fce7e";
        cx.beginPath();
        cx.roundRect(26, y, 12, bot - y, 6);
        cx.fill();
        cx.fillStyle = "#f0f3f8";                       // the knob
        cx.beginPath();
        cx.roundRect(12, y - 8, 40, 16, 6);
        cx.fill();
        Volume.Commit();
    }

    /** paintVolume: the slider at the audio's level, the mute switch at its state. */
    public void PaintSound(float volume, bool enabled)
    {
        PaintVolume(volume, VolumeHovered);
        Mute.SetState(on: !enabled);
    }

    /** setBarToolHover's share for the bar: the slider's own hover, and the linger. */
    public void NoteHover(string? name, double now)
    {
        bool onSlider = name == "volume";
        if (VolumeHovered != onSlider)
        {
            VolumeHovered = onSlider;
            PaintVolume(VolumeLevel, onSlider);
        }
        // the slider is summoned by the speaker and lingers: the beam has to cross the gap
        if (onSlider || name == "mute") SoundPanelUntil = now + VR_SOUND_LINGER;
    }

    public readonly record struct Place(Vector3 Pos, Vector2 Scale);

    // layoutGuiPanel's headset branch, as numbers: guiW is the skills bar's width (VR_GUI_WIDTH x
    // panelWidthScale), barH its mesh's height (guiW x canvas h / w)
    public static float RowY(float barH) => VR_GUI_Y + barH / 2 + VR_BAR_TOOL_SIZE * 0.8f;
    public static float RowX(int index, int leftCount, int rightCount, float guiW, bool left, bool pause)
    {
        float step = VR_BAR_TOOL_SIZE * 1.15f;
        float end = guiW / 2 - VR_BAR_TOOL_SIZE * 0.6f;
        if (pause) return 0;
        return left ? -end + index * step : end - (rightCount - 1 - index) * step;
    }
    public static Place ButtonPlace(float x, float barH, bool hot)
    {
        float s = VR_BAR_TOOL_SIZE * (hot ? VR_BAR_TOOL_HOVER : 1);
        return new Place(new Vector3(x, RowY(barH), VR_GUI_Z + (hot ? VR_BAR_TOOL_SIZE * 0.25f : 0)), new Vector2(s, s));
    }
    public static Place MutePlace(float guiW, float barH, bool hot)
    {
        float sx = guiW / 2 + VR_BAR_TOOL_SIZE * 0.85f;
        float barBottom = VR_GUI_Y - barH / 2;
        float s = VR_BAR_TOOL_SIZE * (hot ? VR_BAR_TOOL_HOVER : 1);
        return new Place(new Vector3(sx, barBottom + VR_BAR_TOOL_SIZE / 2, VR_GUI_Z + (hot ? VR_BAR_TOOL_SIZE * 0.25f : 0)), new Vector2(s, s));
    }
    public static Place VolumePlace(float guiW, float barH, bool muteHot)
    {
        var mute = MutePlace(guiW, barH, muteHot);
        return new Place(new Vector3(mute.Pos.X, mute.Pos.Y + VR_BAR_TOOL_SIZE / 2 + VR_VOLUME_HEIGHT / 2 + 0.008f, VR_GUI_Z),
            new Vector2(VR_BAR_TOOL_SIZE * 0.62f, VR_VOLUME_HEIGHT));
    }

    /** Lay the row and the sound column out (in a headset, every frame). */
    public void Layout(float guiW, float barH, double now)
    {
        for (int i = 0; i < LeftTools.Length; i++) LeftTools[i].Position = LeftTools[i].Position with { X = RowX(i, LeftTools.Length, RightTools.Length, guiW, true, false) };
        Pause.Position = Pause.Position with { X = 0 };
        for (int i = 0; i < RightTools.Length; i++) RightTools[i].Position = RightTools[i].Position with { X = RowX(i, LeftTools.Length, RightTools.Length, guiW, false, false) };
        var mute = MutePlace(guiW, barH, Mute.State.Hovered);
        Mute.Size = mute.Scale.X;
        Mute.Position = mute.Pos;
        Mute.Visible = true;
        var vol = VolumePlace(guiW, barH, Mute.State.Hovered);
        Planes.Set(Volume, vol.Pos, vol.Scale.X, vol.Scale.Y);
        Volume.Visible = now < SoundPanelUntil;
        foreach (var b in Buttons)
        {
            // a hovered button grows and steps toward the player, the way a hovered skill button does
            var p = ButtonPlace(b.Position.X, barH, b.State.Hovered);
            b.Size = p.Scale.X;
            b.Position = p.Pos;
            b.Visible = true;
        }
    }

    /** The desktop: none of it shows. */
    public void Hide()
    {
        foreach (var b in Buttons) b.Visible = false;
        Mute.Visible = false;
        Volume.Visible = false;
    }
}
