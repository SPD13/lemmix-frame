using Godot;
using Lemmix.App.Session;
using Lemmix.Engine;
using Lemmix.Render;

namespace Lemmix.App.Shell;

// The lobby's room (native): while no level is on the board, the room round the player is the
// Dirt gallery's (orig_dirt: its scenery to the horizon, or envgen's rings and haze, as the
// environment switch says), as a level of that style would have it - the room placed as for a
// board of a typical level's size standing where a level's would. A level loading takes the room
// over with its own; leaving it for the lobby brings the Dirt one back.
public sealed partial class App
{
    public const string LobbyStyle = "orig_dirt";
    const int LobbyBoardW = 1600, LobbyBoardH = 160;   // a typical level's size, for the room's layout
    bool _lobbyRoom;                                     // the lobby's room is the one up

    /** The Dirt gallery's context: its style's theme and profile, no level of its own. */
    EnvContext? LobbyRoomContext()
    {
        var styles = new StyleManager(Io);
        var style = styles.Style(LobbyStyle);
        if (!style.Exists) return null;
        var profile = EnvProfile.ForStyle(LobbyStyle, url => GameSession.ReadProfile("res://Data/profiles", url[(url.LastIndexOf('/') + 1)..]));
        return new EnvContext
        {
            Engine = "lemmix", LevelId = "lobby:" + LobbyStyle, Width = LobbyBoardW, Height = LobbyBoardH,
            ThemeName = LobbyStyle, Theme = style.Theme, Profile = profile,
        };
    }

    // per frame: the room put up once the lobby is (with the first head pose), then kept placed
    void LobbyRoomFrame(bool presenting)
    {
        if (Session != null || !presenting || FirstRun || Vr.LastHeadPose is not Transform3D head) return;
        if (!_lobbyRoom)
        {
            _lobbyRoom = true;
            if (LobbyRoomContext() is not { } ctx) { GD.PushWarning("[app] lobby room: no " + LobbyStyle + " style"); return; }
            // where a level's board would stand: the room is laid out round it (the diorama's root is free without a level)
            DioramaRoot.Transform = BoardPlacement(head.Origin, LobbyBoardW, LobbyBoardH);
            Env.SetMode(Fx.Environment);
            _ = Env.SetLevel(ctx, Io, Options.EnvironmentInBackground);
            Env.PlaceForXR(DioramaRoot.Transform, head.Origin);
        }
        Env.Update(head.Origin, DioramaRoot, presenting);
    }
}
