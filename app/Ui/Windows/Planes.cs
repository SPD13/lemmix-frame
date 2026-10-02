using Godot;

namespace Lemmix.App.Ui.Windows;

// The web's windows are 1 x 1 planes scaled to (w, h) metres; a Panel3D's quad is already the
// canvas's aspect, so the same (w, h) is a scale relative to its own size.
public static class Planes
{
    public static void Set(Panel3D p, Vector3 pos, float w, float h)
    {
        p.Position = pos;
        p.Scale = new Vector3(w / p.WidthMetres, h / p.HeightMetres, 1);
    }

    // the (w, h) a panel shows at, in metres: what the web's mesh.scale says
    public static Vector2 SizeOf(Panel3D p) => new(p.Scale.X * p.WidthMetres, p.Scale.Y * p.HeightMetres);
}
