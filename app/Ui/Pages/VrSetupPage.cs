using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;
using Lemmix.Setup;
using Lemmix.Store;

namespace Lemmix.App.Ui.Pages;

// web/setup.html + web/3d/js/setup.js in the headset: NeoLemmix and its styles package (a row
// each with its green or red dot and what is installed), the level directories (with delete), the
// progress bar of an install, the three configuration files, the credits; PLAY in the head once
// everything is there. The native app downloads the three official zips itself (no CORS to work
// around) into the import folder and installs them; "install zip…" lists the zips the player put
// in that folder. The asset mode and the launcher's server are not the native app's, nor are the
// classic DOS games: those parts of the web page are left out.
public sealed class VrSetupPage : VrPage
{
    public const int PAGE_W = 1440, PAGE_H = 1000;
    public const string Where = "headset";        // the web's s.where: "browser" or "server"

    // setup.js KINDS' labels
    static string Label(string kind) => Installer.Labels.GetValueOrDefault(kind) ?? kind;

    // the README's Credits section, shipped with the app (setup.js loadCredits reads it live)
    public static readonly string[] Credits =
    {
        "https://github.com/oklemenz/LemmingsJS - the LemmingsJS engine this repository forks: the DOS games' reimplementation, the 2D page",
        "https://github.com/tomsoftware - the original Lemmings.js the engine descends from",
        "https://www.neolemmix.com - NeoLemmix, whose engine the Lemmix code follows, and whose styles, level packs and music the game plays",
        "Eric Langedijk (for Lemmix), Stephan Neupert and Namida Verasche - the authors of NeoLemmix, whose source is licensed CC BY-NC 4.0 and asks that the three of them be credited; its graphics, music and sounds remain their creators' copyright and are not distributed here",
        "This project was developed with the help of Claude Code - https://claude.com/claude-code",
    };

    public readonly ISetupBackend Backend;
    public readonly IPageConfirm Confirm;

    // the page's state (refresh())
    public SetupUnit? Engine, Styles;
    public IReadOnlyList<SetupDir> LevelDirs = Array.Empty<SetupDir>();
    public bool HasLevels;
    public UploadServerState Upload = UploadServerState.Off;
    public (long Used, long? Available) StorageUse;
    // the message lines: nx, levels, controls, prefs, progress
    public readonly Dictionary<string, ConfigMessage> Messages = new();
    // the progress bar: null label = hidden; null fraction = indeterminate
    public string? ProgressLabel;
    public double? ProgressFrac;
    public bool Busy;
    public string? LastExport;                      // the path the last export wrote (tests)
    CancellationTokenSource? _cancel;
    readonly object _lock = new();
    string? _bgLabel; double? _bgFrac; bool _bgDirty;
    readonly Dictionary<string, Flowed> _flows = new();
    Texture2D? _logo;
    bool _logoTried;

    public VrSetupPage(ISetupBackend backend, IPageConfirm confirm) : base("setup", PAGE_W, PAGE_H, 1.5f)
    {
        Backend = backend;
        Confirm = confirm;
        foreach (var k in new[] { "nx", "levels", "controls", "prefs", "progress" }) Messages[k] = new ConfigMessage("", false);
    }

    public override void Opened() { Refresh(); Paint(); }

    // ---- setup.js say / progress
    public void Say(string id, string text, bool bad = false) => Messages[id] = new ConfigMessage(text ?? "", bad);

    public void Progress(string? label, double? frac = null)
    {
        lock (_lock) { _bgDirty = false; }
        ProgressLabel = label;
        ProgressFrac = frac;
        Busy = label != null;
    }

    // from the background work: the latest only, picked up by Update
    void ProgressFromWork(string label, double? frac)
    {
        lock (_lock) { _bgLabel = label; _bgFrac = frac; _bgDirty = true; }
    }

    protected override bool Poll()
    {
        bool dirty;
        lock (_lock) { dirty = _bgDirty; if (dirty) { ProgressLabel = _bgLabel; ProgressFrac = _bgFrac; _bgDirty = false; } }
        // the indeterminate bar slides: painted every frame while it shows
        return dirty || (ProgressLabel != null && ProgressFrac == null && Root.Visible);
    }

    /** refresh(): the units, the level directories, the storage, the PLAY link. */
    public void Refresh()
    {
        Engine = Backend.Unit("engine");
        Styles = Backend.Unit("styles");
        LevelDirs = Backend.Dirs().ToList(); // its own copy: a delete may be changing the backend's list off the frame
        HasLevels = Backend.HasLevels;
        StorageUse = Backend.Storage();
        Upload = Backend.Upload;
    }

