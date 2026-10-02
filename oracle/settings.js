#!/usr/bin/env node
"use strict";
/**
 * The settings oracle: the player's data layer as the web keeps it - the controls table
 * (hotkeys.js), the three configuration files (config-store.js build/apply, setup.js
 * export/import with the progress merge), the preferences as app.js and audio.js read them,
 * and the library's data side (library.js LevelTree, LevelProgress, RecentLevels,
 * FavoriteLevels, Solutions, the fuzzy search through WorldLibrary._renderSearch), plus the
 * talismans as app.js records them - all run from web/ over a Map-backed localStorage
 * (lib/storage-env.js).
 *
 * Scenarios are lists of steps [op, args]; after each the step's result (JSON.stringify'd)
 * and a hash of the whole store (its entries sorted by key) are recorded, and each distinct
 * store once in full, so the C# replay (Lemmix.Core.Tests Store/Input/Library) can say what
 * differs. Plus tables of the JS primitives the port reimplements (number to string, JSON,
 * parseFloat, ToNumber, case mapping, encodeURIComponent, fuzzyScore).
 *   node oracle/settings.js        -> oracle/out/settings.json.gz
 */
const fs = require("fs");
const path = require("path");
const { ASSETS, WEB, OUT } = require("./lib/env");
const { StateHash } = require("./lib/hash");
const E = require("./lib/storage-env");
const LevelsIndex = require(path.join(WEB, "tools", "levels-index.js"));

const { Hotkeys, ConfigStore, Library, GameAudio, files, downloads } = E;
const { LevelTree, LevelProgress, RecentLevels, FavoriteLevels, Solutions, WorldLibrary, fuzzyScore } = Library;

const hashText = (t) => { const h = new StateHash(); h.str(t); return h.hex(); };

// ---------------------------------------------------------------- the trees and solutions
const NATIVE_INDEX = (() => { const o = LevelsIndex.buildIndex(LevelsIndex.nodeIO(ASSETS), []); o.generated = ""; return JSON.stringify(o); })();
const WEB_INDEX = fs.readFileSync(path.join(ASSETS, "levels", "index.json"), "utf8"); // the classic packs too
const SOLUTIONS = (() => {
  for (const dir of [ASSETS, WEB]) {
    const p = path.join(dir, "solutions", "index.json");
    if (fs.existsSync(p)) return { from: dir === ASSETS ? "assets" : "web", text: fs.readFileSync(p, "utf8") };
  }
  return { from: "none", text: '{"levels":{}}' };
})();
files.set("solutions/index.json", SOLUTIONS.text);

// ---------------------------------------------------------------- the step interpreter
const snapshots = {};
function storeHash() {
  const m = localStorage.m;
  const pairs = Array.from(m.keys()).sort().map((k) => [k, m.get(k)]);
  const text = JSON.stringify(pairs);
  const h = hashText(text);
  snapshots[h] = text;
  return h;
}

let hk = null;      // the HotkeyManager the steps act on
let setup = null;   // window.__setup of the last setup.js evaluation
let lib = null;     // a WorldLibrary over the fake DOM
const msg = (id) => { const el = document.getElementById(id); return { text: el.textContent, cls: el.className }; };
const binding = (b) => (b ? { action: b.action, mod: b.mod } : null);
const libView = () => {
  // what _renderSearch / _renderPicked drew: the header's text, the tiles' level ids, the status
  const grid = lib.dom.grid;
  const header = grid.children.find((c) => c.className === "lib-world");
  return { title: header ? header.children[0].textContent : null, ids: lib.__shown, status: lib.dom.status.textContent };
};
const short = (v) => { // the search's id lists: the first ten and a hash of all
  return { title: v.title, status: v.status, count: v.ids.length, top: v.ids.slice(0, 10), all: hashText(v.ids.join("\n")) };
};

// app.js: the render settings read at start (setting(), the colour blend, the environment),
// taken from its source rather than retyped
const APP = fs.readFileSync(path.join(WEB, "3d", "js", "app.js"), "utf8");
const SETTING_SRC = APP.match(/  const setting = \(name, key, dflt\) => \{[\s\S]*?\n  \};\n/)[0];
const SETTING_CALLS = [...APP.matchAll(/^\s*(\w+): (setting\("[^"]*", "lem3d-[^"]*", (?:true|false)\)),/mg)].map((m) => m[1] + ": " + m[2]);
const IIFE = (name) => name + ": " + APP.match(new RegExp("\\n    " + name + ": (\\(\\(\\) => \\{[\\s\\S]*?\\n    \\}\\)\\(\\)),"))[1];
const readSettings = new Function("params", "localStorage", SETTING_SRC + "\nreturn {" + SETTING_CALLS.concat(IIFE("colorBlend"), IIFE("environment")).join(",\n") + "};");
if (SETTING_CALLS.length !== 9) throw new Error("app.js settings: " + SETTING_CALLS.length + " found");

// app.js onGameEnd (3495-3497), the talismans earned on a win: the three lines, as they are
// there (inside a closure the oracle cannot reach)
function talismanWin(levelId, ids) {
  try {
    const all = JSON.parse(localStorage.getItem("lem3d-talismans") || "{}");
    all[levelId] = Array.from(new Set((all[levelId] || []).concat(ids)));
    localStorage.setItem("lem3d-talismans", JSON.stringify(all));
  } catch (e) {}
}

