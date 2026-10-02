using System;
using Godot;

namespace Lemmix.App.Ui.Windows;

// The state an icon is painted in (web/3d/js/app.js makeIconButton's userData.state): on (paused,
// muted, unlocked, a list shown…) and hovered (the beam is on it).
public sealed class IconState
{
    public bool On, Hovered;
}

// web/3d/js/app.js: the drawn 64x64 icons of the headset's buttons (barToolIcon and each button's
// painter), line for line - the same coordinates, colours and strokes.
public static class BarIcons
{
    const float PI = Mathf.Pi;

    // barToolIcon: the rounded square, the white outline under the beam, then the body's strokes
    public static void BarToolIcon(Canvas2D cx, bool hovered, string bg, string stroke, Action<Canvas2D> body)
    {
        cx.clearRect(0, 0, 64, 64);
        cx.fillStyle = bg;
        cx.beginPath();
        cx.roundRect(2, 2, 60, 60, 12);
        cx.fill();
        if (hovered)
        {
            // the same white outline the skill panel puts round a chosen button
            cx.strokeStyle = "#ffffff";
            cx.lineWidth = 4;
            cx.beginPath();
            cx.roundRect(4, 4, 56, 56, 10);
            cx.stroke();
        }
        cx.strokeStyle = stroke;
        cx.lineWidth = 5;
        cx.lineCap = "round";
        cx.lineJoin = "round";
        body(cx);
    }

    // padlock: shackle up and open when the bar floats free, closed when it rides the head
    public static void Lock(Canvas2D cx, IconState st)
    {
        bool unlocked = st.On;
        BarToolIcon(cx, st.Hovered,
            unlocked ? (st.Hovered ? "#6b6036" : "#4a4326")
                     : (st.Hovered ? "#1d5030" : "#12331d"),
            unlocked ? "#ffd866" : "#6fce7e", c =>
            {
                c.strokeRect(18, 32, 28, 20);
                c.beginPath();
                if (unlocked) c.arc(42, 28, 10, PI, PI * 1.9f); // swung open
                else c.arc(32, 30, 10, PI, 0);
                c.stroke();
            });
    }