    /** renderPlay: "to play, install …" until NeoLemmix, the styles and a level pack are there. */
    public string PlayWhy()
    {
        var missing = new List<string>();
        if (Engine == null) missing.Add("NeoLemmix");
        if (Styles == null) missing.Add("the styles package");
        if (!HasLevels) missing.Add("a level pack");
        if (missing.Count == 0) return "";
        string list = string.Join(", ", missing);
        int last = list.LastIndexOf(", ", StringComparison.Ordinal);
        if (last >= 0) list = list[..last] + " and " + list[(last + 2)..];
        return "to play, install " + list + " below";
    }

    /** The unit's state line under its row. */
    public static string UnitState(SetupUnit? u)
    {
        if (u == null) return "not installed";
        return "installed" + (u.Version != "" ? " (" + u.Version + ")" : "") +
            (u.Files != null ? ": " + u.Files + " files, " + PageText.Mb(u.Bytes) : "") +
            (u.InstalledAt != null ? ", " + PageText.Date(u.InstalledAt) : "");
    }

    public string StorageText() =>
        "storage used by this app: " + PageText.Mb(StorageUse.Used) + (StorageUse.Available is long a ? " of " + PageText.Mb(a) + " available" : "");

    // ---------------------------------------------------------------- installs
    string MsgOf(string expected) => expected == "levels" ? "levels" : "nx";

    /** installZip: a zip (the import folder's, or a download) installed as `expected` expects. */
    public void InstallZip(string path, string expected)
    {
        if (Busy) return;
        string name = Path.GetFileName(path);
        string msg = MsgOf(expected);
        Say(msg, "");
        List<ZipEntryName> names;
        try { names = Backend.ZipNames(path); }
        catch (Exception) { Say(msg, name + ": not a zip file (no central directory)", true); Paint(); return; }
        string? kind = Installer.DetectKind(names);
        if (kind == null) { Say(msg, name + " does not look like NeoLemmix, its styles package or a level pack", true); Paint(); return; }
        void Go()
        {
            InstallPlan plan;
            try { plan = Backend.Plan(path); }
            catch (Exception e) { Say(msg, "install failed: " + e.Message, true); Paint(); return; }
            if (kind is "engine" or "styles") InstallUnit(kind, path, plan, msg);
            else InstallLevels(path, plan, msg);
        }
        if (kind != expected)
            Confirm.Ask(name + " is " + Label(kind), "install it as that", "It was picked on " + Label(expected) + ".", Go);
        else Go();
    }

    void InstallUnit(string kind, string path, InstallPlan plan, string msgId)
    {
        string label = Label(kind);
        var u = kind == "engine" ? Engine : Styles;
        void Dirs()
        {
            ConfirmReplaceDirs(plan.ClashingDirs, () => Unpack(path, plan, msgId, (files, bytes) =>
                label + " installed on the " + Where + ": " + files + " files, " + PageText.Mb(bytes), "nx"));
        }
        if (u != null)
            Confirm.Ask("Replace " + label + "?", "replace",
                "The " + (u.Files != null ? u.Files + " files" : "files") + " installed" + (u.InstalledAt != null ? " on " + PageText.Date(u.InstalledAt) : "") +
                " on the " + Where + " are removed first.", Dirs);
        else Dirs();
    }

    void InstallLevels(string path, InstallPlan plan, string msgId) =>
        ConfirmReplaceDirs(plan.ClashingDirs, () => Unpack(path, plan, msgId, (files, bytes) =>
            string.Join(", ", plan.Dirs) + " installed on the " + Where + ": " + files + " files, " + PageText.Mb(bytes), "levels"));

    void ConfirmReplaceDirs(List<string> clash, Action go)
    {
        if (clash.Count == 0) { go(); return; }
        Confirm.Ask("Replace " + (clash.Count == 1 ? "the level directory " + clash[0] : clash.Count + " level directories") + "?",
            "replace", string.Join(", ", clash) + " already installed: removed first.", go);
    }

