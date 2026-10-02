using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Lemmix.App.Xr;

// What the app answers for the VR layer (web/3d/js/vr.js VRManager hooks).
public interface IVrHooks
{
    // what the ray lands on, or null: the toolbar (with a BarTool name), a window, the board…
    VrPick? Pick(Vector3 origin, Vector3 direction);
    // where the ray hits, for the beam's length and the dot
    VrHit? RaycastHit(Vector3 origin, Vector3 direction);
    void OnSelectPick(VrPick pick);
    void OnHoverPick(VrPick? pick);
    void OnVrButton(string code, bool down) { }
    void OnVrButtonHeld(string code, double dt) { }
    void OnStick(string code, float x, float y, double dt) { }
    void OnBarDragStart() { }
    void OnBarDrag(Vector3 worldDelta) { }
    void OnBarDragEnd() { }
    void OnScrubEnd(string? scrub) { }
    void OnScrubOff(string? scrub) { }
    // position the diorama for the headset; false to try again next frame
    bool PlaceDiorama(Transform3D? headPose);
    void OnRecenter(Transform3D? headPose) { }
}

public sealed record VrPick(string Kind, string? BarTool = null, bool ScrollBar = false, bool Minimap = false, bool Scrubbing = false, object? Data = null);
public sealed record VrHit(Vector3 Point, float Distance, bool OnBoard);

// web/3d/js/vr.js VRManager - the headset's input on the diorama:
// - trigger = the desktop click; one hand points at a time and carries the beam; the right
//   starts with it; a trigger pull on the other hand takes it over (and does nothing else);
// - trigger held and the hand moved past VR_DRAG_THRESHOLD drags the board in all three axes;
// - grip drags the board; both grips scale it (0.15x..8x) about the hands' midpoint;
// - face buttons, stick clicks and sticks are reported by the hand's role (Point/Free) to the
//   controls table.
public sealed class VrManager
{
    public const float VR_PIXEL_SCALE = 0.0025f; // meters per game pixel (1600px level -> 4m)
    public const float VR_GUI_WIDTH = 0.6f, VR_GUI_Y = -0.3f, VR_GUI_Z = -0.75f;
    public const float VR_BAR_FRONT = 0.03f;
    public const float GUI_VR_RELIEF_DEPTH = 1;
    public const float VR_BAR_TOOL_SIZE = 0.045f, VR_BAR_TOOL_HOVER = 1.18f;
    public const float VR_VOLUME_HEIGHT = 0.15f;
    public const int VR_SOUND_LINGER = 2000;
    public const float VR_MODAL_WIDTH = 0.42f, VR_MODAL_Y = 0.02f, VR_MODAL_Z = VR_GUI_Z;
    public const float VR_SETTINGS_WIDTH = 0.34f;
    public const float VR_CATALOG_WIDTH = 0.62f, VR_CATALOG_Y = 0.06f;
    public const int VR_MARK_ORDER = 60;
    public const float VR_STICK_DEADZONE = 0.15f;
    public const float VR_STICK_PAN = 0.8f, VR_STICK_TILT = 1.0f;
    public const float VR_ZOOM_RATE = 2.0f, VR_ZOOM_NEAR = 0.2f, VR_ZOOM_FAR = 6.0f;
    public const float VR_DRAG_THRESHOLD = 0.02f;

    sealed class Press
    {
        public int Hand;
        public Vector3 From;
        public Vector3 RootFrom;
        public bool Bar, Slider, Button, Dragging;
        public string? Scrub;
    }

    sealed class Grab
    {
        public int Mode;
        public int A, B;
        public Vector3 CStart, PStart, Mid0;
        public float D0, S0;
    }

    readonly IXrInput _input;
    readonly IVrHooks _hooks;
    public readonly Node3D DioramaRoot;
    string _pointerHand = "right";
    int? _aiming;
    Press? _press;
    Grab? _grab;
    readonly bool[] _trigger = new bool[2], _squeeze = new bool[2];
    HashSet<string> _buttonsDown = new();
    bool _needsPlacement;
    bool _wasPresenting;
    readonly VrHit?[] _lastHit = new VrHit?[2];

    public VrManager(IXrInput input, IVrHooks hooks, Node3D dioramaRoot)
    {
        _input = input;
        _hooks = hooks;
        DioramaRoot = dioramaRoot;
    }

    public bool Presenting => _input.Presenting;
    public Transform3D? LastHeadPose { get; private set; }
    public string PointerHand => _pointerHand;
    public int? AimingHand => _aiming;
    public VrHit? LastHit(int hand) => _lastHit[hand];
    public bool BeamVisible(int hand) => _input.Hands[hand].Connected && IsPointer(hand);