    // four-way arrows: grab here to move the bar
    public static void Move(Canvas2D cx, IconState st)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#33405a" : "#1c2432", "#cdd6e4", c =>
        {
            c.beginPath();
            c.moveTo(32, 14); c.lineTo(32, 50);
            c.moveTo(14, 32); c.lineTo(50, 32);
            foreach (var (x, y, dx, dy) in new (float, float, float, float)[] { (32, 14, 0, 1), (32, 50, 0, -1), (14, 32, 1, 0), (50, 32, -1, 0) })
            {
                c.moveTo(x - 7 * (dy != 0 ? 1 : 0) + 7 * dx, y - 7 * (dx != 0 ? 1 : 0) + 7 * dy);
                c.lineTo(x, y);
                c.lineTo(x + 7 * (dy != 0 ? 1 : 0) + 7 * dx, y + 7 * (dx != 0 ? 1 : 0) + 7 * dy);
            }
            c.stroke();
        });
    }

    // a board over a bar, with an arrow between: put the bar back where a session starts it
    public static void Park(Canvas2D cx, IconState st)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#33405a" : "#1c2432", "#cdd6e4", c =>
        {
            c.strokeRect(17, 12, 30, 16);        // the board
            c.fillStyle = "#cdd6e4";
            c.fillRect(13, 44, 38, 7);           // the bar
            c.beginPath();                        // the arrow down to it
            c.moveTo(32, 30); c.lineTo(32, 41);
            c.moveTo(26, 36); c.lineTo(32, 41); c.lineTo(38, 36);
            c.stroke();
        });
    }

    // pause, which becomes a play triangle once the game is stopped (on: paused)
    public static void Pause(Canvas2D cx, IconState st)
    {
        bool paused = st.On;
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#1d5030" : "#12331d", "#6fce7e", c =>
        {
            c.fillStyle = "#6fce7e";
            if (paused)
            {                       // a play triangle: press to resume
                c.beginPath();
                c.moveTo(24, 16); c.lineTo(48, 32); c.lineTo(24, 48);
                c.closePath();
                c.fill();
            }
            else
            {                            // two bars: press to pause
                c.fillRect(22, 17, 8, 30);
                c.fillRect(34, 17, 8, 30);
            }
        });
    }

    // an arrow curving right round, its head on the arc's tip
    public static void Restart(Canvas2D cx, IconState st)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#4a4326" : "#33301c", "#ffd866", c =>
        {
            float mx = 32, my = 32, r = 13;
            float from = PI / 6, to = from + PI * 5 / 3;
            c.beginPath();
            c.arc(mx, my, r, from, to);
            c.stroke();
            float ex = mx + r * Mathf.Cos(to), ey = my + r * Mathf.Sin(to);
            float tx = -Mathf.Sin(to), ty = Mathf.Cos(to);   // the way it is going
            float nx = Mathf.Cos(to), ny = Mathf.Sin(to);    // across the stroke
            float head = 9, half = 6.5f;
            c.fillStyle = "#ffd866";
            c.beginPath();
            c.moveTo(ex + tx * head, ey + ty * head);      // the point
            c.lineTo(ex + nx * half, ey + ny * half);
            c.lineTo(ex - nx * half, ey - ny * half);
            c.closePath();
            c.fill();
        });
    }

    // the solution: a play triangle in a ring, in the catalog's cyan (on: the solution is the replay)
    public static void Solution(Canvas2D cx, IconState st)
    {
        bool on = st.On;
        BarToolIcon(cx, st.Hovered, on ? (st.Hovered ? "#26485c" : "#173442") : (st.Hovered ? "#33405a" : "#1c2432"), "#7fd6e8", c =>
        {
            c.beginPath();
            c.arc(32, 32, 15, 0, PI * 2);
            c.stroke();
            c.fillStyle = "#7fd6e8";
            c.beginPath();
            c.moveTo(26, 22); c.lineTo(26, 42); c.lineTo(42, 32);
            c.closePath();
            c.fill();
        });
    }

    // skip-track arrows, flanking restart
    public static void Nav(Canvas2D cx, IconState st, bool back)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#33405a" : "#1c2432", "#cdd6e4", c =>
        {
            c.fillStyle = "#cdd6e4";
            c.fillRect(back ? 18 : 40, 18, 6, 28);   // the bar it stops against
            c.beginPath();
            if (back) { c.moveTo(46, 18); c.lineTo(46, 46); c.lineTo(27, 32); }
            else { c.moveTo(18, 18); c.lineTo(18, 46); c.lineTo(37, 32); }
            c.closePath();
            c.fill();
        });
    }
    public static void Prev(Canvas2D cx, IconState st) => Nav(cx, st, true);
    public static void Next(Canvas2D cx, IconState st) => Nav(cx, st, false);

    // globe: the way into the world catalog
    public static void Worlds(Canvas2D cx, IconState st)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#26485c" : "#152a36", "#7fd6e8", c =>
        {
            c.lineWidth = 3.5f;
            c.beginPath();
            c.arc(32, 32, 20, 0, PI * 2);            // the globe
            c.moveTo(12, 32); c.lineTo(52, 32);      // equator
            c.stroke();
            c.beginPath();
            c.ellipse(32, 32, 9.5f, 20, 0, 0, PI * 2); // one meridian
            c.stroke();
            // two parallels: their ends meet the rim, and each sags toward the equator
            c.beginPath();
            c.moveTo(16, 20); c.quadraticCurveTo(32, 26, 48, 20);
            c.moveTo(16, 44); c.quadraticCurveTo(32, 38, 48, 44);
            c.stroke();
        });
    }

    // speaker, with waves when it is on and a cross when it is not (on: muted)
    public static void Mute(Canvas2D cx, IconState st)
    {
        bool muted = st.On;
        BarToolIcon(cx, st.Hovered,
            muted ? (st.Hovered ? "#5a2a2a" : "#33201c") : (st.Hovered ? "#1d5030" : "#12331d"),
            muted ? "#e07a6a" : "#6fce7e", c =>
            {
                c.fillStyle = muted ? "#e07a6a" : "#6fce7e";
                c.beginPath();                                  // cone and box
                c.moveTo(14, 26); c.lineTo(22, 26); c.lineTo(32, 16);
                c.lineTo(32, 48); c.lineTo(22, 38); c.lineTo(14, 38);
                c.closePath();
                c.fill();
                c.beginPath();
                if (muted)
                {
                    c.moveTo(38, 24); c.lineTo(52, 40);
                    c.moveTo(52, 24); c.lineTo(38, 40);
                }
                else
                {
                    c.arc(34, 32, 10, -PI / 3, PI / 3);
                    c.moveTo(34 + 17 * Mathf.Cos(-PI / 3), 32 + 17 * Mathf.Sin(-PI / 3));
                    c.arc(34, 32, 17, -PI / 3, PI / 3);
                }
                c.stroke();
            });
    }

    // the relief profile on its slab: the 3D effects (the settings window)
    public static void Settings(Canvas2D cx, IconState st)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#33405a" : "#1c2432", "#cdd6e4", c =>
        {
            c.beginPath();
            c.moveTo(10, 46); c.lineTo(10, 38); c.lineTo(22, 22); c.lineTo(31, 32); c.lineTo(42, 16); c.lineTo(54, 38); c.lineTo(54, 46);
            c.closePath();
            c.stroke();
        });
    }

    // three lines of text: the level's own text, off the status strip's end
    public static void Detail(Canvas2D cx, IconState st)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#26485c" : "#152a36", "#7fd6e8", c =>
        {
            c.beginPath();
            c.moveTo(16, 20); c.lineTo(48, 20);
            c.moveTo(16, 32); c.lineTo(48, 32);
            c.moveTo(16, 44); c.lineTo(36, 44);
            c.stroke();
        });
    }

    // the question's answers
    public static void Yes(Canvas2D cx, IconState st)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#1d5030" : "#12331d", "#6fce7e", c =>
        {
            c.beginPath();
            c.moveTo(16, 33); c.lineTo(28, 45); c.lineTo(49, 20);
            c.stroke();
        });
    }

    // a red cross: "no", and the windows' close
    public static void Cross(Canvas2D cx, IconState st)
    {
        BarToolIcon(cx, st.Hovered, st.Hovered ? "#5a2a2a" : "#33201c", "#e07a6a", c =>
        {
            c.beginPath();
            c.moveTo(19, 19); c.lineTo(45, 45);
            c.moveTo(45, 19); c.lineTo(19, 45);
            c.stroke();
        });
    }

    // a clock: the levels played (on: that list is up)
    public static void Recent(Canvas2D cx, IconState st)
    {
        bool on = st.On;
        BarToolIcon(cx, st.Hovered, on ? (st.Hovered ? "#245232" : "#16281a") : (st.Hovered ? "#33405a" : "#1c2432"),
            on ? "#6fce7e" : "#cdd6e4", c =>
            {
                c.beginPath();
                c.arc(32, 32, 19, 0, PI * 2);
                c.moveTo(32, 20); c.lineTo(32, 33); c.lineTo(41, 39);
                c.stroke();
            });
    }

    // a five-pointed star, centred on (x, y) with the outer radius r
    public static void StarPath(Canvas2D c, float x, float y, float r)
    {
        c.beginPath();
        for (int i = 0; i < 10; i++)
        {
            float a = -PI / 2 + i * PI / 5;
            float d = i % 2 != 0 ? r * 0.45f : r;
            if (i != 0) c.lineTo(x + Mathf.Cos(a) * d, y + Mathf.Sin(a) * d);
            else c.moveTo(x + Mathf.Cos(a) * d, y + Mathf.Sin(a) * d);
        }
        c.closePath();
    }

    // the star: empty while not a favorite, full and yellow while it is (on: the favorites list is up)
    public static void Favorite(Canvas2D cx, IconState st)
    {
        bool on = st.On;
        BarToolIcon(cx, st.Hovered, on ? (st.Hovered ? "#4a4326" : "#2a2412") : (st.Hovered ? "#33405a" : "#1c2432"),
            on ? "#ffd866" : "#cdd6e4", c =>
            {
                StarPath(c, 32, 33, 19);
                if (on) { c.fillStyle = "#ffd866"; c.fill(); }
                c.stroke();
            });
    }

    // every headset button's painter, by its bare name (the mesh is "vr-" + name)
    public static Action<Canvas2D, IconState>? ByName(string name) => name switch
    {
        "lock" => Lock, "move" => Move, "park" => Park, "settings" => Settings, "pause" => Pause,
        "restart" => Restart, "solution" => Solution, "prev" => Prev, "next" => Next, "worlds" => Worlds,
        "mute" => Mute, "detail" => Detail, "yes" => Yes, "no" => Cross, "catclose" => Cross,
        "catrecent" => Recent, "catfav" => Favorite, "setclose" => Cross,
        _ => null,
    };
}