    /** The unpacking, off the frame: the progress bar as it goes, the message when it is done. */
    void Unpack(string path, InstallPlan plan, string msgId, Func<int, long, string> done, string doneMsg)
    {
        string name = Path.GetFileName(path);
        long size = File.Exists(path) ? new FileInfo(path).Length
            : Backend.Files.List(Path.GetDirectoryName(path) ?? "", ".zip").FirstOrDefault(f => f.Path == path)?.Size ?? 0;
        Progress(plan.Replaces != null ? "removing the previous " + plan.Label : "unpacking " + name, null);
        Paint();
        RunBackground(async () =>
        {
            await System.Threading.Tasks.Task.Yield();
            try
            {
                int files = 0;
                var u = Backend.Install(path, plan, name, (frac, _) =>
                {
                    files++;
                    if (frac >= 1) ProgressFromWork("indexing the levels", null);
                    else ProgressFromWork("unpacking " + name + " — " + PageText.Mb((long)(frac * size)) + " of " + PageText.Mb(size) + ", " + files + " files", frac);
                });
                // the whole install: every unit it touched (a level pack's directories)
                int nFiles = plan.Kind == "levels" ? LevelFiles(plan).files : u.Files;
                long nBytes = plan.Kind == "levels" ? LevelFiles(plan).bytes : u.Bytes;
                Post(() => { Say(doneMsg, done(nFiles, nBytes)); Finish(); });
            }
            catch (Exception e) { Post(() => { Say(msgId, "install failed: " + e.Message, true); Finish(); }); }
        });
    }

    (int files, long bytes) LevelFiles(InstallPlan plan)
    {
        var dirs = Backend.Dirs().Where(d => plan.Dirs.Contains(d.Dir)).ToList();
        return (dirs.Sum(d => d.Files ?? 0), dirs.Sum(d => d.Bytes ?? 0));
    }

    void Finish()
    {
        Progress(null);
        Refresh();
    }

    /** "1. get …": the official zip downloaded into the import folder, then installed. */
    public void Download(Downloads.Official what, string kind)
    {
        if (Busy) return;
        string msg = MsgOf(kind);
        Say(msg, "");
        _cancel = new CancellationTokenSource();
        Progress("downloading " + what.Name, null);
        Paint();
        var cancel = _cancel.Token;
        RunBackground(async () =>
        {
            try
            {
                string saved = await Backend.Download(what, (got, total) =>
                    ProgressFromWork("downloading " + what.Name + " — " + PageText.Mb(got) + (total is long t ? " of " + PageText.Mb(t) : ""),
                        total is long tt && tt > 0 ? got / (double)tt : null), cancel);
                Post(() => { Progress(null); InstallZip(saved, kind); });
            }
            catch (Exception e) { Post(() => { Say(msg, "download failed: " + e.Message, true); Finish(); }); }
        });
    }

    /** deleteLevelDir, after its question. */
    public void DeleteDir(string dir)
    {
        if (Busy) return;
        Confirm.Ask("Delete " + dir + "?", "delete", "Its levels leave this " + Where + "'s storage; your progress on them stays.", () =>
        {
            Progress("deleting " + dir, null);
            Paint();
            RunBackground(async () =>
            {
                await System.Threading.Tasks.Task.Yield();
                try { Backend.DeleteDir(dir); Post(() => { Say("levels", dir + " deleted from the " + Where); Finish(); }); }
                catch (Exception e) { Post(() => { Say("levels", "delete failed: " + e.Message, true); Finish(); }); }
            });
        });
    }

    // ---------------------------------------------------------------- configuration
    public void ExportFile(string kind)
    {
        var f = Backend.Export(kind);
        try
        {
            LastExport = Backend.Files.WriteText(Backend.Files.ExportFolder, f.Name, f.Text);
            Say(kind, "saved as " + LastExport);
        }
        catch (Exception e) { Say(kind, f.Name + ": " + e.Message, true); }
    }

    public void ImportFile(string kind, string path)
    {
        string name = Path.GetFileName(path);
        string text;
        try { text = Backend.Files.ReadText(path); }
        catch (Exception e) { Say(kind, name + ": " + e.Message, true); return; }
        var m = Backend.Import(kind, text, name);
        Say(kind, m.Text, m.Bad);
    }

    // ---------------------------------------------------------------- presses
    static readonly string[] Kinds = { "controls", "prefs", "progress" };

    protected override void OnPress(string id)
    {
        switch (id)
        {
            case "play": Backend.Play(); return;
            case "upload-server": Backend.SetUpload(!Upload.On); Refresh(); return;
            case "get-engine": Download(Downloads.Engine, "engine"); return;
            case "get-styles": Download(Downloads.Styles, "styles"); return;
            case "get-packs": Download(Downloads.Packs, "levels"); return;
            case "zip-engine": case "zip-styles": case "zip-levels":
                PickFile(id, ".zip", "msg"); return;
        }
        if (id.StartsWith("del:", StringComparison.Ordinal)) { DeleteDir(id[4..]); return; }
        if (id.StartsWith("dl-", StringComparison.Ordinal)) { ExportFile(id[3..]); return; }
        if (id.StartsWith("ul-", StringComparison.Ordinal)) { PickFile(id, ".json", id[3..]); return; }
    }