async function step(op, args) {
  const a = args;
  switch (op) {
    // the store
    case "reset": localStorage.clear(); for (const [k, v] of Object.entries(a[0] || {})) localStorage.setItem(k, v); return;
    case "set": localStorage.setItem(a[0], a[1]); return;
    case "remove": localStorage.removeItem(a[0]); return;
    case "get": return localStorage.getItem(a[0]);
    // hotkeys.js
    case "hk.new": hk = new Hotkeys.HotkeyManager(); return;
    case "hk.load": hk.load(); return;
    case "hk.save": hk.save(); return;
    case "hk.applyPreset": return a.length > 1 ? hk.applyPreset(a[0], a[1]) : hk.applyPreset(a[0]);
    case "hk.applyVrPreset": return a.length ? hk.applyVrPreset(a[0]) : hk.applyVrPreset();
    case "hk.fillDefaults": return a.length ? hk.fillDefaults(a[0]) : hk.fillDefaults();
    case "hk.hasHalf": return hk.hasHalf(a[0]);
    case "hk.set": return a.length > 2 ? hk.set(a[0], a[1], a[2]) : hk.set(a[0], a[1]);
    case "hk.get": return binding(hk.get(a[0]));
    case "hk.codesFor": return a.length > 1 ? hk.codesFor(a[0], a[1]) : hk.codesFor(a[0]);
    case "hk.keyNameFor": return a.length > 1 ? hk.keyNameFor(a[0], a[1]) : hk.keyNameFor(a[0]);
    case "hk.export": return hk.exportJSON();
    case "hk.import": try { return hk.importJSON(a[0]); } catch (e) { return { error: e.message }; }
    case "hk.table": return Array.from(hk.table, ([code, b]) => [code, binding(b)]);
    // config-store.js
    case "cfg.build": return ConfigStore.build(a[0]);
    case "cfg.apply": try { ConfigStore.apply(a[0], JSON.parse(a[1])); return "ok"; } catch (e) { return { error: e.message }; }
    // setup.js
    case "setup.load": setup = E.loadSetup(); return;
    case "setup.exportControls": case "setup.exportPrefs": case "setup.exportProgress": {
      downloads.length = 0;
      setup[op.slice(6)]();
      return downloads[0] || null;
    }
    case "setup.importControls": setup.importControls(a[0], a[1]); return msg("msg-controls");
    case "setup.importPrefs": setup.importPrefs(a[0], a[1]); return msg("msg-prefs");
    case "setup.importProgress": setup.importProgress(a[0], a[1]); return msg("msg-progress");
    // app.js / audio.js preferences
    case "pref.read": return readSettings(new URLSearchParams(a[0] || ""), localStorage);
    case "audio.new": { const au = new GameAudio(); return { enabled: au.enabled, volume: au.volume }; }
    case "audio.setVolume": { const au = new GameAudio(); au.setVolume(a[0]); return au.volume; }
    case "audio.setEnabled": { const au = new GameAudio(); au.stopAll = () => {}; au.setEnabled(a[0]); return au.enabled; }
    // library.js: the tree
    case "tree.load": files.set("levels/index.json", a[0] === "web" ? WEB_INDEX : NATIVE_INDEX); await LevelTree.load("", true); return LevelTree.byId.size;
    case "tree.next": return LevelTree.next(a[0], a[1]);
    case "tree.describe": {
      const d = LevelTree.describe(a[0]);
      return d && { engine: d.engine, label: d.label, packName: d.packName, title: d.title, node: d.node.path, pack: d.pack ? d.pack.path : null };
    }
    case "tree.levelsOf": { const n = LevelTree.nodeAt(a[0]); const ids = LevelTree.levelsOf(n).map((l) => l.id); return { count: ids.length, first: ids[0] || null, all: hashText(ids.join("\n")) }; }
    case "tree.firstLevelId": return LevelTree.firstLevelId();
    case "tree.classicId": return LevelTree.classicId(a[0], a[1], a[2]);
    case "tree.nodeAt": { const n = LevelTree.nodeAt(a[0]); return n && { path: n.path, kind: n.kind, name: n.name, parent: n.parent ? n.parent.path : null, pack: n.pack ? n.pack.path : null }; }
    // library.js: progress, recent, favorites, solutions
    case "prog.all": return LevelProgress.all();
    case "prog.record": return LevelProgress.record(a[0], a[1], a[2]);
    case "prog.best": return LevelProgress.best(a[0]);
    case "prog.saved": return LevelProgress.saved(a[0]);
    case "prog.clearedUnder": return LevelProgress.clearedUnder(LevelTree.nodeAt(a[0]));
    case "prog.migrate": LevelProgress.migrate(); return;
    case "prog.format": return LevelProgress.format(a[0]);
    case "recent.push": RecentLevels.push(a[0]); return;
    case "recent.list": return RecentLevels.list();
    case "fav.toggle": return FavoriteLevels.toggle(a[0]);
    case "fav.has": return FavoriteLevels.has(a[0]);
    case "fav.list": return FavoriteLevels.list();
    case "sol.info": return Solutions.info(a[0]);
    case "sol.has": return Solutions.has(a[0]);
    case "sol.url": try { return Solutions.url(a[0], a[1]); } catch (e) { return { error: e.name }; }
    case "talisman.win": talismanWin(a[0], a[1]); return;
    // library.js: the WorldLibrary's data (order, path, recent on entering, the views)
    case "lib.new": {
      E.byId.clear(); // fresh elements: the listeners of an earlier library do not fire again
      lib = new WorldLibrary({}, "", () => {}, null);
      lib.__shown = [];
      lib._buildTile = (node, level) => { lib.__shown.push(level.id); return new E.FakeEl("div"); };
      return { order: lib.order, path: lib.path };
    }
    case "lib.toggleOrder": lib.dom.order.click(); return lib.order;
    case "lib.navigate": lib.navigate(a[0]); return lib.path;
    case "lib.setCurrent": lib.setCurrent(a[0]); return lib.currentLevelId;
    case "lib.levelName": return lib.levelName(a[0]);
    case "lib.search": case "lib.recentView": case "lib.favoritesView": {
      lib.isOpen = true;
      lib.dom.grid.innerHTML = "";
      lib.dom.status.textContent = "";
      lib.__shown = [];
      if (op === "lib.search") { lib.query = a[0]; await lib._renderSearch(a[0]); return short(libView()); }
      if (op === "lib.recentView") { lib.recent = true; await lib._renderRecent(); } else { lib.favorites = true; await lib._renderFavorites(); }
      return libView();
    }
  }
  throw new Error("unknown op " + op);
}

const scenarios = [];
async function scenario(name, steps) {
  const out = [];
  for (const [op, ...raw] of steps) {
    const args = JSON.parse(JSON.stringify(raw)); // what the record says is what ran (undefined becomes null)
    let r;
    try { r = await step(op, args); } catch (e) {
      if (!(e instanceof TypeError)) throw e;
      r = { threw: "TypeError" }; // the web code itself fails here (an index off the list, say): so must the port
    }
    const text = r === undefined ? null : JSON.stringify(r);
    out.push([op, args, text, storeHash()]);
  }
  scenarios.push({ name, steps: out });
}

// ---------------------------------------------------------------- a seeded random source
let seed = 20261002;
const rnd = () => { seed = (Math.imul(seed, 1103515245) + 12345) >>> 0; return seed / 4294967296; };
const pick = (list) => list[Math.floor(rnd() * list.length)];

// ---------------------------------------------------------------- the scenarios
const K = Hotkeys;
const CODES = K.KEYS.map((k) => k.code).concat(K.VR_KEYS.map((k) => k.code));
const ACTION_IDS = K.ACTIONS.map((a) => a.id);
const MODS = [undefined, null, 0, 1, -1, -2, 170, -17, 2.7, -2147483648, 4294967297, 1e21, "digger", "walker", "", "ßx", "ǆa", "\ud83d\ude00x",
  "\udc00", "12", " 0x1F ", "1e3", "abc", "0b101", "-0x10", " \n 42 \t", "Infinity", "-Infinity", "-0", ".5", "5.", "1_000", true, false,
  [3], ["a", "b"], {}, [], [null], [[1, 2], 3], { a: 1 }];

