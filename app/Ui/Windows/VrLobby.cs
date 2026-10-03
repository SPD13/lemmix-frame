using System;
using System.Collections.Generic;
using Godot;
using Lemmix.App.Xr;
using Bitmap = Lemmix.Engine.Bitmap;
using Lemmix.Io;
using Lemmix.Ui;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// The lobby (native, the app's title screen): NeoLemmix's main menu (GameMenuScreen.pas) as a
// screen standing in the room before the player - its background tiled, its logo, its footer and
// its scroller with the two worker lemmings - with three signs held up by lemmings floating in
// front of it, each the headset's own: PLAY (the world catalog), VR SETTINGS (the VR window, as
// the bar's VR button opens it), QUIT (the app ends). A sign under the beam glows as NeoLemmix's
// does under the mouse and steps toward the player. Up while no level is on the board. It hangs in
// the windows' frame, further off than the windows, so they open in front of it, upright (square
// to the floor) as they are. The art is the
// installed NeoLemmix's (TitleArt); without it the screen and the signs are drawn plainly.
public sealed class VrLobby
{
    // the screen: this wide, this far off in the windows' frame, its centre on the line the
    // windows are opened along (WindowPitch below the eyes); the signs stand off it toward the player
    public const float WIDTH = 1.5f, Z = -1.4f, SIGN_FRONT = 0.12f, SIGN_HOVER_FRONT = 0.03f, SIGN_HOVER_SCALE = 1.06f;
    public const float PX = WIDTH / TitleArt.ScreenW;     // metres a screen pixel
    const int ScreenK = 2, SignK = 4, ScrollerK = 2;      // canvas pixels a NeoLemmix pixel

    public static readonly string[] Tools = { "lobbyplay", "lobbyvr", "lobbyquit" };
    static readonly string[] Labels = { "PLAY", "VR SETTINGS", "QUIT" };
    static readonly string[] Captions =
    {
        "Play: choose a world and a level",
        "VR settings: view height, foveation",
        "Quit: leave Lemmix",
    };

    // the scroller's lines: the headset's, then NeoLemmix's credits (data/scroller.nxmi)
    public static readonly string[] ScrollerLines =
    {
        "Lemmix in virtual reality",
        "Point a controller at a sign and pull the trigger",
        "Grip the board to move it, both grips to scale it",
        "The thumbsticks pan, tilt and zoom the view",
        "Built on NeoLemmix, developed by namida and Nepster",
        "With contributions by WillLem, Simon, Anders Melander",
        "Additional graphical work by WillLem and zanzindorf",
        "Thanks to the Lemmings Forums community",
        "and to DMA Design for the original Lemmings game",
    };

    public readonly Node3D Root = new() { Name = "vr-lobby", Visible = false };
    public readonly Panel3D Screen, Scroller;
    public readonly Panel3D[] Signs = new Panel3D[3];
    // a veil over the screen and its signs while a window or a page stands in front of them
    public readonly MeshInstance3D Shade;
    public string? Hover;
    public string Version = "";
    public float WindowPitch;                              // the windows' frame looks down this much

    TitleArt? _art;
    TitleScroller? _scroller;
    Image? _scrollerImage;
    ImageTexture? _screenTex, _scrollerTex;
    readonly ImageTexture?[] _signTex = new ImageTexture?[3], _signHotTex = new ImageTexture?[3];
    readonly Dictionary<string, ImageTexture> _footer = new();