    /** The files of the import folder as the popup of the button pressed (the web's file picker). */
    void PickFile(string id, string ext, string msgKind)
    {
        var files = Backend.Files.List(Backend.Files.ImportFolder, ext);
        string msg = msgKind == "msg" ? (id == "zip-levels" ? "levels" : "nx") : msgKind;
        if (files.Count == 0)
        {
            Say(msg, "no " + ext + " file in " + Backend.Files.ImportFolder + " - copy one there first", true);
            return;
        }
        var anchor = RegionRect(id) ?? new Rect2(W / 2, H / 2, U(260), ButtonH);
        anchor.Size = new Vector2(Math.Max(anchor.Size.X, U(360)), anchor.Size.Y);
        OpenPopup(id, files.Select(f => (f.Path, f.Name + " · " + PageText.Mb(f.Size))).ToList(), null, anchor);
    }

    protected override void Choose(string popupId, string value)
    {
        switch (popupId)
        {
            case "zip-engine": InstallZip(value, "engine"); return;
            case "zip-styles": InstallZip(value, "styles"); return;
            case "zip-levels": InstallZip(value, "levels"); return;
        }
        if (popupId.StartsWith("ul-", StringComparison.Ordinal)) ImportFile(popupId[3..], value);
    }

    public override bool OnKey(string code, string? text)
    {
        if (code == "Escape" && Popup != null) { Popup = null; Paint(); return true; }
        return false;
    }

    // ---------------------------------------------------------------- painting
    float X0 => U(30);                // the page's content, left
    float CW => U(840);               // and its width
    string F(float css, bool bold = false) => Font(css, bold);

    Flowed FlowCached(Run[] runs, float w, float indent = 0)
    {
        string key = w + "|" + indent + "|" + string.Join("\u0001", runs.Select(r => r.Font + "\u0002" + r.Color + "\u0002" + r.Text));
        if (_flows.TryGetValue(key, out var f)) return f;
        if (_flows.Count > 400) _flows.Clear();
        return _flows[key] = PageText.Flow(cx, runs, w, indent);
    }

    /** A paragraph: its height, painted when asked. */
    float Para(Run[] runs, float x, float y, float w, float lineCss, bool paint)
    {
        var f = FlowCached(runs, w);
        if (paint) PageText.Paint(cx, f, x, y, U(lineCss));
        return f.Lines * U(lineCss);
    }

    Run Dim(string t) => new(t, F(11), Css.Dim);
    Run DimB(string t) => new(t, F(11, true), Css.Dim);
    Run Lnk(string t) => new(t, F(11), Css.Link);

    float HeadH;

    void Logo(float x, float y, float size)
    {
        if (!_logoTried)
        {
            _logoTried = true;
            if (ResourceLoader.Exists("res://Ui/Pages/logo.png")) _logo = GD.Load<Texture2D>("res://Ui/Pages/logo.png");
        }
        if (_logo != null) cx.drawImage(_logo, x, y, size, size);
    }