const ctrl = (keys, extra) => JSON.stringify(Object.assign({ format: "lemmings-3d-controls", version: 1, keys }, extra || {}), null, 2);

async function hotkeyScenarios() {
  await scenario("hotkeys-fresh", [
    ["reset", {}], ["hk.new"], ["hk.table"], ["hk.export"], ["hk.hasHalf", true], ["hk.hasHalf", false],
    ["hk.applyPreset", "functional"], ["hk.export"], ["hk.applyPreset", "minimal"], ["hk.table"],
    ["hk.applyPreset", "nope"], ["hk.applyPreset", "traditional", false], ["hk.table"], ["hk.save"],
    ["hk.set", "VrFreeA", "pause"], ["hk.applyVrPreset", false], ["hk.table"], ["hk.applyVrPreset"],
    ["hk.set", "KeyA", "skill", "digger"], ["hk.set", "KeyA", null], ["hk.set", "KeyA", "skill"], ["hk.get", "KeyA"],
    ["hk.set", "F1", "skip"], ["hk.set", "KeyG", "special_skip", 1], ["hk.set", "KeyZ", "bogus_action"], ["hk.set", "KeyY", ""],
    ["hk.set", "Digit1", "skill", { x: 1 }], ["hk.set", "Digit2", "skip", "12"], ["hk.set", "Space", "clear_physics", null],
    ["hk.get", "KeyZ"], ["hk.get", "Nope"], ["hk.codesFor", "skill"], ["hk.codesFor", "skill", "digger"], ["hk.codesFor", "skip", 170],
    ["hk.codesFor", "pause"], ["hk.codesFor", "skip", "12"], ["hk.keyNameFor", "pause"], ["hk.keyNameFor", "skip", 170],
    ["hk.keyNameFor", "recenter_vr"], ["hk.keyNameFor", "vr_pan"], ["hk.keyNameFor", "nothing"], ["hk.keyNameFor", "skill", "builder"],
    ["hk.export"], ["hk.new"], ["hk.table"], ["hk.fillDefaults"], ["hk.export"],
    ["hk.set", "Pause", "pause"], ["hk.set", "MouseRight", "nuke"], ["hk.set", "VrPointB", "nuke"], ["hk.keyNameFor", "nuke"],
  ]);

  const loads = [
    "", "garbage{", "null", "5", '"text"', "[]", '{"keys":5}', '{"keys":null}', '{"keys":"abc"}',
    '{"keys":{"KeyA":{"action":"pause"}}}',
    '{"vr":true,"keys":{"VrPointA":{"action":"pause"}}}',
    '{"vr":true,"keys":{"KeyA":{"action":"pause"},"VrPointA":{"action":"nuke"}}}',
    '{"vr":1,"keys":{}}', '{"vr":0,"keys":{}}',
    '{"vr":true,"keys":{"KeyA":{"action":"skill","mod":null},"KeyB":{"action":"skip"},"KeyC":{"action":"skill","mod":{"o":[1,{"p":null}]}},"Foo":{"action":"pause"},"KeyD":{"action":"nope"},"KeyE":5,"KeyF":null,"KeyG":["pause"],"VrFreeStick":{"action":"pause"}}}',
    '{"vr":true,"keys":[{"action":"pause"},{"action":"nuke","mod":7}]}',
    '{"vr":true,"keys":{"KeyA":{"action":"pause"},"7":{"action":"nuke"},"KeyB":{"action":"restart"},"0":{"action":"cheat"},"4294967295":{"action":"quit"},"4294967294":{"action":"quit"},"01":{"action":"quit"}}}',
    '{"vr":true,"keys":{"KeyA":{"action":"pause"},"KeyA":{"action":"nuke"},"KeyB":{"action":"quit"}}}',
    '{"vr":true,"keys":{"KeyA":{"action":"pause","mod":1.5e300},"KeyB":{"action":"skip","mod":-0.0000001},"KeyC":{"action":"skip","mod":123456789012345680000}}}',
    '{"vr":true,"keys":{"KeyA":{"action":"skill","mod":"\\u00e9\\ud800\\u2028\\u0007\\"\\\\/"}}}',
    '  {"vr":true,"keys":{"KeyA":{"action":"pause"}}}  ',
  ];
  const steps = [];
  for (const raw of loads) steps.push(["reset", { "lem3d-hotkeys": raw }], ["hk.new"], ["hk.table"], ["hk.save"], ["hk.export"]);
  await scenario("hotkeys-load", steps);

  const files = [
    "not json", "", "[]", "5", "null", '"x"', '{"keys":[]}', '{"keys":"x"}', '{"keys":{}}', '{"format":"other","keys":{}}',
    '{"format":5,"keys":{}}', '{"format":["a","b"],"keys":{}}', '{"format":{},"keys":{}}', '{"format":"","keys":{}}', '{"format":0,"keys":{}}',
    ctrl({ KeyA: { action: "pause" } }),
    ctrl({ VrPointA: { action: "pause" }, VrFreeStick: { action: "vr_zoom" } }),
    ctrl({ VrPointStick: { action: "pause" }, VrPointA: { action: "vr_pan" }, VrPointB: { action: "piece_editor" }, VrFreeA: { action: "cycle_class" }, VrFreeB: { action: "vr_dolly_in" }, KeyA: { action: "vr_pan" }, KeyB: { action: "vr_dolly_out" }, KeyC: { action: "piece_editor" } }),
    ctrl({ KeyA: { action: "skill", mod: "ßeta" }, KeyB: { action: "skip", mod: "0x10" }, KeyC: { action: "skip", mod: [5] }, KeyD: { action: "skill", mod: { a: 1 } }, KeyE: { action: "clear_physics", mod: "" }, KeyF: { action: "special_skip", mod: -1 }, KeyG: 5, KeyH: null, KeyI: ["skill"], KeyJ: { action: "skill", mod: null }, Foo: { action: "pause" }, KeyK: { action: 5 }, KeyL: {} }),
    '{"keys":{"KeyA":{"action":"pause"},"KeyA":{"action":"nuke"},"5":{"action":"pause"},"Digit5":{"action":"quit"}}}',
    '{"format":"lemmings-3d-controls","version":7,"keys":{"VrPointA":{"action":"pause"},"KeyQ":{"action":"quit"}},"extra":[1,2]}',
  ];
  const isteps = [["reset", {}], ["hk.new"], ["hk.import", "x"]];
  // its own export, back in
  isteps.push(["hk.applyPreset", "functional"], ["hk.set", "VrFreeB", "nuke"], ["hk.set", "KeyA", "skip", -85]);
  isteps.push(["hk.export"]);
  await scenario("hotkeys-import", isteps.concat(files.map((t) => ["hk.import", t]).flatMap((s) => [s, ["hk.table"]])));
  // the export of a table carrying odd mods, imported again
  const own = [];
  own.push(["reset", { "lem3d-hotkeys": loads[14] }], ["hk.new"], ["hk.export"]);
  await scenario("hotkeys-roundtrip", own);
  const exported = JSON.parse(scenarios[scenarios.length - 1].steps[2][2]);
  await scenario("hotkeys-roundtrip-back", [["reset", {}], ["hk.new"], ["hk.import", exported], ["hk.table"], ["hk.export"]]);

  // random sequences of everything a player can do to the table
  const rsteps = [["reset", {}], ["hk.new"]];
  for (let i = 0; i < 400; i++) {
    const r = rnd();
    if (r < 0.45) rsteps.push(["hk.set", pick(CODES.concat(["Bogus", "Digit5"])), pick(ACTION_IDS.concat([null, null, "nope"])), pick(MODS)]);
    else if (r < 0.55) rsteps.push(["hk.applyPreset", pick(["traditional", "functional", "minimal", "nope"])]);
    else if (r < 0.6) rsteps.push(["hk.applyVrPreset"]);
    else if (r < 0.7) rsteps.push(["hk.codesFor", pick(ACTION_IDS), pick(MODS)]);
    else if (r < 0.78) rsteps.push(["hk.keyNameFor", pick(ACTION_IDS)]);
    else if (r < 0.84) rsteps.push(["hk.new"]);
    else if (r < 0.9) rsteps.push(["hk.export"]);
    else if (r < 0.95) rsteps.push(["hk.fillDefaults"]);
    else rsteps.push(["hk.hasHalf", rnd() < 0.5]);
  }
  rsteps.push(["hk.table"]);
  await scenario("hotkeys-random", rsteps);
}

