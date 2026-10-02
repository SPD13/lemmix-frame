#!/usr/bin/env node
// webshots.js - the web version's own VR windows, as reference pictures and data for the C# port
// (app/Ui/Windows). The page runs in headless Chrome (no headset: the windows exist, hidden, and
// their painters are driven by hand, through window.__lem3d and the meshes' own userData). For
// every window state it writes:
//   build/webshots/<window>-<state>.png   the canvas the window's texture is made of
//   build/webshots/fixture.json           what the port needs to paint the same states: the
//                                         catalog's items (with their miniatures), the level text,
//                                         the settings, the status; the draw-call trace of every
//                                         paint; and the layout the web gives the meshes (metres)
// The fixture is copied to app/Test/Fixtures/windows.json, which the app's shots and tests read.
//
// Needs the web working copy WITH assets served on 8124:
//   cd ../LemmingsJS && npx http-server -p 8124 -c-1 --silent &
//   node oracle/webshots.js
"use strict";
const fs = require("fs");
const path = require("path");
const puppeteer = require("puppeteer-core");

const ROOT = path.resolve(__dirname, "..");
const OUT = path.join(ROOT, "build", "webshots");
const APP_FIXTURE = path.join(ROOT, "app", "Test", "Fixtures", "windows.json");
const CHROME = process.env.CHROME || "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const BASE = process.env.WEB_URL || "http://127.0.0.1:8124/";
const LEVEL = "NeoLemmix_Introduction_Pack/Skills/Amphibious_Engineer_Squad.nxlv";

// what this browser has played: a few of the Skills levels cleared, every level of one directory
// cleared (its row goes green), favorites and recent levels from several packs
const SEED = {
  cleared: {
    "NeoLemmix_Introduction_Pack/Skills/Just_Digging_Into_NeoLemmix.nxlv": 42,
    "NeoLemmix_Introduction_Pack/Skills/Let's_Take_A_Bash_At_It!.nxlv": 75,
    "NeoLemmix_Introduction_Pack/Skills/Fence_&Mine_For_A_Diagonal_Line.nxlv": 128,
  },
  clearedDir: "NeoLemmix_Introduction_Pack/Objects_&_Functions",
  favorites: [
    "NeoLemmix_Introduction_Pack/Skills/Let's_Take_A_Bash_At_It!.nxlv",
    "Lemmings_Redux/Gentle/Just_dig!.nxlv",
    LEVEL,
  ],
  recent: [
    LEVEL,
    "Lemmings_Redux/Gentle/Only_floaters_can_survive_this.nxlv",
    "NeoLemmix_Introduction_Pack/Skills/Just_Digging_Into_NeoLemmix.nxlv",
    "Lemmings_Redux/Gentle/Just_dig!.nxlv",
    "LemmingsPlus_All_20201114/Lemmings_Plus_I/Mild/Just_Walk!.nxlv",
  ],
};