    protected override void PaintPage()
    {
        // ---- the head (sticky): logo, title and version, the tagline; PLAY or what is missing
        float padY = U(10), x = X0, right = X0 + CW - U(70); // the window's close sits right of it
        string why = PlayWhy();
        float playW = 0, whyW = 0;
        cx.font = F(12);
        if (why == "") playW = PageText.Spaced(cx, "▶ PLAY", 0, 0, U(1.2f), false) + U(30);
        else whyW = Math.Min(U(300), (right - x) * 0.4f);
        float slotW = why == "" ? playW : whyW;
        float titleX = x + U(62), titleW = right - titleX - slotW - U(14);
        var tag = FlowCached(new[] { Dim("An engine to run legacy Lemmings games and Lemmix engine and levels with 3D rendering and VR compatibility.") }, titleW);
        float titleH = U(24) + U(2) + tag.Lines * U(17.6f);
        var whyFlow = why != "" ? FlowCached(new[] { Dim(why) }, whyW) : null;
        float inner = Math.Max(U(48), Math.Max(titleH, whyFlow != null ? whyFlow.Lines * U(17.6f) : U(29)));
        HeadH = inner + padY * 2;
        float top = padY + (inner - titleH) / 2;
        Logo(x, padY + (inner - U(48)) / 2, U(48));
        cx.font = F(15, true);
        cx.fillStyle = Css.Green;
        float tw = PageText.Spaced(cx, "LEMMIX JS+VR — SETUP", titleX, top + U(12), U(1.2f));
        cx.font = F(11);
        cx.fillStyle = Css.Dim;
        cx.fillText("v" + Backend.Version, titleX + tw + U(10), top + U(12));
        PageText.Paint(cx, tag, titleX, top + U(26), U(17.6f));
        if (why == "")
        {
            float bh = U(29.2f), bx = right - playW, by = padY + (inner - bh) / 2;
            bool hot = Hot("play");
            cx.fillStyle = hot ? "#8ee09b" : Css.Green;
            cx.beginPath();
            cx.roundRect(bx, by, playW, bh, U(4));
            cx.fill();
            cx.fillStyle = Css.Ground;
            cx.font = F(12);
            PageText.Spaced(cx, "▶ PLAY", bx + U(15), by + bh / 2, U(1.2f));
            Hit("play", bx, by, playW, bh);
        }
        else
        {
            // right-aligned lines
            float wy = padY + (inner - whyFlow!.Lines * U(17.6f)) / 2;
            cx.textAlign = "right";
            cx.font = F(11);
            cx.fillStyle = Css.Dim;
            foreach (var p in whyFlow.Parts) cx.fillText(p.Text.TrimEnd(), right, wy + p.Line * U(17.6f) + U(8.8f));
            cx.textAlign = "left";
        }
        cx.fillStyle = Css.Border;
        cx.fillRect(4, HeadH, W - 8, Math.Max(1, U(1)));
        View = new Rect2(4, HeadH + U(1), W - 8, H - HeadH - U(1) - 6);

        // ---- the body
        BeginScroll();
        float y = U(18);
        y += Card(y, "notice", NoticeCard) + U(12);
        y += Card(y, "", NxCard) + U(12);
        y += Card(y, "", LevelsCard);
        if (ProgressLabel != null) y += U(12) + ProgressBar(y + U(12));
        y += U(12);
        y += Card(y, "", UploadCard) + U(12);
        y += Card(y, "", ConfigCard) + U(12);
        y += Card(y, "", CreditsCard) + U(40);
        EndScroll(y);
    }

    /** A .card: measured, its ground painted, then its content over it. */
    float Card(float y, string kind, Func<float, float, float, bool, float> body)
    {
        float ix = X0 + U(15), iw = CW - U(30), iy = y + U(13);
        float h = body(ix, iy, iw, false) + U(26);
        if (InView(y, h))
        {
            cx.fillStyle = Css.Card;
            cx.beginPath();
            cx.roundRect(X0, y, CW, h, U(6));
            cx.fill();
            cx.strokeStyle = kind == "notice" ? "#4a4326" : Css.Border;
            cx.lineWidth = Math.Max(1, U(1));
            cx.stroke();
            body(ix, iy, iw, true);
        }
        return h;
    }

    float H2(string text, float x, float y, bool paint)
    {
        if (paint)
        {
            cx.font = F(13, true);
            cx.fillStyle = Css.Bright;
            PageText.Spaced(cx, text.ToUpperInvariant(), x, y + U(10.4f), U(0.78f));
        }
        return U(20.8f) + U(8);
    }

    float NoticeCard(float x, float y, float w, bool paint)
    {
        var runs = new[]
        {
            new Run("The NeoLemmix engine and the levels are stored locally, on this headset.", F(12, true), Css.NoticeBold),
            new Run(" Nothing installed here goes to a server: it lives in this app's storage on this device, and it is wiped if the app is uninstalled or its data cleared. ", F(12), Css.Yellow),
            new Run("None of the copyrighted assets are shipped with this engine, you will need to bring your own versions.", F(12, true), Css.NoticeBold),
        };
        float h = Para(runs, x, y, w, 19.2f, paint);
        h += Para(new[] { Dim(StorageText()) }, x, y + h, w, 17.6f, paint);
        return h;
    }

    float NxCard(float x, float y, float w, bool paint)
    {
        float y0 = y;
        y += H2("NeoLemmix", x, y, paint);
        y += Para(new[]
        {
            Dim("The files come from "), Lnk("neolemmix.com"),
            Dim(": the first button downloads the official zip from there and installs it, the second installs a zip you saved in the import folder ("
                + Backend.Files.ImportFolder + ")."),
        }, x, y, w, 17.6f, paint);
        y += Para(new[] { Dim("This is the app's storage on this headset: installs land here.") }, x, y, w, 17.6f, paint);
        y += UnitRow("engine", "NeoLemmix", "gfx, sounds, music, the classic styles and its two level packs", Downloads.Engine, Engine, x, y, w, paint);
        y += UnitRow("styles", "Styles package", "every style the level packs use", Downloads.Styles, Styles, x, y, w, paint);
        y += Msg("nx", x, y, w, paint);
        return y - y0;
    }

