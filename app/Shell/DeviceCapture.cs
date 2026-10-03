using Godot;

namespace Lemmix.App.Shell;

// A picture of what the player sees, asked for over ssh during a device session: when
// user://capture.request appears (touch it), the next frames render the scene once from the head's
// pose - the same world, renderer and draw order as the headset, one wide mono view - into
// user://capture-<n>.png, and the request goes. tools/frame-capture.sh asks and pulls it.
public sealed partial class DeviceCapture : Node
{
    public const string Request = "user://capture.request";
    readonly Node3D _head;
    double _poll;
    SubViewport? _vp;
    int _frames, _count;

    public DeviceCapture(Node3D head) { _head = head; Name = "device-capture"; }

    public override void _Process(double delta)
    {
        if (_vp != null)
        {
            if (++_frames < 3) return;
            string path = "user://capture-" + (++_count) + ".png";
            var err = _vp.GetTexture().GetImage().SavePng(path);
            GD.Print($"[capture] {ProjectSettings.GlobalizePath(path)} ({err})");
            _vp.QueueFree();
            _vp = null;
            return;
        }
        _poll += delta;
        if (_poll < 0.5) return;
        _poll = 0;
        if (!FileAccess.FileExists(Request)) return;
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(Request));
        _vp = new SubViewport
        {
            Size = new Vector2I(1600, 1200), World3D = GetViewport().World3D, Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var cam = new Camera3D { Fov = 90, Near = 0.05f, Far = 300, Current = true };
        _vp.AddChild(cam);
        AddChild(_vp);
        cam.GlobalTransform = _head.GlobalTransform;
        _frames = 0;
    }
}