// the pure functions of hotkeys.js
function hotkeyFunctions() {
  const out = { describe: [], tagOf: [], allowedOn: [], normalizeCode: [], keyName: [] };
  const bindings = [null, { action: "nope" }];
  for (const id of ACTION_IDS) for (const mod of MODS) bindings.push({ action: id, mod });
  for (const b of bindings) {
    out.describe.push([b, JSON.stringify(K.describe(b))]);
    out.tagOf.push([b, K.tagOf(b)]);
  }
  for (const code of CODES.concat(["Bogus", ""])) for (const id of ACTION_IDS.concat(["nope"])) out.allowedOn.push([code, id, K.allowedOn(code, id)]);
  for (const c of ["", null, "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight", "OSLeft", "OSRight", "KeyA", "Shift", "shiftleft", "Numpad1"]) out.normalizeCode.push([c, K.normalizeCode(c)]);
  for (const code of CODES.concat(["Bogus", ""])) out.keyName.push([code, K.keyName(code)]);
  // the tables themselves
  out.tables = JSON.stringify({ ACTIONS: K.ACTIONS, KEYS: K.KEYS, VR_KEYS: K.VR_KEYS, VR_PRESET: K.VR_PRESET, PRESETS: K.PRESETS, SKILLS: K.SKILLS, DOS_SKILLS: Array.from(K.DOS_SKILLS), SPECIAL_SKIPS: K.SPECIAL_SKIPS, DEFAULT_PRESET: K.DEFAULT_PRESET, EXPORT_FORMAT: K.EXPORT_FORMAT, EXPORT_FILE: K.EXPORT_FILE });
  return out;
}

const prog = (cleared, talismans, extra) => JSON.stringify(Object.assign({ format: "lemmings-3d-progress", version: 1, cleared, talismans }, extra || {}));