    /** A .where badge (the web's "browser" one, in its colours). */
    float WhereBadge(float x, float yMid, bool paint)
    {
        cx.font = F(10);
        string t = Where.ToUpperInvariant();
        float w = PageText.Spaced(cx, t, 0, 0, U(0.4f), false) + U(14);
        if (paint)
        {
            float h = U(20);
            cx.strokeStyle = "#6a5b2c";
            cx.lineWidth = Math.Max(1, U(1));
            cx.beginPath();
            cx.roundRect(x, yMid - h / 2, w, h, U(8));
            cx.stroke();
            cx.fillStyle = "#c9b26a";
            PageText.Spaced(cx, t, x + U(7), yMid, U(0.4f));
        }
        return w;
    }

    float UnitRow(string kind, string name, string what, Downloads.Official o, SetupUnit? u, float x, float y, float w, bool paint)
    {
        float top = y + U(6);
        float line1 = U(25.6f), h = U(6) + line1 + U(10) + U(17.6f) + U(6) + U(2);
        if (!paint) return h;
        Row(x, top, w, h);
        float ix = x + U(13), mid = top + U(7) + line1 / 2;
        float cxPos = ix + WhereBadge(ix, mid, true) + U(10);
        // the dot: green when installed, red when not, glowing
        cx.fillStyle = u != null ? "rgba(111, 206, 126, 0.35)" : "rgba(224, 85, 74, 0.35)";
        cx.beginPath(); cx.arc(cxPos + U(5), mid, U(8), 0, Mathf.Tau); cx.fill();
        cx.fillStyle = u != null ? Css.Green : "#e0554a";
        cx.beginPath(); cx.arc(cxPos + U(5), mid, U(5), 0, Mathf.Tau); cx.fill();
        float nameX = cxPos + U(20);
        string get = "1. get " + o.Name + " (" + o.Size + ")", zip = u != null ? "2. re-install zip…" : "2. install zip…";
        float bz = ButtonW(zip), bg = ButtonW(get);
        float bx2 = x + w - U(13) - bz, bx1 = bx2 - U(10) - bg;
        Button("get-" + kind, get, bx1, mid - ButtonH / 2, !Busy);
        Button("zip-" + kind, zip, bx2, mid - ButtonH / 2, !Busy);
        NameWithDim(name, what, nameX, mid, bx1 - U(10) - nameX);
        cx.font = F(11);
        cx.fillStyle = Css.Dim;
        cx.fillText(UnitState(u), ix + U(83), top + U(7) + line1 + U(10) + U(8.8f));
        return h;
    }

    void Row(float x, float y, float w, float h)
    {
        cx.fillStyle = Css.Row;
        cx.beginPath();
        cx.roundRect(x, y, w, h - U(6), U(6));
        cx.fill();
        cx.strokeStyle = Css.Border;
        cx.lineWidth = Math.Max(1, U(1));
        cx.stroke();
    }

    /** .lib-row-name: a name in white and what it is, dim, cut with an ellipsis together. */
    void NameWithDim(string name, string dim, float x, float mid, float maxW, string color = Css.Bright)
    {
        cx.font = F(12);
        float nw = cx.measureText(name).width;
        cx.fillStyle = color;
        if (nw >= maxW) { cx.fillText(PageText.Fit(cx, name, maxW), x, mid); return; }
        cx.fillText(name, x, mid);
        if (dim == "") return;
        cx.font = F(11);
        cx.fillStyle = Css.Dim;
        float sp = cx.measureText(" ").width;
        cx.fillText(PageText.Fit(cx, dim, maxW - nw - sp), x + nw + sp, mid);
    }

    float Msg(string id, float x, float y, float w, bool paint)
    {
        var m = Messages[id];
        float h = U(6);
        if (m.Text == "") return h + U(16);
        var runs = new[] { new Run(m.Text, F(11), m.Bad ? Css.Red : Css.Green) };
        return h + Math.Max(U(16), Para(runs, x, y + h, w, 17.6f, paint));
    }

