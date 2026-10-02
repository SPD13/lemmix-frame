namespace Lemmix.Engine;

// The classic engine's pieces the Lemmix Game reuses (web js/lemmings.js): EventHandler,
// GameStateTypes, GameResult, CommandManager and the Command* classes, so commands log and
// replay the way the web version does.

public sealed class EventHandler<T>
{
    List<Action<T>> _handlers = new();
    public void On(Action<T> h) => _handlers.Add(h);
    public void Off(Action<T> h) => _handlers = _handlers.Where(x => x != h).ToList();
    public void Dispose() => _handlers = new();
    public void Trigger(T arg) { foreach (var h in _handlers.ToList()) h(arg); }
}

public enum GameStateTypes { UNKNOWN = 0, RUNNING = 1, FAILED_OUT_OF_TIME = 2, FAILED_LESS_LEMMINGS = 3, SUCCEEDED = 4 }

// Lemmings.GameResult: what onGameEnd carries (its constructor reads the state, which can nuke)
public sealed class ClassicGameResult
{
    public ClassicGameResult(Game game)
    {
        State = game.GetGameState();
        Replay = game.CommandManager.Serialize();
        SurvivorPercentage = game.VictoryCondition.GetSurvivorPercentage();
        Survivors = game.VictoryCondition.GetSurvivorsCount();
        Duration = game.GameTimer.GetGameTicks();
    }
    public GameStateTypes State { get; }
    public string Replay { get; }
    public int SurvivorPercentage { get; }
    public int Survivors { get; }
    public int Duration { get; }
}

public interface ICommand
{
    string CommandKey { get; }
    void Load(double[] values);
    int[] Save();
    bool Execute(Game game);
}

// CommandLemmingsAction: the selected skill to a lemming, by id
public sealed class CommandLemmingsAction : ICommand
{
    public int? LemmingId;
    public CommandLemmingsAction(int? lemmingId = null) { if (lemmingId != null) LemmingId = lemmingId; }
    public string CommandKey => "l";
    public void Load(double[] values) { if (values.Length < 1) return; LemmingId = (int)values[0]; }
    public int[] Save() => new[] { LemmingId ?? 0 };
    public bool Execute(Game game)
    {
        var lemManager = game.LemmingManager;
        var lem = LemmingId is int id ? lemManager.GetLemming(id) : null;
        if (lem == null) return false; // "Lemming not found!"
        var skills = game.Skills;
        int selectedSkill = skills.GetSelectedSkill();
        if (!skills.CanReduseSkill(selectedSkill)) return false; // "Not enough skills!"
        if (!lemManager.DoLemmingAction(lem, selectedSkill)) return false; // "unable to execute action on lemming!"
        return skills.ReduseSkill(selectedSkill);
    }
}

public sealed class CommandNuke : ICommand
{
    public string CommandKey => "n";
    public void Load(double[] values) { }
    public int[] Save() => Array.Empty<int>();
    public bool Execute(Game game)
    {
        var lemManager = game.LemmingManager;
        if (lemManager.IsNuking()) return false;
        lemManager.DoNukeAllLemmings();
        game.VictoryCondition.DoNuke();
        return true;
    }
}

public sealed class CommandReleaseRateDecrease : ICommand
{
    public int Number;
    public CommandReleaseRateDecrease(int? number = null) { if (number != null) Number = number.Value; }
    public string CommandKey => "d";
    public void Load(double[] values) { if (values.Length < 1) return; Number = (int)values[0]; }
    public int[] Save() => new[] { Number };
    public bool Execute(Game game) => game.VictoryCondition.ChangeReleaseRate(-Number);
}

public sealed class CommandReleaseRateIncrease : ICommand
{
    public int Number;
    public CommandReleaseRateIncrease(int? number = null) { if (number != null) Number = number.Value; }
    public string CommandKey => "i";
    public void Load(double[] values) { if (values.Length < 1) return; Number = (int)values[0]; }
    public int[] Save() => new[] { Number };
    public bool Execute(Game game) => game.VictoryCondition.ChangeReleaseRate(Number);
}

public sealed class CommandSelectSkill : ICommand
{
    // `if (skill) this.skill = skill;` in the JS: index 0 is falsy, so a command for the first
    // panel skill carries no skill and selects nothing. Kept as the web version behaves.
    public int? Skill;
    public CommandSelectSkill(int? skill = null) { if (skill is int s && s != 0) Skill = s; }
    public string CommandKey => "s";
    public void Load(double[] values) { Skill = values.Length > 0 ? (int)values[0] : null; }
    public int[] Save() => new[] { Skill ?? 0 };
    public bool Execute(Game game) => game.Skills.SetSelectedSkill(Skill);
}

// manages commands user -> game
public sealed class CommandManager
{
    readonly Game _game;
    readonly GameTimer _gameTimer;
    public readonly Dictionary<int, ICommand> RunCommands = new();
    public readonly SortedDictionary<int, ICommand> LoggedCommands = new(); // JS object with integer keys: numeric order

    public CommandManager(Game game, GameTimer gameTimer)
    {
        _game = game;
        _gameTimer = gameTimer;
        _gameTimer.OnBeforeGameTick.On(tick =>
        {
            if (RunCommands.TryGetValue(tick, out var command)) QueueCommand(command);
        });
    }

    // the DOS replay string: "<tick>=<key><values>&…"
    public void LoadReplay(string replayString)
    {
        foreach (string part in replayString.Split('&'))
        {
            var commandStr = part.Split('=', 2);
            if (commandStr.Length != 2) continue;
            int tick = Util.JsMath.ToInt32(double.TryParse(commandStr[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : double.NaN);
            var cmd = ParseCommand(commandStr[1]);
            if (cmd != null) RunCommands[tick] = cmd;
        }
    }

    static ICommand? CommandFactory(string type) => type.ToLowerInvariant() switch
    {
        "l" => new CommandLemmingsAction(),
        "n" => new CommandNuke(),
        "s" => new CommandSelectSkill(),
        "i" => new CommandReleaseRateIncrease(),
        "d" => new CommandReleaseRateDecrease(),
        _ => null,
    };

    static ICommand? ParseCommand(string valuesStr)
    {
        if (valuesStr.Length < 1) return null;
        var cmd = CommandFactory(valuesStr[..1]);
        cmd?.Load(valuesStr[1..].Split(':').Select(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (v == "" ? 0 : double.NaN)).ToArray());
        return cmd;
    }

    // add a command to execute queue (it runs at once; only executable commands are logged)
    public void QueueCommand(ICommand newCommand)
    {
        int currentTick = _gameTimer.GetGameTicks();
        if (newCommand.Execute(_game)) LoggedCommands[currentTick] = newCommand;
    }

    public string Serialize() =>
        string.Join("&", LoggedCommands.Select(kv => kv.Key + "=" + kv.Value.CommandKey + string.Join(":", kv.Value.Save())));
}