async function main() {
  fs.mkdirSync(OUT, { recursive: true });
  const index = JSON.parse(fs.readFileSync(path.join(ROOT, "..", "LemmingsJS", "levels", "index.json"), "utf8"));
  const find = (n, p) => (n.path === p ? n : (n.children || []).map((c) => find(c, p)).find(Boolean));
  const dir = find(index, SEED.clearedDir);
  const cleared = {};
  for (const [id, s] of Object.entries(SEED.cleared)) cleared[id] = { best: s };
  dir.levels.forEach((l, i) => { cleared[l.id] = { best: 30 + i * 7 }; });

  const browser = await puppeteer.launch({
    executablePath: CHROME, headless: "new",
    args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--no-first-run", "--window-size=1280,800"],
  });
  try {
    const page = await browser.newPage();
    page.on("pageerror", (e) => console.log("[page error]", e.message));
    if (process.env.VERBOSE) page.on("console", (m) => console.log("[page]", m.text().slice(0, 300)));
    const step = (s) => { if (process.env.VERBOSE) console.log("-- " + s); };
    await page.evaluateOnNewDocument((seed) => {
      try {
        localStorage.setItem("lem3d-cleared", JSON.stringify(seed.cleared));
        localStorage.setItem("lem3d-favorites", JSON.stringify(seed.favorites));
        localStorage.setItem("lem3d-recent", JSON.stringify(seed.recent));
        localStorage.removeItem("lem3d-bar");
        localStorage.removeItem("lem3d-lib-path");
      } catch (e) { /* first load before the origin has storage */ }
    }, { cleared, favorites: SEED.favorites, recent: SEED.recent });
    await page.goto(BASE + "?assets=server#" + LEVEL.split("/").map(encodeURIComponent).join("/"), { waitUntil: "load" });
    await page.waitForFunction(() => window.__lem3d && window.__lem3d.session && window.__lem3d.session.gui.mesh, { timeout: 90000 });
    await new Promise((r) => setTimeout(r, 1500));

    // the helpers live in the page: a recorder of a canvas context's calls, and a grab
    await page.evaluate(() => {
      const L = window.__lem3d;
      const scene = L.dioramaRoot.parent;
      const byName = (name) => { let hit = null; scene.traverse((o) => { if (!hit && o.name === name) hit = o; }); return hit; };
      const num = (v) => (typeof v === "number" ? Math.round(v * 1000) / 1000 : v);
      const num6 = (v) => (typeof v === "number" ? Math.round(v * 1e6) / 1e6 : v); // layout: metres
      const SETTERS = ["fillStyle", "strokeStyle", "lineWidth", "font", "textAlign", "textBaseline", "lineCap", "lineJoin", "globalAlpha", "imageSmoothingEnabled"];
      const METHODS = ["save", "restore", "translate", "scale", "rotate", "beginPath", "moveTo", "lineTo", "closePath", "rect", "roundRect",
        "arc", "ellipse", "quadraticCurveTo", "bezierCurveTo", "fill", "stroke", "fillRect", "strokeRect", "clearRect", "fillText", "drawImage", "clip"];
      /** Record what is drawn on this canvas's context from now on, into `trace`. */
      const record = (canvas) => {
        const cx = canvas.getContext("2d");
        if (cx.__rec) return cx;
        cx.__rec = true;
        cx.__trace = null;
        const proto = CanvasRenderingContext2D.prototype;
        for (const k of SETTERS) {
          const d = Object.getOwnPropertyDescriptor(proto, k);
          Object.defineProperty(cx, k, {
            get() { return d.get.call(this); },
            set(v) { if (this.__trace) this.__trace.push(k + "=" + num(v)); d.set.call(this, v); },
          });
        }
        for (const k of METHODS) {
          const f = proto[k];
          cx[k] = function (...args) {
            if (this.__trace) {
              const shown = k === "drawImage" ? ["img"].concat(args.slice(1)) : args;
              this.__trace.push(k + "(" + shown.map((a) => (typeof a === "string" ? JSON.stringify(a) : num(a))).join(",") + ")");
            }
            return f.apply(this, args);
          };
        }
        return cx;
      };
      const canvasOf = (mesh) => mesh.material.map.image;
      /** Run a paint with the context recording; the canvas and the calls it made. */
      const traced = (mesh, paint) => {
        let cx = record(canvasOf(mesh));
        cx.__trace = [];
        paint();
        const cv = canvasOf(mesh); // a resized canvas keeps its context; a fresh texture keeps the canvas
        cx = cv.getContext("2d");
        const trace = cx.__trace || [];
        cx.__trace = null;
        return { png: cv.toDataURL("image/png"), w: cv.width, h: cv.height, trace };
      };
      const pose = (o) => {
        o.updateWorldMatrix(true, false);
        return { pos: o.position.toArray().map(num6), quat: o.quaternion.toArray().map(num6), scale: o.scale.toArray().map(num6), visible: o.visible };
      };
      window.__shots = { L, scene, byName, record, traced, canvasOf, pose, num, num6 };
    });

    const shots = {};   // name -> { png, w, h, trace }
    const fixture = { level: LEVEL, shots: {}, catalog: {}, layout: {}, thumbs: {} };
    const keep = (name, shot, extra) => {
      shots[name] = shot;
      fixture.shots[name] = Object.assign({ w: shot.w, h: shot.h, trace: shot.trace }, extra || {});
    };

    step('the icon buttons, every state they paint');
    // ---- the icon buttons, every state they paint
    const icons = await page.evaluate(() => {
      const S = window.__shots;
      const out = {};
      const ON = ["lock", "pause", "solution", "mute", "catrecent", "catfav"];
      S.scene.traverse((o) => {
        if (!o.name.startsWith("vr-") || !o.userData.repaint) return;
        const name = o.name.slice(3);
        const st = o.userData.state;
        const was = Object.assign({}, st);
        const variants = [["", { on: false, hovered: false }], ["-hover", { on: false, hovered: true }]];
        if (ON.includes(name)) variants.push(["-on", { on: true, hovered: false }], ["-on-hover", { on: true, hovered: true }]);
        for (const [suffix, s] of variants) {
          out["icon-" + name + suffix] = S.traced(o, () => { Object.assign(st, s); o.userData.repaint(); });
          out["icon-" + name + suffix].state = s;
        }
        Object.assign(st, was);
        o.userData.repaint();
      });
      return out;
    });
    for (const [k, v] of Object.entries(icons)) keep(k, v, { state: v.state });

    step('the volume slider');
    // ---- the volume slider
    const volume = await page.evaluate(() => {
      const S = window.__shots;
      const m = S.byName("vr-volume");
      const out = {};
      for (const [name, level, hovered] of [["volume-full", 1, false], ["volume-40-hover", 0.4, true], ["volume-zero", 0, false]]) {
        out[name] = S.traced(m, () => m.userData.paint(level, hovered));
        out[name].args = { level, hovered };
      }
      m.userData.paint(S.L.audio.volume, false);
      return out;
    });
    for (const [k, v] of Object.entries(volume)) keep(k, v, v.args);

    step('the question and the notice');
    // ---- the question and the notice
    const modal = await page.evaluate(() => {
      const S = window.__shots;
      let m = null;
      S.scene.traverse((o) => { if (o.userData.ask) m = o; });
      const out = {};
      for (const [name, title, body] of [
        ["modal-restart", "Restart level?", undefined],
        ["modal-solution", "Watch the solution?", undefined],
        ["modal-catalog", "Open the world catalog?", undefined],
        ["modal-notice", "No solution", "This level has no stored solution."],
      ]) {
        out[name] = S.traced(m, () => m.userData.ask(title, body));
        out[name].args = { title, body: body === undefined ? null : body };
      }
      m.userData.ask("Restart level?");
      return out;
    });
    for (const [k, v] of Object.entries(modal)) keep(k, v, v.args);

    step('the status strip: as the level left it, ');
    // ---- the status strip: as the level left it, and the outcomes (its painter rebound to a
    // strip of our own, since the strip's fields are the page's)
    const playing = await page.evaluate(() => {
      const S = window.__shots;
      const m = S.byName("vr-status");
      return S.traced(m, () => m.userData.paint());
    });
    // the meta is the page's (pack · rank · save): read it off the DOM HUD, which says the same
    const meta = await page.evaluate(() => document.getElementById("level-meta").textContent);
    await page.evaluate((m) => { window.__statusMeta = m; }, meta);
    // the outcomes: the strip's painter rebound to a strip of our own (its fields are the page's)
    const status2 = await page.evaluate(() => {
      const S = window.__shots;
      const m = S.byName("vr-status");
      const src = m.userData.paint.toString();
      const name = S.L.session.level.name.trim();
      const out = {};
      for (const [shot, patch] of [
        ["status-won", { note: "COMPLETE — 1:07 (best)", kind: "won" }],
        ["status-lost", { note: "FAILED", kind: "lost" }],
        ["status-loading", { note: "loading…", kind: "" }],
        ["status-solution", { note: "SOLUTION COMPLETE", kind: "won" }],
        ["status-choose", { name: "choose a level", meta: "", note: "", kind: "" }],
      ]) {
        const vrStatus = Object.assign({ name, meta: window.__statusMeta, note: "", kind: "" }, patch);
        out[shot] = S.traced(m, () => {
          const cx = S.canvasOf(m).getContext("2d");
          // eslint-disable-next-line no-new-func
          new Function("cx", "VR_STATUS_W", "VR_STATUS_H", "vrStatus", "tex", "return (" + src + ");")(cx, 1024, 132, vrStatus, {})();
        });
        out[shot].args = vrStatus;
      }
      m.userData.paint();
      return out;
    });
    const levelName = await page.evaluate(() => window.__lem3d.session.level.name.trim());
    keep("status-playing", playing, { name: levelName, meta, note: "", kind: "" });
    for (const [k, v] of Object.entries(status2)) keep(k, v, v.args);

    step("the level's text, its OK under the beam,");
    // ---- the level's text, its OK under the beam, and a text too long for the window
    const detail = await page.evaluate(() => {
      const S = window.__shots;
      const m = S.byName("vr-detailpanel");
      const flow = (lines) => {
        const paragraphs = [];
        let cur = "";
        for (const raw of lines) {
          const line = String(raw).trim();
          if (!line) { if (cur) { paragraphs.push(cur); cur = ""; } continue; }
          cur = cur ? cur + " " + line : line;
        }
        if (cur) paragraphs.push(cur);
        return paragraphs;
      };
      const pretext = S.L.session.level.pretext || [];
      const lines = flow(pretext);
      const out = {};
      out["leveltext-pretext"] = S.traced(m, () => m.userData.paint(lines));
      // the OK lit: the panel repaints itself with the page's own lines on a hover
      m.parent.visible = true;
      out["leveltext-ok-hover"] = S.traced(m, () => S.L.vr.hooks.onHoverPick({ barTool: "detailok" }));
      S.L.vr.hooks.onHoverPick(null);
      m.parent.visible = false;
      const long = [];
      for (let i = 1; i <= 7; i++) long.push("Paragraph " + i + ": a level text that runs on and on, word after word, so that it wraps over several lines of the window and is cut at the sixteenth line.");
      out["leveltext-long"] = S.traced(m, () => m.userData.paint(long));
      m.userData.paint(lines);
      return { out, pretext, lines, long };
    });
    keep("leveltext-pretext", detail.out["leveltext-pretext"], { raw: detail.pretext, lines: detail.lines, hot: false });
    keep("leveltext-ok-hover", detail.out["leveltext-ok-hover"], { raw: detail.pretext, lines: detail.lines, hot: true });
    keep("leveltext-long", detail.out["leveltext-long"], { lines: detail.long, hot: false });

    step('the settings');
    // ---- the settings
    const settings = await page.evaluate(() => {
      const S = window.__shots;
      const m = S.byName("vr-setpanel");
      const st = S.L.state;
      const rows = {
        emboss: st.emboss, doors: st.doors, smooth: st.smooth, smoothTerrain: st.smoothTerrain,
        colorBlend: st.colorBlend, skillBar: st.skillBar, flatSkills: st.flatSkills, environment: st.environment,
      };
      const out = {};
      out["settings"] = S.traced(m, () => m.userData.paint());
      out["settings-hover-2"] = S.traced(m, () => S.L.vr.hooks.onHoverPick({ barTool: "setpanel", row: 2 }));
      out["settings-hover-8"] = S.traced(m, () => S.L.vr.hooks.onHoverPick({ barTool: "setpanel", row: 8 }));
      S.L.vr.hooks.onHoverPick(null);
      return { out, rows };
    });
    keep("settings", settings.out["settings"], { rows: settings.rows, hover: -1 });
    keep("settings-hover-2", settings.out["settings-hover-2"], { rows: settings.rows, hover: 2 });
    keep("settings-hover-8", settings.out["settings-hover-8"], { rows: settings.rows, hover: 8 });

    step('the tooltip strip');
    // ---- the tooltip strip
    const tips = await page.evaluate(() => {
      const S = window.__shots;
      const m = S.L.vrTip.mesh;
      const out = {};
      for (const [name, text] of [["tip-lock", "lock the bar to your head"], ["tip-pause", "pause"],
        ["tip-move", "hold the trigger here and move your hand to carry the bar"], ["tip-worlds", "world library: choose a level"]]) {
        out[name] = S.traced(m, () => m.userData.paint(text));
        out[name].text = text;
        out[name].scale = m.scale.toArray().map(S.num);
      }
      return out;
    });
    for (const [k, v] of Object.entries(tips)) keep(k, v, { text: v.text, scale: v.scale });

    step('the layout the page gives the meshes, in');
    // ---- the layout the page gives the meshes, in a headset (isPresenting faked for the call),
    // the REPLAY plate made on the way, and a tooltip placed over a rested button
    const layout = await page.evaluate(() => {
      const S = window.__shots;
      const L = S.L;
      const xr = L.renderer.xr;
      const out = {};
      xr.isPresenting = true;
      let badge = null;
      try {
        L.setReplayBadge(true);
        L.layoutGuiPanel();
        const gui = L.session.gui;
        out.gui = { canvasW: gui.canvas.width, canvasH: gui.canvas.height, mesh: S.pose(gui.mesh) };
        out.level = { width: L.session.level.width, height: L.session.level.height };
        out.buttons = {};
        S.scene.traverse((o) => { if (o.name.startsWith("vr-") && o.isMesh) out.buttons[o.name.slice(3)] = S.pose(o); });
        let modal = null;
        S.scene.traverse((o) => { if (o.userData.ask) modal = o; });
        out.buttons.modalpanel = S.pose(modal);
        // the tooltip over pause, the beam having rested on it for longer than the wait
        L.vr.hooks.onHoverPick({ barTool: "pause" });
        const now = performance.now;
        performance.now = () => now.call(performance) + 1600;
        L.vrTip.update();
        performance.now = now;
        out.tip = { text: L.vrTip.text(), mesh: S.pose(L.vrTip.mesh), canvasW: S.canvasOf(L.vrTip.mesh).width };
        const pause = S.byName("vr-pause");
        out.tip.anchorWorld = pause.matrixWorld.toArray().map(S.num6);
        out.tip.parentWorld = pause.parent.matrixWorld.toArray().map(S.num6);
        L.vr.hooks.onHoverPick(null);
        // the hovered pause, grown and stepped forward
        L.vr.hooks.onHoverPick({ barTool: "pause" });
        L.layoutGuiPanel();
        out.pauseHovered = S.pose(pause);
        L.vr.hooks.onHoverPick({ barTool: "mute" });
        L.layoutGuiPanel();
        out.muteHovered = S.pose(S.byName("vr-mute"));
        out.volumeLingering = S.pose(S.byName("vr-volume"));
        L.vr.hooks.onHoverPick(null);
        badge = S.byName("vr-replaybadge");
      } finally {
        xr.isPresenting = false;
      }
      const shot = badge ? { png: S.canvasOf(badge).toDataURL("image/png"), w: S.canvasOf(badge).width, h: S.canvasOf(badge).height, trace: [] } : null;
      L.setReplayBadge(false);
      L.layoutGuiPanel();
      // where the bar goes by default, from a diorama placed for a head at a known pose
      const head = { pos: new THREE.Vector3(0.3, 1.6, 0.2), quat: new THREE.Quaternion().setFromEuler(new THREE.Euler(-0.2, 0.5, 0, "YXZ")) };
      xr.isPresenting = true;
      try {
        L.placeDioramaForXR(head);
      } finally { xr.isPresenting = false; }
      const d = L.dioramaRoot;
      out.diorama = { pos: d.position.toArray().map(S.num6), rotY: S.num6(d.rotation.y), scale: S.num6(d.scale.x) };
      const placed = L.barDefaultPlacement();
      out.barDefault = { pos: placed.pos.toArray().map(S.num6), quat: placed.quat.toArray().map(S.num6), head: { pos: head.pos.toArray(), quat: head.quat.toArray() } };
      return { out, shot };
    });
    fixture.layout = layout.out;
    if (layout.shot) keep("replay-badge", layout.shot);

    step('the world catalog');
    // ---- the world catalog
    const settle = async () => {
      // the tiles ask for their miniatures as they are painted: wait for them, then paint again
      for (let i = 0; i < 100; i++) {
        const pending = await page.evaluate(() => window.__lem3d.vrCatalog.items().filter((it) => it.thumbReq && !it.thumb).length);
        if (!pending) break;
        await new Promise((r) => setTimeout(r, 200));
      }
    };
    const catalogShot = async (name, extra) => {
      step(name);
      await settle();
      const got = await page.evaluate(() => {
        const S = window.__shots;
        const c = S.L.vrCatalog;
        const shot = S.traced(c.panel, () => c.panel.userData.paint());
        const items = c.items().map((it) => {
          const o = {};
          for (const k of ["kind", "levelId", "playable", "label", "name", "set", "best", "solution", "favorite", "current", "engine", "count", "done"]) if (it[k] !== undefined) o[k] = it[k];
          if (it.node) o.path = it.node.path;
          if (it.thumb) o.thumb = it.levelId;
          return o;
        });
        const thumbs = {};
        for (const it of c.items()) if (it.thumb) thumbs[it.levelId] = it.thumb.toDataURL("image/png");
        // what the panel said: the note and the band, from the calls that drew them
        const texts = shot.trace.filter((t) => t.startsWith("fillText(")).map((t) => JSON.parse("[" + t.slice(9, -1) + "]")[0]);
        const chain = [];
        for (let n = S.L.library.currentNode(); n; n = n.parent) chain.unshift(n.name);
        return { shot, items, thumbs, note: texts[1], chain: chain.join(" › "), cells: c.cells().map((cl) => [cl.i, cl.x, cl.y, cl.w, cl.h]) };
      });
      Object.assign(fixture.thumbs, got.thumbs);
      const n = got.items.length - 1;
      const heading = extra && extra.filter
        ? (extra.filter === "recent" ? "recently played" : "favorites") + " · " + n + (n === 1 ? " level" : " levels")
        : got.chain;
      fixture.catalog[name] = Object.assign({ items: got.items, cells: got.cells, heading }, extra || {});
      keep(name, got.shot, { catalog: name });
      return got;
    };
    const waitCatalog = async (pred) => {
      for (let i = 0; i < 150; i++) {
        if (await page.evaluate(pred)) return;
        await new Promise((r) => setTimeout(r, 100));
      }
      throw new Error("catalog did not load");
    };
    const select = (p) => page.evaluate((p) => window.__lem3d.vr.hooks.onSelectPick(p), p);
    const hover = (p) => page.evaluate((p) => window.__lem3d.vr.hooks.onHoverPick(p), p);

    // landing: the directory of the level being played, opened on it
    await page.evaluate(() => window.__lem3d.vrCatalog.load(true));
    await catalogShot("catalog-levels", { scroll: "reveal", hover: -1 });
    const firstTile = await page.evaluate(() => window.__lem3d.vrCatalog.items().findIndex((it) => it.kind === "level" && !it.current));
    await hover({ barTool: "worldpanel", tile: firstTile + 4 });
    await catalogShot("catalog-levels-hover", { scroll: "reveal", hover: firstTile + 4 });
    await hover({ barTool: "worldpanel", scrollBar: true, tile: -1 });
    await catalogShot("catalog-levels-bar-hover", { scroll: "reveal", hover: -2 });
    await hover(null);
    await select({ barTool: "worldpanel", scrollBar: true, scrollAt: 1e6 });
    await catalogShot("catalog-levels-end", { scroll: "end", hover: -1 });
    await select({ barTool: "worldpanel", scrollBar: true, scrollAt: -1e6 });
    await catalogShot("catalog-levels-top", { scroll: "top", hover: -1 });
    // the root of the tree and one pack, a row per directory
    await page.evaluate(() => { window.__lem3d.library.navigate(""); return window.__lem3d.vrCatalog.load(false); });
    await catalogShot("catalog-root", { scroll: "top", hover: -1 });
    await hover({ barTool: "worldpanel", tile: 1 });
    await catalogShot("catalog-root-hover", { scroll: "top", hover: 1 });
    await hover(null);
    await page.evaluate(() => { window.__lem3d.library.navigate("NeoLemmix_Introduction_Pack"); return window.__lem3d.vrCatalog.load(false); });
    await catalogShot("catalog-pack", { scroll: "top", hover: -1 });
    await hover({ barTool: "worldpanel", tile: 0 });
    await catalogShot("catalog-pack-back-hover", { scroll: "top", hover: 0 });
    await hover(null);
    // the two lists, from any pack
    await select({ barTool: "catrecent" });
    await waitCatalog(() => { const it = window.__lem3d.vrCatalog.items(); return it.length > 1 && it[0].kind === "back" && it[1].kind === "level" && it[1].label.includes(" › ") && it.length === 6; });
    await catalogShot("catalog-recent", { scroll: "top", hover: -1, filter: "recent" });
    await select({ barTool: "catfav" });
    await waitCatalog(() => { const it = window.__lem3d.vrCatalog.items(); return it.length === 4 && it[1].label.includes(" › ") && it.every((x) => x.kind !== "dir"); });
    await catalogShot("catalog-favorites", { scroll: "top", hover: -1, filter: "favorites" });
    // pressed again: the directory; then no favorites at all
    await select({ barTool: "catfav" });
    await waitCatalog(() => window.__lem3d.vrCatalog.items().some((it) => it.kind === "dir"));
    await page.evaluate(() => localStorage.setItem("lem3d-favorites", "[]"));
    await select({ barTool: "catfav" });
    await waitCatalog(() => window.__lem3d.vrCatalog.items().length === 1);
    await catalogShot("catalog-favorites-empty", { scroll: "top", hover: -1, filter: "favorites" });

    // the notes as drawn (the port is given them; its own Load builds them in tests)
    for (const [name, c] of Object.entries(fixture.catalog)) {
      const texts = fixture.shots[name].trace.filter((t) => t.startsWith("fillText(")).map((t) => JSON.parse("[" + t.slice(9, -1) + "]")[0]);
      c.note = texts[1] === "stick up / down to scroll" ? "" : texts[1];
    }

    step('write');
    // ---- write
    for (const [name, s] of Object.entries(shots)) {
      fs.writeFileSync(path.join(OUT, name + ".png"), Buffer.from(s.png.split(",")[1], "base64"));
    }
    const json = JSON.stringify(fixture, null, 1);
    fs.writeFileSync(path.join(OUT, "fixture.json"), json);
    fs.mkdirSync(path.dirname(APP_FIXTURE), { recursive: true });
    fs.writeFileSync(APP_FIXTURE, json);
    console.log("webshots: " + Object.keys(shots).length + " pictures in " + path.relative(ROOT, OUT) + ", fixture " + (json.length >> 10) + " KiB");
  } finally {
    await browser.close();
  }
}

// headless Chrome on swiftshader now and then loses the page mid-run: try again
(async () => {
  for (let attempt = 1; attempt <= 3; attempt++) {
    try { await main(); return; } catch (e) { console.error("webshots attempt " + attempt + " failed: " + e.message); }
  }
  process.exit(1);
})();