    float LevelsCard(float x, float y, float w, bool paint)
    {
        float y0 = y;
        y += H2("Levels", x, y, paint);
        y += Para(new[]
        {
            DimB("None of the copyrighted assets ship with this engine."),
            Dim(" You need to own the original game to use the Lemmings and Oh No! More Lemmings levels. The NeoLemmix levels are provided by their authors, and their licences apply. Check the licence of the levels with their respective authors."),
        }, x, y, w, 17.6f, paint);
        y += Para(new[] { Dim("A level zip lands in its own directory (NeoLemmix packs): put the zip in the import folder and install it with the second button.") }, x, y, w, 17.6f, paint);
        y += Para(new[]
        {
            Dim("The Lemmings Plus packs come from "), Lnk("neolemmix.com"),
            Dim(": the first button downloads the official zip from there and installs it, the second installs any level pack zip from the import folder."),
        }, x, y, w, 17.6f, paint);
        // the buttons
        y += U(8);
        string get = "1. get " + Downloads.Packs.Name + " (" + Downloads.Packs.Size + ")";
        if (paint)
        {
            float bx = x;
            bx += Button("get-packs", get, bx, y, !Busy) + U(6);
            Button("zip-levels", "2. install zip…", bx, y, !Busy);
        }
        y += ButtonH;
        y += Para(new[] { Dim("This is the app's storage on this headset: installs land here and delete removes from it.") }, x, y, w, 17.6f, paint);
        if (LevelDirs.Count == 0)
        {
            if (paint)
            {
                cx.font = F(12);
                cx.fillStyle = Css.Dim;
                cx.fillText("no level directory installed", x + U(12), y + U(6) + U(9.6f));
            }
            y += U(31.2f);
        }
        foreach (var d in LevelDirs) y += DirRow(d, x, y, w, paint);
        y += Msg("levels", x, y, w, paint);
        return y - y0;
    }

    // The level upload server: levels do not ship with the app, so a browser on another computer
    // of the network can put them in (LevelServer). Its switch, and the address to type.
    float UploadCard(float x, float y, float w, bool paint)
    {
        float y0 = y;
        var u = Upload;
        y += H2("Upload from a computer", x, y, paint);
        y += Para(new[]
        {
            Dim("Turn on the web server to manage the levels folder from a web browser on a computer connected to the same network as this headset: browse it, upload level files or whole folders, delete folders, and install a level pack's zip."),
        }, x, y, w, 17.6f, paint);
        y += U(8);
        float rowH = U(26);
        if (paint) Checkbox("upload-server", "web server for level uploads", u.On, x, y, rowH, enabled: !Busy, labelColor: Css.Text);
        y += rowH + U(6);
        if (u.On && u.Urls.Count > 0)
        {
            y += Para(new[] { Dim("In the computer's browser, type:") }, x, y, w, 17.6f, paint);
            foreach (var url in u.Urls)
            {
                if (paint)
                {
                    cx.font = F(17, true);
                    cx.fillStyle = Css.Green;
                    cx.fillText(url, x + U(12), y + U(14));
                }
                y += U(28);
            }
            y += Para(new[] { Dim("Anyone on this network can reach it while it is on: turn it off when you are done.") }, x, y, w, 17.6f, paint);
        }
        else if (u.On)
            y += Para(new[] { new Run("on, but this headset has no network address: connect it to Wi-Fi", F(11), Css.Red) }, x, y, w, 17.6f, paint);
        if (u.Error != null)
            y += Para(new[] { new Run(u.Error, F(11), Css.Red) }, x, y, w, 17.6f, paint);
        if (u.Activity != "")
            y += Para(new[] { new Run(u.Activity, F(11), Css.Green) }, x, y, w, 17.6f, paint);
        return y - y0;
    }

    float DirRow(SetupDir d, float x, float y, float w, bool paint)
    {
        float top = y + U(6), line = U(25.6f), h = U(6) + line + U(14) + U(6);
        if (!paint || !InView(y, h)) return h;
        Row(x, top, w, h);
        float ix = x + U(13), mid = top + U(7) + line / 2;
        float nx = ix + WhereBadge(ix, mid, true) + U(10);
        float right = x + w - U(13);
        // right to left: delete, what is known of it, the count, the engine badge
        string del = "delete";
        float bw = ButtonW(del);
        Button("del:" + d.Dir, del, right - bw, mid - ButtonH / 2, !Busy);
        right -= bw + U(10);
        string about = string.Join(" · ", new[] { d.Bytes != null ? PageText.Mb(d.Bytes) : "", d.Files != null ? d.Files + " files" : "", d.Source }.Where(s => s != ""));
        cx.font = F(11);
        if (about != "")
        {
            float aw = Math.Min(cx.measureText(about).width, U(300));
            cx.fillStyle = Css.Dim;
            cx.fillText(PageText.Fit(cx, about, aw), right - aw, mid);
            right -= aw + U(10);
        }
        if (d.Count != null)
        {
            string c = d.Count + " levels";
            float cw = cx.measureText(c).width;
            cx.fillStyle = Css.Dim;
            cx.fillText(c, right - cw, mid);
            right -= cw + U(10);
        }
        string engine = d.Engine ?? "";
        string badge = engine != "" ? engine : "no levels found";
        string col = engine == "lemmix" ? "#ffb066" : engine == "classic" ? Css.Link : Css.Dim;
        string border = engine == "lemmix" ? "#6b4a2f" : engine == "classic" ? "#2f5f6b" : Css.BtnBorder;
        cx.font = F(10);
        float bwid = PageText.Spaced(cx, badge.ToUpperInvariant(), 0, 0, U(0.8f), false) + U(14);
        Chip(badge, right - bwid, mid, "rgba(0,0,0,0)", col, 10, border, upper: true, spacing: 0.8f);
        right -= bwid + U(10);
        string name = d.Name ?? d.Dir;
        // .row.empty: a directory the index has no levels from, its name dim
        NameWithDim(name, d.Name != null && d.Name != d.Dir ? d.Dir : "", nx, mid, right - nx, d.Name != null ? Css.Bright : Css.Dim);
        return h;
    }

