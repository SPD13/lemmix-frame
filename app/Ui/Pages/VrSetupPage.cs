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
//
// Read in a headset, it departs from the web page (device session 1, 2 Oct 2026): a larger scale
// (2.1 canvas px a CSS px, the content 600 CSS px wide: the same canvas width), body text bright
// rather than dim, one big "Get …" button per download and no zip button (a zip installs from a
// computer, through the upload server), the help cut to where the files come from.
public sealed class VrSetupPage : VrPage
{
    public const int PAGE_W = 1440, PAGE_H = 1000;
    public const string Where = "headset";        // the web's s.where: "browser" or "server"

    // setup.js KINDS' labels
    static string Label(string kind) => Installer.Labels.GetValueOrDefault(kind) ?? kind;

    // the app's credits (the web reads its README's live, setup.js loadCredits); the LemmingsJS
    // engine is not part of this app, so it is not credited here
    public static readonly string[] Credits =
    {
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
    CancellationTokenSource? _cancel;
    readonly object _lock = new();
    string? _bgLabel; double? _bgFrac; bool _bgDirty;
    readonly Dictionary<string, Flowed> _flows = new();
    Texture2D? _logo;
    bool _logoTried;

    public VrSetupPage(ISetupBackend backend, IPageConfirm confirm) : base("setup", PAGE_W, PAGE_H, 2.1f)
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

    // ---------------------------------------------------------------- presses
    protected override void OnPress(string id)
    {
        switch (id)
        {
            case "play": Backend.Play(); return;
            case "upload-server": Backend.SetUpload(!Upload.On); Refresh(); return;
            case "get-engine": Download(Downloads.Engine, "engine"); return;
            case "get-styles": Download(Downloads.Styles, "styles"); return;
            case "get-packs": Download(Downloads.Packs, "levels"); return;
        }
        if (id.StartsWith("del:", StringComparison.Ordinal)) { DeleteDir(id[4..]); return; }
    }

    public override bool OnKey(string code, string? text)
    {
        if (code == "Escape" && Popup != null) { Popup = null; Paint(); return true; }
        return false;
    }

    // ---------------------------------------------------------------- painting
    float X0 => U(21);                // the page's content, left
    float CW => U(600);               // and its width
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

    Run Dim(string t) => new(t, F(11), Css.Text);
    Run DimB(string t) => new(t, F(11, true), Css.Text);
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
        float tw = PageText.Spaced(cx, "LEMMIX VR Steam Frame — SETUP", titleX, top + U(12), U(1.2f));
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

    // the "Get …" buttons: larger than the page's others
    const float GetCss = 15, GetH = 40;
    public static readonly Dictionary<string, string> GetLabel = new()
    {
        ["engine"] = "Get NeoLemmix", ["styles"] = "Get Style Packages", ["packs"] = "Get Lemmings Plus Packs",
    };

    float GetButton(string kind, float x, float y) => Button("get-" + kind, GetLabel[kind], x, y, !Busy, "primary", GetCss, height: U(GetH));

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
        y += Para(new[] { Dim("The files come from "), Lnk("neolemmix.com") }, x, y, w, 17.6f, paint);
        y += UnitRow("engine", "NeoLemmix", Engine, x, y, w, paint);
        y += UnitRow("styles", "Styles", Styles, x, y, w, paint);
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

    float UnitRow(string kind, string name, SetupUnit? u, float x, float y, float w, bool paint)
    {
        float top = y + U(6);
        float line1 = U(GetH), h = U(8) + line1 + U(8) + U(17.6f) + U(8) + U(2);
        if (!paint) return h;
        Row(x, top, w, h);
        float ix = x + U(13), mid = top + U(8) + line1 / 2;
        // the dot: green when installed, red when not, glowing
        cx.fillStyle = u != null ? "rgba(111, 206, 126, 0.35)" : "rgba(224, 85, 74, 0.35)";
        cx.beginPath(); cx.arc(ix + U(8), mid, U(8), 0, Mathf.Tau); cx.fill();
        cx.fillStyle = u != null ? Css.Green : "#e0554a";
        cx.beginPath(); cx.arc(ix + U(8), mid, U(5), 0, Mathf.Tau); cx.fill();
        float nameX = ix + U(24);
        float bw = ButtonW(GetLabel[kind], GetCss), bx = x + w - U(13) - bw;
        GetButton(kind, bx, mid - U(GetH) / 2);
        cx.font = F(14, true);
        cx.fillStyle = Css.Bright;
        cx.fillText(PageText.Fit(cx, name, bx - U(10) - nameX), nameX, mid);
        cx.font = F(11);
        cx.fillStyle = Css.Text;
        cx.fillText(UnitState(u), nameX, top + U(8) + line1 + U(8) + U(8.8f));
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
        y += Para(new[] { Dim("The files come from "), Lnk("neolemmix.com"), Dim(". The levels' licences are their authors'.") }, x, y, w, 17.6f, paint);
        y += U(6);
        if (paint) GetButton("packs", x, y);
        y += U(GetH) + U(10);
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

    // a level directory: its name large, what it holds under it, a delete button (in a headset the
    // web row's badges and source name only crowd it)
    float DirRow(SetupDir d, float x, float y, float w, bool paint)
    {
        float top = y + U(6), line = U(22), h = U(8) + line + U(4) + U(17.6f) + U(8) + U(6);
        if (!paint || !InView(y, h)) return h;
        Row(x, top, w, h);
        float ix = x + U(13), right = x + w - U(13);
        string del = "delete";
        float bh = U(34), bw = ButtonW(del, 13);
        Button("del:" + d.Dir, del, right - bw, top + (h - U(6) - bh) / 2, !Busy, "warn", 13, height: bh);
        float maxW = right - bw - U(14) - ix;
        // .row.empty: a directory the index has no levels from, its name dim
        cx.font = F(14, true);
        cx.fillStyle = d.Name != null ? Css.Bright : Css.Dim;
        cx.fillText(PageText.Fit(cx, d.Name ?? d.Dir, maxW), ix, top + U(8) + line / 2);
        string about = string.Join(" · ", new[]
        {
            d.Count != null ? d.Count + " levels" : d.Engine == null ? "no levels found" : "",
            d.Bytes != null ? PageText.Mb(d.Bytes) : "",
        }.Where(t => t != ""));
        cx.font = F(11);
        cx.fillStyle = Css.Text;
        cx.fillText(PageText.Fit(cx, about, maxW), ix, top + U(8) + line + U(4) + U(8.8f));
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