    public VrLobby()
    {
        Screen = new Panel3D(TitleArt.ScreenW * ScreenK, TitleArt.ScreenH * ScreenK, WIDTH, transparent: false) { Name = "vr-lobbyscreen" };
        Root.AddChild(Screen);
        int sw = TitleArt.ScrollerWidth + 96, sh = 28;
        Scroller = new Panel3D(sw * ScrollerK, sh * ScrollerK, sw * PX) { Name = "vr-lobbyscroller", Visible = false };
        Root.AddChild(Scroller);
        for (int i = 0; i < Signs.Length; i++)
        {
            Signs[i] = new Panel3D(130 * SignK, 97 * SignK, 130 * PX) { Name = "vr-" + Tools[i] };
            Root.AddChild(Signs[i]);
        }
        Shade = new MeshInstance3D
        {
            Name = "vr-lobbyshade", Visible = false,
            Mesh = new QuadMesh { Size = new Vector2(WIDTH, WIDTH * TitleArt.ScreenH / TitleArt.ScreenW) },
            Position = new Vector3(0, 0, 0.008f),   // on the screen, over its scroller
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.02f, 0.03f, 0.05f, 0.93f), // (blended in linear light: about a quarter left)
            },
        };
        Root.AddChild(Shade);
        Load(null);
    }

    /** A window or a page up in front: the screen veiled behind it, the signs (which it owns the beam from) put away. */
    public void SetShaded(bool on)
    {
        if (Shade.Visible == on) return;
        Shade.Visible = on;
        foreach (var sign in Signs) sign.Visible = !on;
        if (on) SetHover(null);
    }

    static ImageTexture Texture(Bitmap b)
    {
        using var img = Image.CreateFromData(b.Width, b.Height, false, Image.Format.Rgba8, b.Data);
        return ImageTexture.CreateFromImage(img);
    }

    /** The art from the asset root (null, or not installed: the plain screen); painted again. */
    public void Load(IFileSource? io)
    {
        _art = io != null ? TitleArt.Load(io) : null;
        _footer.Clear();
        _scroller = null;
        if (_art is { Ok: true } a)
        {
            _screenTex = Texture(a.Screen!);
            var signs = new[] { a.Play!, a.VrSettings!, a.Quit! };
            for (int i = 0; i < 3; i++) { _signTex[i] = Texture(signs[i].Normal); _signHotTex[i] = Texture(signs[i].Hover); }
            if (a.Font != null && a.ScrollerLemmings != null && a.ScrollerSegment != null)
            {
                _scroller = new TitleScroller(a.Font, a.ScrollerLemmings, a.ScrollerSegment, ScrollerLines);
                var s = _scroller.Strip;
                Scroller.Resize(s.Width * ScrollerK, s.Height * ScrollerK); // (the reel is taller than NeoLemmix's)
                _scrollerImage = Image.CreateFromData(s.Width, s.Height, false, Image.Format.Rgba8, s.Data);
                _scrollerTex = ImageTexture.CreateFromImage(_scrollerImage);
            }
        }
        Scroller.Visible = _scroller != null;
        Layout();
        PaintScreen();
        for (int i = 0; i < Signs.Length; i++) PaintSign(i);
        PaintScroller();
    }

    public bool HasArt => _art is { Ok: true };

    // ---- painting
    // the footer's two lines (MakeFooterText's places): what the hovered sign does, or how to choose;
    // the app and its version
    (string, string) FooterLines()
    {
        int i = Array.IndexOf(Tools, Hover);
        return (i >= 0 ? Captions[i] : "Point at a sign and pull the trigger", "Lemmix for Steam Frame" + (Version != "" ? " V" + Version : ""));
    }

    ImageTexture? FooterTexture(string line)
    {
        if (_art?.Font is not MenuFont font) return null;
        if (!_footer.TryGetValue(line, out var t)) _footer[line] = t = Texture(font.Render(line));
        return t;
    }

    public void PaintScreen()
    {
        var cx = Screen.Canvas;
        int w = TitleArt.ScreenW * ScreenK, h = TitleArt.ScreenH * ScreenK;
        cx.clearRect(0, 0, w, h);
        var (l1, l2) = FooterLines();
        if (_screenTex != null)
        {
            cx.drawImage(_screenTex, 0, 0, w, h);
            int y = TitleArt.FooterTextY;
            foreach (var line in new[] { l1, l2 })
            {
                if (FooterTexture(line) is { } t)
                    cx.drawImage(t, (TitleArt.ScreenW - t.GetWidth()) / 2 * ScreenK, y * ScreenK, t.GetWidth() * ScreenK, t.GetHeight() * ScreenK);
                y += 3 * MenuFont.CharH;
            }
        }
        else
        {
            // no NeoLemmix art: a plain screen with the title and the footer
            cx.fillStyle = "#3a0e06";
            cx.fillRect(0, 0, w, h);
            cx.strokeStyle = "#ffd866";
            cx.lineWidth = 8;
            cx.strokeRect(4, 4, w - 8, h - 8);
            cx.fillStyle = "#6fce7e";
            cx.font = "bold 150px monospace";
            cx.textAlign = "center";
            cx.fillText("LEMMIX", w / 2f, TitleArt.LogoCenterY * ScreenK + 50);
            cx.fillStyle = "#f0f3f8";
            cx.font = "36px monospace";
            cx.fillText(l1, w / 2f, TitleArt.FooterTextY * ScreenK + 36);
            cx.fillStyle = "#8fa1bb";
            cx.fillText(l2, w / 2f, (TitleArt.FooterTextY + 3 * MenuFont.CharH) * ScreenK + 36);
            cx.textAlign = "left";
        }
        Screen.Commit();
    }

    void PaintSign(int i)
    {
        var p = Signs[i];
        var cx = p.Canvas;
        int w = p.Canvas.Width, h = p.Canvas.Height;
        bool hot = Hover == Tools[i];
        cx.clearRect(0, 0, w, h);
        if ((hot ? _signHotTex[i] : _signTex[i]) is { } tex && HasArt) cx.drawImage(tex, 0, 0, w, h);
        else
        {
            string[] fills = { "#134f1d", "#e8d020", "#a30000" };
            cx.fillStyle = fills[i];
            cx.beginPath();
            cx.roundRect(24, 60, w - 48, h - 120, 18);
            cx.fill();
            cx.strokeStyle = hot ? "#ffffff" : "#000000";
            cx.lineWidth = hot ? 10 : 6;
            cx.stroke();
            cx.fillStyle = i == 1 ? "#202020" : "#ffffff";
            cx.font = "bold 56px monospace";
            cx.textAlign = "center";
            cx.fillText(Labels[i], w / 2f, h / 2f + 20);
            cx.textAlign = "left";
        }
        p.Commit();
    }

    void PaintScroller()
    {
        if (_scroller == null || _scrollerTex == null || _scrollerImage == null) return;
        var s = _scroller.Strip;
        _scrollerImage.SetData(s.Width, s.Height, false, Image.Format.Rgba8, s.Data);
        _scrollerTex.Update(_scrollerImage);
        var cx = Scroller.Canvas;
        cx.clearRect(0, 0, cx.Width, cx.Height);
        cx.drawImage(_scrollerTex, 0, 0, s.Width * ScrollerK, s.Height * ScrollerK);
        Scroller.Commit();
    }

    // ---- placing
    // a screen pixel's place on the lobby's root (its centre at the root's origin)
    static Vector3 At(float px, float py, float z) => new((px - TitleArt.ScreenW / 2f) * PX, (TitleArt.ScreenH / 2f - py) * PX, z);

    public void Layout()
    {
        // on the line the windows open along: as far below the windows' as the extra distance makes it
        float y = VR_MODAL_Y - (-Z + VR_MODAL_Z) * MathF.Tan(WindowPitch);
        Root.Position = new Vector3(0, y, Z);
        // upright, square to the floor, as the windows stand
        Root.Rotation = Vector3.Zero;
        // NeoLemmix's reel's middle stays where it was; the taller reel grows round it
        Scroller.Position = At(TitleArt.ScreenW / 2f, TitleArt.ScrollerTopY + 14, 0.004f);
        for (int i = 0; i < Signs.Length; i++)
        {
            bool hot = Hover == Tools[i];
            var (cx, cy) = TitleArt.CardCentre(i - 1);
            Signs[i].Position = At(cx, cy, SIGN_FRONT + (hot ? SIGN_HOVER_FRONT : 0));
            Signs[i].Scale = Vector3.One * (hot ? SIGN_HOVER_SCALE : 1);
        }
    }

    // ---- the beam
    /** What the beam is on: a sign (its tool), or the screen itself (null tool, a hit that ends the beam). */
    public (VrPick? Pick, float Distance, bool OnScreen) Pick(Vector3 origin, Vector3 dir)
    {
        if (!Root.IsVisibleInTree()) return (null, 0, false);
        float best = float.MaxValue;
        string? tool = null;
        for (int i = 0; i < Signs.Length; i++)
        {
            var px = Signs[i].Hit(origin, dir, out float d);
            if (px is not Vector2 p || d >= best || !SignAt(i, p)) continue;
            best = d;
            tool = Tools[i];
        }
        if (tool != null) return (new VrPick("bar", BarTool: tool), best, true);
        if (Screen.Hit(origin, dir, out float sd) != null) return (null, sd, true);
        return (null, 0, false);
    }

    // a sign is hit where it is drawn (its board, its lemming), not in its margin's glow
    bool SignAt(int i, Vector2 canvasPx)
    {
        if (!HasArt) return true;
        var sign = i switch { 0 => _art!.Play!, 1 => _art!.VrSettings!, _ => _art!.Quit! };
        var bmp = sign.Normal;
        int x = (int)(canvasPx.X / SignK), y = (int)(canvasPx.Y / SignK);
        // a little slack round the art: the hand-drawn edge is ragged
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int sx = x + dx, sy = y + dy;
                if (sx >= 0 && sy >= 0 && sx < bmp.Width && sy < bmp.Height && bmp.Data[(sy * bmp.Width + sx) * 4 + 3] > 0) return true;
            }
        return false;
    }

    /** The beam's sign (or none): lit, stepped forward, the footer saying what it does. */
    public void SetHover(string? tool)
    {
        if (tool != null && Array.IndexOf(Tools, tool) < 0) tool = null;
        if (Hover == tool) return;
        string? was = Hover;
        Hover = tool;
        for (int i = 0; i < Signs.Length; i++)
            if (Tools[i] == was || Tools[i] == tool) PaintSign(i);
        PaintScreen();
        Layout();
    }

    /** A shot's: the reel run on until its line stands centred. */
    public void CentreScrollerText()
    {
        if (_scroller == null) return;
        for (int i = 0; i < 5000 && _scroller.Freeze == 0; i++) _scroller.Step();
        _scroller.Draw();
        PaintScroller();
    }

    /** Per frame while up: the scroller's reel turns. */
    public void Update(double nowMs)
    {
        if (!Root.Visible || _scroller == null) return;
        if (_scroller.Update(nowMs)) PaintScroller();
    }
}