    float ProgressBar(float y)
    {
        float x = X0, w = CW, th = U(8);
        cx.fillStyle = Css.Input;
        cx.beginPath();
        cx.roundRect(x, y, w, th, U(4));
        cx.fill();
        cx.strokeStyle = Css.BtnBorder;
        cx.lineWidth = Math.Max(1, U(1));
        cx.stroke();
        cx.fillStyle = Css.Green;
        if (ProgressFrac is double f)
        {
            float fw = (float)Math.Clamp(f, 0, 1) * (w - 2);
            if (fw > 0) cx.fillRect(x + 1, y + 1, fw, th - 2);
        }
        else
        {
            // indeterminate: a 30% slice sliding along
            double t = Time.GetTicksMsec() / 1200.0 % 1;
            float sx = (float)(-0.3 + t * 1.3) * w;
            float a = Math.Max(0, sx), b = Math.Min(w - 2, sx + w * 0.3f);
            if (b > a) cx.fillRect(x + 1 + a, y + 1, b - a, th - 2);
        }
        cx.font = F(11);
        cx.fillStyle = Css.Dim;
        cx.fillText(ProgressLabel ?? "", x, y + th + U(4) + U(8.8f));
        return th + U(4) + U(17.6f);
    }

    float ConfigCard(float x, float y, float w, bool paint)
    {
        float y0 = y;
        y += H2("Configuration", x, y, paint);
        y += Para(new[] { Dim("Your settings live on this headset too. Export them to keep or move them (they land in " + Backend.Files.ExportFolder + "); import a file from the import folder to bring them back.") }, x, y, w, 17.6f, paint);
        y += ConfigRow("controls", "Controls", "keyboard and VR controller bindings", x, y, w, paint);
        y += Msg("controls", x, y, w, paint);
        y += ConfigRow("prefs", "Preferences", "3D effects, default view, sound and music, the VR skills bar's place, the library's order", x, y, w, paint);
        y += Msg("prefs", x, y, w, paint);
        y += ConfigRow("progress", "Progress", "the levels cleared, best times, most lemmings saved, talismans", x, y, w, paint);
        y += Msg("progress", x, y, w, paint);
        return y - y0;
    }

    float ConfigRow(string kind, string name, string what, float x, float y, float w, bool paint)
    {
        float top = y + U(6), line = U(25.6f), h = U(6) + line + U(14) + U(6);
        if (!paint || !InView(y, h)) return h;
        Row(x, top, w, h);
        float ix = x + U(13), mid = top + U(7) + line / 2;
        float nx = ix + WhereBadge(ix, mid, true) + U(10);
        float ub = ButtonW("import…"), db = ButtonW("export");
        float b2 = x + w - U(13) - ub, b1 = b2 - U(6) - db;
        Button("dl-" + kind, "export", b1, mid - ButtonH / 2);
        Button("ul-" + kind, "import…", b2, mid - ButtonH / 2);
        NameWithDim(name, what, nx, mid, b1 - U(10) - nx);
        return h;
    }

    float CreditsCard(float x, float y, float w, bool paint)
    {
        float y0 = y;
        y += H2("Credits", x, y, paint);
        foreach (var item in Credits)
        {
            y += U(4);
            var runs = new List<Run>();
            int at = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(item, @"https?://[^\s<)]+"))
            {
                if (m.Index > at) runs.Add(Dim(item[at..m.Index]));
                runs.Add(Lnk(System.Text.RegularExpressions.Regex.Replace(m.Value, @"^https?://(www\.)?", "")));
                at = m.Index + m.Length;
            }
            if (at < item.Length) runs.Add(Dim(item[at..]));
            y += Para(runs.ToArray(), x, y, w, 17.6f, paint) + U(4);
        }
        return y - y0;
    }
}