    // Re-place the diorama - and whatever else follows a recentre - from the latest head pose.
    public void RecenterNow()
    {
        _grab = null;
        _hooks.PlaceDiorama(LastHeadPose);
        _hooks.OnRecenter(LastHeadPose);
    }

    public void ResetDiorama()
    {
        DioramaRoot.Position = Vector3.Zero;
        DioramaRoot.Rotation = Vector3.Zero;
        DioramaRoot.Scale = Vector3.One;
    }

    // The hand that points: the right; an unnamed controller points as well, and so does a lone
    // left one, since otherwise nothing would.
    bool HandPoints(string? hand)
    {
        if (hand == null) return true;
        if (hand == _pointerHand) return true;
        return !_input.Hands.Any(h => h.Connected && h.Handedness == _pointerHand);
    }

    bool IsPointer(int i) => HandPoints(_input.Hands[i].Handedness);

    static (Vector3 Origin, Vector3 Dir) Ray(HandState h) => (h.Aim.Origin, -h.Aim.Basis.Z.Normalized());

    void OnSelectStart(int i)
    {
        if (!Presenting) return;
        var h = _input.Hands[i];
        if (h.Handedness != null && h.Handedness != _pointerHand)
        {
            _pointerHand = h.Handedness;
            _aiming = null;
            _press = null;
            return;
        }
        if (!IsPointer(i)) return;
        var (o, d) = Ray(h);
        var on = _hooks.Pick(o, d);
        bool slider = on != null && (on.BarTool == "volume" || on.ScrollBar || on.Minimap);
        bool bar = on != null && on.BarTool == "move";
        _press = new Press
        {
            Hand = i, From = h.Aim.Origin, RootFrom = DioramaRoot.Position,
            Bar = bar, Slider = slider, Scrub = slider ? on!.BarTool : null,
            Button = on?.BarTool != null && !bar && !slider,
            Dragging = slider,
        };
        if (slider) _hooks.OnSelectPick(on!);
    }

    void OnSelectEnd(int i)
    {
        var p = _press;
        _press = null;
        if (p != null && p.Hand == i && p.Slider) _hooks.OnScrubEnd(p.Scrub);
        if (p != null && p.Hand == i && p.Bar && p.Dragging) _hooks.OnBarDragEnd();
        if (p == null || p.Hand != i || p.Dragging || !Presenting) return;
        var (o, d) = Ray(_input.Hands[i]);
        var pick = _hooks.Pick(o, d);
        if (pick != null) _hooks.OnSelectPick(pick);
    }

    void UpdateDrag()
    {
        var p = _press;
        if (p == null || _grab != null) return;
        var h = _input.Hands[p.Hand];
        if (p.Slider)
        {
            var (o, d) = Ray(h);
            var on = _hooks.Pick(o, d);
            if (on != null && on.BarTool == p.Scrub) _hooks.OnSelectPick(on with { Scrubbing = true });
            else _hooks.OnScrubOff(p.Scrub);
            return;
        }
        if (p.Button) return;
        var delta = h.Aim.Origin - p.From;
        if (!p.Dragging && delta.Length() < VR_DRAG_THRESHOLD) return;
        if (!p.Dragging && p.Bar) _hooks.OnBarDragStart();
        p.Dragging = true;
        if (p.Bar) _hooks.OnBarDrag(delta);
        else DioramaRoot.Position = p.RootFrom + delta;
    }

    // (Re)baseline the grab whenever the set of gripping controllers changes.
    void GrabBaseline()
    {
        var g = Enumerable.Range(0, 2).Where(i => _squeeze[i]).ToList();
        if (g.Count == 1)
            _grab = new Grab { Mode = 1, A = g[0], CStart = _input.Hands[g[0]].Grip.Origin, PStart = DioramaRoot.Position };
        else if (g.Count == 2)
        {
            Vector3 a = _input.Hands[g[0]].Grip.Origin, b = _input.Hands[g[1]].Grip.Origin;
            _grab = new Grab
            {
                Mode = 2, A = g[0], B = g[1], D0 = Math.Max(a.DistanceTo(b), 0.01f),
                Mid0 = (a + b) * 0.5f, S0 = DioramaRoot.Scale.X, PStart = DioramaRoot.Position,
            };
        }
        else _grab = null;
    }