async function configScenarios() {
  const prefsAll = {};
  ConfigStore.PREFS_KEYS.forEach((k, i) => { prefsAll[k] = "v" + i; });
  await scenario("config-build-apply", [
    ["reset", {}], ["cfg.build", "controls"], ["cfg.build", "prefs"], ["cfg.build", "progress"], ["cfg.build", "nope"],
    ["hk.new"], ["cfg.build", "controls"],
    ["set", "lem3d-emboss", "off"], ["set", "lem3d-volume", "0.5"], ["set", "lem3d-unrelated", "x"], ["set", "lem3d-favorites", '["a","b"]'],
    ["cfg.build", "prefs"],
    ["set", "lem3d-cleared", '{"a":{"best":3,"clears":1}}'], ["cfg.build", "progress"],
    ["set", "lem3d-cleared", "{}"], ["set", "lem3d-talismans", '{"a":["t"]}'], ["cfg.build", "progress"],
    ["set", "lem3d-talismans", "[]"], ["cfg.build", "progress"], ["set", "lem3d-talismans", "[1]"], ["cfg.build", "progress"],
    ["set", "lem3d-cleared", "5"], ["cfg.build", "progress"], ["set", "lem3d-cleared", "garbage"], ["cfg.build", "progress"],
    ["set", "lem3d-hotkeys", '{"keys":[1]}'], ["cfg.build", "controls"], ["set", "lem3d-hotkeys", '{"version":"x","keys":{"A":1}}'], ["cfg.build", "controls"],
    ["set", "lem3d-hotkeys", '{"keys":null}'], ["cfg.build", "controls"], ["set", "lem3d-hotkeys", "0"], ["cfg.build", "controls"],
    // apply
    ["cfg.apply", "controls", '{"keys":{"KeyA":{"action":"pause"}}}'], ["cfg.apply", "controls", '{"version":3,"keys":{"VrPointA":{"action":"pause"}}}'],
    ["hk.new"], ["hk.table"],
    ["cfg.apply", "controls", '{"format":"lemmings-3d-controls","keys":[{"action":"pause"}]}'], ["hk.new"], ["hk.table"],
    ["cfg.apply", "controls", '{"format":"lemmings-3d-preferences","keys":{}}'], ["cfg.apply", "controls", '{"keys":5}'],
    ["cfg.apply", "controls", "5"], ["cfg.apply", "controls", "null"], ["cfg.apply", "controls", '"x"'], ["cfg.apply", "controls", "[]"],
    ["cfg.apply", "controls", '{"format":0,"keys":{"Vr":1}}'], ["cfg.apply", "controls", '{"format":"","keys":{"Vrx":1,"5":2}}'],
    ["cfg.apply", "prefs", JSON.stringify({ values: prefsAll })], ["cfg.build", "prefs"],
    ["cfg.apply", "prefs", '{"format":"lemmings-3d-preferences","values":{"lem3d-emboss":5,"lem3d-smooth":"on","lem3d-nope":"x","lem3d-flat":null}}'],
    ["cfg.apply", "prefs", '{"values":[]}'], ["cfg.apply", "prefs", '{"values":"x"}'], ["cfg.apply", "prefs", '{"format":"x","values":{}}'],
    ["cfg.build", "prefs"],
    ["cfg.apply", "progress", '{"cleared":{"a":{"best":1}},"talismans":{"a":["x"]}}'], ["cfg.build", "progress"],
    ["cfg.apply", "progress", '{"cleared":[1,2]}'], ["cfg.build", "progress"],
    ["cfg.apply", "progress", '{"cleared":{},"talismans":"x"}'], ["cfg.build", "progress"],
    ["cfg.apply", "progress", '{"cleared":{"b":2},"talismans":[["x"]]}'], ["cfg.build", "progress"],
    ["cfg.apply", "progress", '{"cleared":null}'], ["cfg.apply", "progress", '{"format":"lemmings-3d-progress"}'],
    ["cfg.apply", "progress", '{"cleared":{"2":1,"b":2,"1":3,"a":{"z":1,"0":2}}}'], ["cfg.build", "progress"],
  ]);

  // setup.js: the three files down and up
  const seeded = {
    "lem3d-cleared": JSON.stringify({ A: { best: 30, clears: 2, saved: 5 }, B: { best: null, clears: 0 }, C: { clears: 1 }, D: { best: "12", clears: "3" }, E: 5, L: { best: 8, clears: 1, saved: 3 }, "1/0/3": { best: 1, clears: 1 } }),
    "lem3d-talismans": JSON.stringify({ A: ["t2", "t3"], C: "zy", D: [1], Q: ["q"] }),
  };
  const file = prog({
    A: { best: 20, clears: 1, saved: 7 }, B: { best: 15 }, C: { best: null, clears: 4 }, D: { best: 10, clears: 2 }, E: { best: 5 },
    F: { best: 7, clears: 1, saved: 0 }, G: null, H: "x", I: [1], J: { best: "9" }, K: { saved: "4", clears: true },
    L: { best: 9, clears: 0, saved: 2 }, M: { best: -1, clears: -5, saved: -3 }, N: { best: 1.25, clears: 1.5, saved: "x" },
    O: { clears: [], saved: [7] }, 10: { best: 3 }, 2: { best: 4 }, __x: { best: 0, clears: 0, saved: 0 },
  }, { A: ["t1", "t2"], B: "notarray", C: ["x", "x", "y"], D: [1, "1", 1, null, null, {}, [], 0, -0], Z: [], 3: ["n"] });
  await scenario("setup-files", [
    ["reset", {}], ["setup.load"], ["setup.exportPrefs"], ["setup.exportProgress"], ["setup.exportControls"],
    ["setup.importPrefs", "not json", "p.json"], ["setup.importPrefs", "{}", "p.json"], ["setup.importPrefs", '{"format":"lemmings-3d-preferences"}', "p.json"],
    ["setup.importPrefs", '{"format":"lemmings-3d-preferences","values":{"lem3d-emboss":"off","lem3d-volume":"0.3","lem3d-x":"y","lem3d-flat":1}}', "p.json"],
    ["setup.importPrefs", "null", "n.json"], ["setup.importPrefs", "[]", "a.json"],
    ["setup.exportPrefs"],
    ["setup.importControls", "x", "c.json"], ["setup.importControls", ctrl({ KeyA: { action: "pause" }, Foo: { action: "x" } }), "c.json"],
    ["setup.importControls", ctrl({ KeyA: { action: "pause" }, VrPointA: { action: "nuke" } }), "c.json"],
    ["setup.importControls", ctrl({}), "c.json"], ["setup.importControls", ctrl({ KeyA: { action: "pause" } }), "one.json"],
    ["setup.exportControls"],
    ["setup.importProgress", "x", "g.json"], ["setup.importProgress", "{}", "g.json"], ["setup.importProgress", '{"cleared":{}}', "g.json"],
    ["setup.importProgress", "null", "g.json"],
    ["setup.importProgress", prog({}, undefined), "empty.json"], ["setup.exportProgress"],
    ["reset", seeded], ["setup.importProgress", file, "merge.json"], ["setup.exportProgress"], ["setup.importProgress", file, "again.json"], ["setup.exportProgress"],
    ["reset", { "lem3d-cleared": "[]", "lem3d-talismans": '{"A":5}' }], ["setup.importProgress", prog({ A: { best: 1 }, 1: { best: 2 } }, { A: ["x"] }), "t.json"],
    ["setup.exportProgress"],
    ["reset", { "lem3d-cleared": "5" }], ["setup.importProgress", prog({ A: { best: 1 } }, {}), "t.json"],
    ["reset", { "lem3d-cleared": '"str"' }], ["setup.importProgress", prog({ A: { best: 1 } }, {}), "t.json"],
    ["reset", { "lem3d-cleared": "true" }], ["setup.importProgress", prog({ A: { best: 1 } }, {}), "t.json"],
    ["reset", { "lem3d-cleared": "0" }], ["setup.importProgress", prog({ A: { best: 1 } }, {}), "t.json"],
    ["reset", { "lem3d-cleared": "{}", "lem3d-talismans": "null" }], ["setup.importProgress", prog({}, { A: ["x"] }), "t.json"],
    ["reset", { "lem3d-talismans": '{"A":{},"B":"s"}' }], ["setup.importProgress", prog({}, { B: ["x", "s"] }), "t.json"], ["setup.importProgress", prog({}, { A: ["x"] }), "t.json"],
    ["reset", { "lem3d-talismans": '"str"' }], ["setup.importProgress", prog({}, { 0: ["x"], A: ["y"] }), "t.json"],
    ["reset", {}], ["setup.importProgress", prog([{ best: 3 }, null, { best: 4 }], "abc"), "arr.json"], ["setup.exportProgress"],
  ]);

  // a random merge fuzz: random stored records, random files
  const vals = [null, 0, 1, 2, 5, 10, 30, 1.5, -1, "3", "", "x", true, false, [], [4], {}];
  const ids = ["a", "b", "c", "d", "1", "22", "e"];
  const rec = () => { const o = {}; for (const f of ["best", "clears", "saved"]) if (rnd() < 0.7) o[f] = pick(vals); return o; };
  const fsteps = [];
  for (let i = 0; i < 60; i++) {
    const mine = {}, theirs = {}, tm = {}, tt = {};
    for (const id of ids) {
      if (rnd() < 0.6) mine[id] = rnd() < 0.9 ? rec() : pick(vals);
      if (rnd() < 0.6) theirs[id] = rnd() < 0.9 ? rec() : pick(vals);
      if (rnd() < 0.4) tm[id] = Array.from({ length: Math.floor(rnd() * 4) }, () => pick(["t1", "t2", "t3", 1, null]));
      if (rnd() < 0.4) tt[id] = rnd() < 0.85 ? Array.from({ length: Math.floor(rnd() * 4) }, () => pick(["t1", "t2", "t4", 1, null])) : pick(vals);
    }
    fsteps.push(["reset", { "lem3d-cleared": JSON.stringify(mine), "lem3d-talismans": JSON.stringify(tm) }], ["setup.importProgress", prog(theirs, tt), "r.json"]);
  }
  fsteps.unshift(["setup.load"]);
  await scenario("setup-merge-random", fsteps);
}

