using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.App.Ui;

namespace Lemmix.App.Test;

// Screenshots for review without a headset: `-- --shot <name> <file.png>` renders a named scene
// (in the render box: tools/render.sh, Xvfb + software Vulkan), waits a few frames and saves the
// picture. Each shot is a static method taking the root node and returning the Viewport to grab.
public partial class Shots : Node
{
    static readonly Dictionary<string, Func<Node, Viewport>> All = new(StringComparer.Ordinal)
    {
        ["canvas-demo"] = CanvasDemo,
    };

    string _name = "", _file = "";
    Viewport? _grab;
    int _frames;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().SkipWhile(a => a != "--shot").Skip(1).ToList();
        _name = args.ElementAtOrDefault(0) ?? "";
        _file = args.ElementAtOrDefault(1) ?? "user://shot.png";
        if (!All.TryGetValue(_name, out var make)) { GD.Print("[lemmix] shot unknown: " + _name + " (" + string.Join(", ", All.Keys) + ")"); GetTree().Quit(2); return; }
        _grab = make(this);
    }

    public override void _Process(double delta)
    {
        if (_grab == null || ++_frames < 6) return;
        var img = _grab.GetTexture().GetImage();
        var err = img.SavePng(_file);
        GD.Print($"[lemmix] shot saved {_name} {img.GetWidth()}x{img.GetHeight()} -> {_file} ({err})");
        _grab = null;
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    // every Canvas2D primitive the windows use, on a 512x320 panel
    static Viewport CanvasDemo(Node root)
    {
        var panel = new Panel3D(512, 320, 0.4f, transparent: false);
        root.AddChild(panel);
        var g = panel.Canvas;
        g.fillStyle = "#1c2733"; g.fillRect(0, 0, 512, 320);
        g.strokeStyle = "#36e06c"; g.lineWidth = 4; g.beginPath(); g.roundRect(12, 12, 488, 296, 18); g.stroke();
        g.fillStyle = "rgba(255, 255, 255, 0.9)"; g.font = "bold 28px monospace"; g.textAlign = "center"; g.textBaseline = "middle";
        g.fillText("Restart the level?", 256, 60);
        g.font = "20px monospace"; g.textAlign = "left"; g.textBaseline = "alphabetic"; g.fillStyle = "#cccccc";
        g.fillText("The attempt so far is lost.", 40, 110);
        g.fillStyle = "#2e5f46"; g.beginPath(); g.roundRect(60, 200, 150, 64, 12); g.fill();
        g.fillStyle = "#5f2e2e"; g.beginPath(); g.roundRect(302, 200, 150, 64, 12); g.fill();
        g.fillStyle = "white"; g.font = "bold 24px monospace"; g.textAlign = "center"; g.textBaseline = "middle";
        g.fillText("YES", 135, 232); g.fillText("NO", 377, 232);
        g.strokeStyle = "#ffd200"; g.lineWidth = 3; g.beginPath(); g.arc(256, 160, 22, 0, Mathf.Tau); g.stroke();
        g.beginPath(); g.moveTo(246, 150); g.lineTo(270, 160); g.lineTo(246, 170); g.closePath(); g.fillStyle = "#ffd200"; g.fill();
        panel.Commit();
        return panel.GetChild<SubViewport>(0);
    }
}