    void PollButtons(double dt)
    {
        var now = new HashSet<string>();
        foreach (var h in _input.Hands)
        {
            if (!h.Connected) continue;
            string role = HandPoints(h.Handedness) ? "Point" : "Free";
            if (h.Lower) now.Add("Vr" + role + "A");
            if (h.Upper) now.Add("Vr" + role + "B");
            if (h.StickClick) now.Add("Vr" + role + "StickClick");
        }
        foreach (var code in now)
        {
            if (!_buttonsDown.Contains(code)) _hooks.OnVrButton(code, true);
            else if (dt > 0) _hooks.OnVrButtonHeld(code, dt);
        }
        foreach (var code in _buttonsDown) if (!now.Contains(code)) _hooks.OnVrButton(code, false);
        _buttonsDown = now;
    }

    void PollSticks(double dt)
    {
        if (dt <= 0) return;
        foreach (var h in _input.Hands)
        {
            if (!h.Connected) continue;
            static float Dead(float v) => Math.Abs(v) < VR_STICK_DEADZONE ? 0 : v;
            float x = Dead(h.Stick.X), y = Dead(h.Stick.Y);
            if (x == 0 && y == 0) continue;
            _hooks.OnStick(HandPoints(h.Handedness) ? "VrPointStick" : "VrFreeStick", x, -y, dt);
        }
    }

    // Per frame: session edges, trigger/grip edges, grabs, sticks, hover from the aiming hand.
    public void Update(double dt)
    {
        _input.Poll();
        bool presenting = Presenting;
        if (presenting && !_wasPresenting) _needsPlacement = true;            // sessionstart
        if (!presenting && _wasPresenting) ResetDiorama();                    // sessionend
        _wasPresenting = presenting;
        if (!presenting) return;
        LastHeadPose = _input.Head;
        if (_needsPlacement && LastHeadPose != null)
        {
            try { if (_hooks.PlaceDiorama(LastHeadPose)) _needsPlacement = false; }
            catch (Exception e) { GD.PushError("[vr] placement failed: " + e.Message); _needsPlacement = false; }
        }
        // the select/squeeze events of WebXR, from the button edges
        for (int i = 0; i < 2; i++)
        {
            var h = _input.Hands[i];
            bool trig = h.Connected && h.Trigger, sq = h.Connected && h.Squeeze;
            if (trig && !_trigger[i]) { _trigger[i] = true; OnSelectStart(i); }
            else if (!trig && _trigger[i]) { _trigger[i] = false; OnSelectEnd(i); }
            if (sq != _squeeze[i]) { _squeeze[i] = sq; GrabBaseline(); }
        }
        PollButtons(dt);
        PollSticks(dt);
        UpdateDrag();
        var g = _grab;
        if (g != null && g.Mode == 1 && _squeeze[g.A])
            DioramaRoot.Position = g.PStart + (_input.Hands[g.A].Grip.Origin - g.CStart);
        else if (g != null && g.Mode == 2 && _squeeze[g.A] && _squeeze[g.B])
        {
            Vector3 a = _input.Hands[g.A].Grip.Origin, b = _input.Hands[g.B].Grip.Origin;
            float k = Math.Clamp(a.DistanceTo(b) / g.D0, 0.15f, 8f);
            DioramaRoot.Scale = Vector3.One * (g.S0 * k);
            var mid = (a + b) * 0.5f;
            DioramaRoot.Position = mid - (g.Mid0 - g.PStart) * k; // the point between the hands stays under them
        }
        bool hasControllers = _input.Hands.Any(h => h.Connected);
        int? aiming = null;
        float aimingDist = float.PositiveInfinity;
        for (int i = 0; i < 2; i++)
        {
            var h = _input.Hands[i];
            _lastHit[i] = null;
            if (!h.Connected || !IsPointer(i)) continue;
            var (o, d) = Ray(h);
            var hit = _hooks.RaycastHit(o, d);
            _lastHit[i] = hit;
            if (hit == null) continue;
            if (i == _aiming) { aiming = i; aimingDist = -1; }
            else if (hit.Distance < aimingDist) { aimingDist = hit.Distance; aiming = i; }
        }
        if (hasControllers)
        {
            _aiming = aiming;
            if (aiming is int a2)
            {
                var (o, d) = Ray(_input.Hands[a2]);
                _hooks.OnHoverPick(_hooks.Pick(o, d));
            }
            else _hooks.OnHoverPick(null);
        }
    }

    // The beam's length for a hand this frame (4 m when it lands on nothing).
    public float BeamLength(int hand) => _lastHit[hand] is { } hit ? Math.Max(hit.Distance, 0.05f) : 4f;
}