async function preferenceScenarios() {
  const steps = [["reset", {}], ["pref.read", ""]];
  const stored = ["on", "off", "", "ON", "1", "true", "yes", "soft", "smooth", "0", "full", "none", "ambient", "2", "false", "OFF", "Full", "null", "undefined", "x"];
  for (const v of stored) {
    const all = {};
    for (const k of ["lem3d-emboss", "lem3d-smooth", "lem3d-smooth-terrain", "lem3d-color-blend", "lem3d-environment", "lem3d-doors", "lem3d-skillbar", "lem3d-flatskills", "lem3d-flat", "lem3d-shadows", "lem3d-music"]) all[k] = v;
    steps.push(["reset", all], ["pref.read", ""]);
  }
  steps.push(["reset", { "lem3d-emboss": "off", "lem3d-color-blend": "smooth" }]);
  for (const q of ["emboss", "emboss=1", "emboss=ON", "emboss=0", "emboss=no", "emboss=yes&flat=true&music=", "colorblend=0", "colorblend=SOFT", "colorblend=",
    "environment=OFF", "environment=Ambient", "environment=", "environment=2&doors=off", "smoothterrain=on&skillbar=TRUE&flatskills=x&shadows=1"]) steps.push(["pref.read", q]);
  // audio.js: the sound switch and the volume
  steps.push(["reset", {}], ["audio.new"]);
  for (const v of ["0.5", "0", "1", "2", "-1", "x", "", " 0.25 ", ".75abc", "1e-1", "Infinity", "-Infinity", "NaN", "0x10", "1e400", "-0", "+.5", "\u00a00.3", "5e-324"]) steps.push(["set", "lem3d-volume", v], ["audio.new"]);
  for (const s of ["on", "off", "", "OFF", "x"]) steps.push(["set", "lem3d-sound", s], ["audio.new"]);
  steps.push(["reset", {}]);
  for (const v of [0.5, 0, 1, 2, -1, 0.1 + 0.2, 1 / 3, 1e-7, 5e-324]) steps.push(["audio.setVolume", v], ["get", "lem3d-volume"]);
  steps.push(["audio.setEnabled", false], ["audio.setEnabled", true]);
  await scenario("preferences", steps);
}

async function libraryScenarios() {
  const steps = [["reset", {}], ["tree.load", "native"], ["tree.firstLevelId"]];
  const tree = JSON.parse(NATIVE_INDEX);
  const all = [], nodes = [];
  const walk = (n) => { nodes.push(n.path || ""); for (const l of n.levels || []) all.push(l.id); for (const c of n.children || []) walk(c); };
  tree.path = ""; walk(tree);
  for (let i = 0; i < all.length; i += 37) for (const d of [-1, 1, 0, 5, -200, 1000]) steps.push(["tree.next", all[i], d]);
  for (const id of [all[0], all[all.length - 1], "nope", ""]) steps.push(["tree.next", id, 1], ["tree.next", id, -1]);
  for (let i = 0; i < all.length; i += 53) steps.push(["tree.describe", all[i]]);
  steps.push(["tree.describe", "nope"]);
  for (const p of nodes) steps.push(["tree.levelsOf", p], ["tree.nodeAt", p], ["prog.clearedUnder", p]);
  steps.push(["tree.nodeAt", "nope"], ["tree.nodeAt", null], ["tree.classicId", 1, 0, 0]);
  // progress: wins recorded, read back
  const sample = all.filter((_, i) => i % 97 === 0);
  for (const [i, id] of sample.entries()) {
    steps.push(["prog.record", id, 60 + i, i % 3 ? 10 + i : undefined], ["prog.best", id], ["prog.saved", id]);
    steps.push(["prog.record", id, 50 + (i % 20), "7"], ["prog.record", id, 70, 99], ["prog.best", id], ["prog.saved", id]);
  }
  steps.push(["prog.best", "nope"], ["prog.saved", "nope"], ["prog.all"]);
  for (const p of nodes.slice(0, 12)) steps.push(["prog.clearedUnder", p]);
  for (const s of [0, 5, 59, 60, 61, 125, 3600, 3599.5, 7.25, -1, -61, 1e21]) steps.push(["prog.format", s]);
  // odd stored records: what record and best make of them
  steps.push(["set", "lem3d-cleared", JSON.stringify({ x: { clears: 2 }, y: { best: "5", clears: "3", saved: "2" }, z: 5, w: { best: null, clears: null }, v: { best: 10, clears: 1, saved: true } })]);
  for (const id of ["x", "y", "z", "w", "v"]) steps.push(["prog.record", id, 8, 3], ["prog.best", id], ["prog.saved", id]);
  steps.push(["prog.all"], ["set", "lem3d-cleared", "[]"], ["prog.record", "a", 5, 1], ["prog.all"], ["set", "lem3d-cleared", "x"], ["prog.record", "a", 5, 1], ["prog.all"]);
  steps.push(["set", "lem3d-cleared", "null"], ["prog.record", "b", 0, 0], ["prog.best", "b"], ["prog.record", "b", -0, -1]);
  // recent: pushed, trimmed to 50, latest first
  for (let i = 0; i < 70; i++) steps.push(["recent.push", all[(i * 13) % 40]]);
  steps.push(["recent.list"], ["recent.push", all[0]], ["recent.push", 5], ["recent.list"]);
  steps.push(["set", "lem3d-recent", '{"a":1}'], ["recent.list"], ["recent.push", "z"], ["set", "lem3d-recent", "garbage"], ["recent.push", "z"], ["recent.list"]);
  // favorites
  for (const id of [all[3], all[5], all[3], all[7], "gone/level.nxlv", all[5], all[9]]) steps.push(["fav.toggle", id], ["fav.has", id]);
  steps.push(["fav.list"], ["set", "lem3d-favorites", '"x"'], ["fav.has", "x"], ["fav.toggle", "y"], ["fav.list"]);
  steps.push(["set", "lem3d-favorites", '["a",1,null,"a"]'], ["fav.toggle", "a"], ["fav.list"], ["fav.toggle", "a"], ["fav.list"], ["fav.toggle", all[2]]);
  // talismans as app.js records them
  steps.push(["talisman.win", all[1], ["t1", "t2"]], ["talisman.win", all[1], ["t2", "t3"]], ["talisman.win", all[2], []], ["get", "lem3d-talismans"]);
  steps.push(["set", "lem3d-talismans", '{"a":"xy"}'], ["talisman.win", "a", ["y", "z"]], ["set", "lem3d-talismans", '{"a":5}'], ["talisman.win", "a", ["q"]]);
  steps.push(["set", "lem3d-talismans", "null"], ["talisman.win", "a", ["q"]], ["set", "lem3d-talismans", ""], ["talisman.win", "a", ["q"]], ["set", "lem3d-talismans", "[]"], ["talisman.win", "0", ["q"]]);
  steps.push(["set", "lem3d-talismans", "x"], ["talisman.win", "a", ["q"]], ["set", "lem3d-talismans", '{"a":[1,"1",null]}'], ["talisman.win", "a", [1, null, "2"]]);
  // solutions
  const sol = JSON.parse(SOLUTIONS.text).levels || {};
  const solIds = Object.keys(sol).filter((_, i) => i % 11 === 0).concat(["nope"]);
  for (const id of solIds) steps.push(["sol.info", id], ["sol.has", id], ["sol.url", "", id], ["sol.url", "../", id]);
  // the WorldLibrary's own keys
  steps.push(["reset", {}], ["lib.new"], ["lib.toggleOrder"], ["lib.toggleOrder"], ["lib.toggleOrder"], ["lib.navigate", nodes[3]], ["lib.navigate", ""], ["lib.navigate", null], ["lib.navigate", nodes[5]], ["lib.new"]);
  steps.push(["set", "lem3d-lib-order", "World"], ["lib.new"], ["set", "lem3d-lib-path", ""], ["lib.new"]);
  steps.push(["lib.setCurrent", all[4]], ["lib.setCurrent", all[4]], ["lib.setCurrent", all[6]], ["lib.setCurrent", null], ["lib.setCurrent", all[4]], ["recent.list"]);
  for (const id of all.slice(0, 5)) steps.push(["lib.levelName", id]);
  steps.push(["lib.levelName", "nope"]);
  // the recent and favorites views
  steps.push(["reset", {}], ["lib.new"], ["lib.recentView"], ["lib.favoritesView"]);
  steps.push(["recent.push", all[10]], ["recent.push", "gone"], ["recent.push", all[11]], ["fav.toggle", all[12]], ["fav.toggle", "gone"], ["lib.recentView"], ["lib.favoritesView"]);
  steps.push(["set", "lem3d-recent", JSON.stringify(["gone"])], ["lib.recentView"]);
  await scenario("library", steps);

  // the search
  const queries = ["dig", "just dig", "lemming", "mild 1", "Wimpy 3", "tower", "xmas", "orig_dirt", "bubble", "zzz", "a", "the", "", "  ",
    "LEMMINGS PLUS", "plus i medi", "holiday", "basic training", "skills 4", "ohno", "sega", "crystal", "net", "jdin", "lmmngs",
    "bridge breaking", "pop yor", "!!!", "?", "up for a walk", "objects functions", "&", "ß", "é", "1", "12", "30", "gentle 32", "zany",
    "redux", "namida", "lemmingsplus all", "keep close", "severe", "malice", "x", "qq", "walk!", "fire", "snow", "shadow l2", "İ", "\u212a",
    "ΣΑΣ", "e\u0301", "😀", "a\u00a0b", "dig\tdirt", "_", "x2", "o'", "lem-ming", "tRiCkY", "z 0"];
  const ssteps = [["reset", {}], ["lib.new"]];
  for (const q of queries) ssteps.push(["lib.search", q]);
  await scenario("search", ssteps);

  // the web's own tree (with the classic packs): the classic ids, the records' migration, and the
  // tree loaded over the previous one (LevelTree.load does not clear its maps)
  const wsteps = [["reset", {}], ["tree.load", "web"], ["tree.firstLevelId"], ["tree.classicId", 1, 0, 0], ["tree.classicId", 2, 4, 19], ["tree.classicId", 1, 9, 0], ["tree.classicId", 3, 0, 0], ["tree.classicId", 1, 0, 99]];
  wsteps.push(["tree.next", "lemmings/0/0", -1], ["tree.next", "lemmings/3/29", 1], ["tree.next", "lemmings_ohNo/4/19", 1], ["tree.describe", "lemmings/1/4"], ["tree.levelsOf", ""], ["tree.levelsOf", "lemmings"]);
  wsteps.push(["tree.next", all[0], 1], ["tree.describe", all[0]]); // the native tree's ids are still known
  wsteps.push(["set", "lem3d-cleared", JSON.stringify({ "1/0/3": { best: 5, clears: 1 }, "2/1/0": { best: 6, clears: 2 }, "3/0/0": { best: 7, clears: 1 }, "1/9/9": { best: 1, clears: 1 }, "lemmings/0/4": { best: 9, clears: 9 }, "1/0/4": { best: 1, clears: 1 }, "1/00/1": { best: 2, clears: 1 }, "x/0/0": { best: 3, clears: 1 } })]);
  wsteps.push(["prog.migrate"], ["prog.all"], ["prog.migrate"], ["prog.clearedUnder", "lemmings"], ["prog.clearedUnder", "lemmings/0"]);
  await scenario("web-tree", wsteps);
}

// ---------------------------------------------------------------- the JS primitives
function primitives() {
  const out = {};
  // String(number)
  const nums = [0, -0, 1, -1, 0.1, 0.2, 0.1 + 0.2, 1 / 3, 2 / 3, 1e21, 1e20, 123456789012345680000, 1e-6, 1e-7, 1.5e-7, 123e-20, 5e-324, 1.7976931348623157e308,
    2 ** 53, 2 ** 53 + 2, 4294967295, 4294967296, -2147483648, 0.000001, 0.0000012345, 100, 1e100, 25, 0.5, 3599.5, 7.25, 299792458, 1e15, 1e16, 1e17, 9.5e20, 9.999999999999999e20];
  for (let i = 0; i < 3000; i++) {
    const r = rnd();
    if (r < 0.4) { const b = new DataView(new ArrayBuffer(8)); b.setUint32(0, Math.floor(rnd() * 4294967296)); b.setUint32(4, Math.floor(rnd() * 4294967296)); const v = b.getFloat64(0); if (Number.isFinite(v)) nums.push(v); }
    else if (r < 0.7) nums.push(Math.round(rnd() * 1e6) / Math.pow(10, Math.floor(rnd() * 12)));
    else if (r < 0.85) nums.push((rnd() - 0.5) * Math.pow(10, Math.floor(rnd() * 60) - 30));
    else nums.push(Math.floor(rnd() * 2 ** 53) * (rnd() < 0.5 ? -1 : 1));
  }
  out.numbers = nums.map((v) => { const b = new DataView(new ArrayBuffer(8)); b.setFloat64(0, v); return [b.getUint32(0), b.getUint32(4), String(v)]; });
  // JSON.parse then JSON.stringify (plain and with a 2-space gap); "error" when it does not parse
  const texts = ["{}", "[]", "null", "true", "false", "0", "-0", "1e400", "-1e400", "1E5", "0.1e-2", "01", "1.", ".5", "+1", "-", "1e", "\"\\u0041\\ud800\"", "\"\\/\"",
    "\"a\tb\"", "\"\\x41\"", "{\"a\":1,}", "[1,]", "{'a':1}", "{\"a\":1 \"b\":2}", " \t\r\n{\"a\" : [ 1 , 2 ] }\n", "\ufeff{}", "{\"\":1}", "{\"a\":{\"b\":{\"c\":[[[]]]}}}",
    "{\"b\":1,\"a\":2,\"10\":3,\"2\":4,\"-1\":5,\"01\":6,\"1.5\":7,\"4294967294\":8,\"4294967295\":9}", "{\"a\":1,\"a\":2,\"b\":3,\"a\":4}",
    "[1,\"2\",null,true,{},[]]", "\"\\u2028\\u2029\\u007f\\u0000\\u001f\\b\\f\\n\\r\\t\"", "\"\u00e9\u4e2d\ud83d\ude00\"", "123456789012345678901234567890", "0.30000000000000004",
    "[1e21,1e-7,1.5e300]", "\"unterminated", "nul", "[\"a\" , \"b\"]", "{\"__proto__\":1}", "1 2", "", " ", "\u00a0{}", "{\"a\":\"\u0001\"}", "-01", "[-]", "2e-400", "1e-7"];
  out.json = texts.map((t) => { try { const v = JSON.parse(t); return [t, JSON.stringify(v), JSON.stringify(v, null, 2) ?? null]; } catch (e) { return [t, "error", null]; } });
  // parseFloat, Number (ToNumber of a string), ToInt32
  const strs = ["", " ", "0", "-0", "1", "1.5", " 1.5 ", "1.5abc", "abc", ".5", "5.", "+.5", "-.5e2", "1e", "1e+", "1e-1", "1E3", "Infinity", "-Infinity", "+Infinity", "infinity",
    "Infinityx", "NaN", "0x10", "0X1f", "-0x10", "0b101", "0o17", "0b2", "0x", "1_000", "\u00a01", "\u20281\u2029", "\ufeff1", "1\u0085", "١", "12345678901234567890", "1e400",
    "5e-324", "2e-324", "0.1e1", "00012", "-00.5", "1.2.3", "--1", "+-1", ".", "e5", "\t\n\v\f\r 7 \t", "0x1g", "4294967297", "-2147483649", "3000000000"];
  out.parseFloat = strs.map((s) => [s, JSON.stringify(parseFloat(s)) ?? null, String(parseFloat(s))]);
  out.toNumber = strs.map((s) => [s, String(Number(s)), Number(s) | 0]);
  // case mapping, per code point (only where it changes); and whole strings (final sigma)
  const lower = [], upper = [];
  for (let cp = 0; cp <= 0x10ffff; cp++) {
    if (cp >= 0xd800 && cp <= 0xdfff) continue;
    const s = String.fromCodePoint(cp);
    const l = s.toLowerCase(), u = s.toUpperCase();
    if (l !== s) lower.push([cp, l]);
    if (u !== s) upper.push([cp, u]);
  }
  out.lower = lower;
  out.upper = upper;
  out.lowerStrings = ["ΣΑΣ", "ΑΣ ΑΣ", "Σ", "AΣ", "ΑΣ.", "ΑΣ'Β", "Α'Σ", "ΑΣ\u0301", "Α\u0301Σ", "1Σ", "ΣΣ", "ΑΣΣ", "ΑΣ1", "İ", "Iİ", "ǅ", "ΑΣα"].map((s) => [s, s.toLowerCase()]);
  // encodeURIComponent (Solutions.url)
  out.encodeURIComponent = ["a b", "!'()*~-_.", "é", "😀", "/?#[]@$&+,;=", "%", "\u0000\u007f", "\ud800", "a\udc00b"].map((s) => { try { return [s, encodeURIComponent(s)]; } catch (e) { return [s, null]; } });
  // fuzzyScore on random pairs
  const chars = "abcdefghij ABC-_.'1é Σ😀\t";
  const word = (n) => Array.from({ length: n }, () => chars[Math.floor(rnd() * chars.length)]).join("");
  const rchars = (n) => { let s = ""; for (let i = 0; i < n; i++) s += pick(Array.from(chars)); return s; };
  out.fuzzy = [];
  for (let i = 0; i < 600; i++) {
    const q = rnd() < 0.5 ? word(1 + Math.floor(rnd() * 6)) : rchars(1 + Math.floor(rnd() * 6));
    const t = rchars(5 + Math.floor(rnd() * 30));
    out.fuzzy.push([q, t, fuzzyScore(q, t)]);
  }
  return out;
}

async function main() {
  await Solutions.load("");
  await hotkeyScenarios();
  await configScenarios();
  await preferenceScenarios();
  await libraryScenarios();
  const result = {
    webSha: require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim(),
    solutions: SOLUTIONS.from,
    scenarios,
    snapshots,
    hotkeys: hotkeyFunctions(),
    primitives: primitives(),
  };
  const text = JSON.stringify(result);
  // gzipped (OracleData.Load reads the .gz): the stores repeat the controls table a lot
  fs.writeFileSync(path.join(OUT, "settings.json.gz"), require("zlib").gzipSync(text + "\n", { level: 9 }));
  try { fs.unlinkSync(path.join(OUT, "settings.json")); } catch (e) { /* none */ }
  const steps = scenarios.reduce((n, s) => n + s.steps.length, 0);
  console.log(scenarios.length + " scenarios, " + steps + " steps, " + Object.keys(snapshots).length + " stores, " + (text.length / 1024 / 1024).toFixed(2) + " MB");
}

main().catch((e) => { console.error(e); process.exit(1); });
