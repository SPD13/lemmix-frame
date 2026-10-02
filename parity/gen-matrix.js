#!/usr/bin/env node
// Builds parity/matrix.json: one row per thing the web app does that the Frame app must do too.
// Rows are scraped from the web sources where a table exists (hotkey actions, lem3d-* settings)
// and listed by hand below for the rest (screens, render features, engine modules, formats).
// Re-running keeps each existing row's status and notes, adds new rows as "todo", and marks
// rows whose source vanished as "gone" - so a bump of the web/ pin shows what changed.
// Usage: node parity/gen-matrix.js
"use strict";
const fs = require("fs");
const path = require("path");
const { execSync } = require("child_process");

const ROOT = path.resolve(__dirname, "..");
const WEB = path.join(ROOT, "web");
const OUT = path.join(__dirname, "matrix.json");
const sha = execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
const read = (f) => fs.readFileSync(path.join(WEB, f), "utf8");
const lineOf = (f, needle) => {
  const i = read(f).split("\n").findIndex((l) => l.includes(needle));
  return i < 0 ? f : `${f}:${i + 1}`;
};

// What the user excluded (plan, "Play features only"; classic engine out of scope)
const EXCLUDED_ACTIONS = new Set(["piece_editor", "cycle_class"]);
const EXCLUDED_KEYS = new Set([
  "lem3d-files", // IndexedDB name: the native app has a filesystem
  "lem3d-gal-open", // galleries page (dev tool)
  "lem3d-sw-reload", "lem3d-version", // service worker / page version: Steam updates the app
  "lem3d-worlds-v4", // classic scan cache
  "lem3d-favorite", // prefix of lem3d-favorites
  "lem3d-flat", // desktop 2D view preference (the in-VR flat view is a row of its own)
]);

const rows = [];
const add = (r) => rows.push({ status: "todo", ...r });

// 1. hotkey actions (hotkeys.js ACTIONS): each is a function the controls can trigger
const hk = read("3d/js/hotkeys.js");
for (const m of hk.matchAll(/\{ id: "([a-z_]+)", label: "([^"]+)"([^}]*)\}/g)) {
  const [, id, label, rest] = m;
  if (EXCLUDED_ACTIONS.has(id)) continue;
  const tag = (rest.match(/tag: "([a-z]+)"/) || [])[1];
  add({ id: `action.${id}`, area: "input", title: label, source: lineOf("3d/js/hotkeys.js", `id: "${id}"`),
        handler: lineOf("3d/js/app.js", `case "${id}"`), oracle: "unit", phase: tag === "vr" ? 4 : 5 });
}

// 2. persisted settings (every lem3d-* key in the 3D layer)
const keys = new Set();
for (const f of fs.readdirSync(path.join(WEB, "3d/js"))) {
  for (const m of read(`3d/js/${f}`).matchAll(/"(lem3d-[a-z0-9-]+)"/g)) keys.add(m[1]);
}
for (const k of [...keys].sort()) {
  if (EXCLUDED_KEYS.has(k)) continue;
  const f = fs.readdirSync(path.join(WEB, "3d/js")).find((x) => read(`3d/js/${x}`).includes(`"${k}"`));
  add({ id: `setting.${k}`, area: "persistence", title: k, source: lineOf(`3d/js/${f}`, `"${k}"`), oracle: "schema", phase: 7 });
}

// 3. by hand: engine, formats, rendering, VR, panels, audio, setup
const H = (id, area, title, source, oracle, phase) => add({ id, area, title, source, oracle, phase });
H("core.parser", "engine", "NeoLemmix text format parser", "lemmix/js/parser.js", "parse-dump", 2);
H("core.styles", "engine", "Styles: pieces, gadgets (.nxmo), terrain (.nxmt), themes (.nxtm), alias/scheme, variants, nine-slice", "lemmix/js/styles.js", "parse-dump", 2);
H("core.pixels", "engine", "Bitmap ops and blending (Math.round semantics)", "lemmix/js/pixels.js", "sim-hash", 2);
H("core.level", "engine", "Level build: terrain, physics map, gadgets, seeded animation frames", "lemmix/js/level.js", "parse-dump+sim-hash", 2);
H("core.lemgame", "engine", "LemGame: all 21 skills, gadgets, zombies, neutrals, talismans, time limit", "lemmix/js/lemgame.js", "sim-hash", 2);
H("core.sprites", "engine", "Sprite sets, scheme recolouring, masks", "lemmix/js/sprites.js", "sim-hash", 2);
H("core.shadows", "engine", "Skill shadow simulation", "lemmix/js/shadows.js", "shadow-dump", 2);
H("core.rewind", "engine", "Rewind states every 170 frames, thinning, silent re-sim", "lemmix/js/rewind.js", "rewind-selfcheck", 2);
H("core.replay", "engine", ".nxrp parse/serialize, cut, insert mode", "lemmix/js/replay.js", "sim-hash", 2);
H("core.game", "engine", "Game adapter: commands, timer, state, results (game.js + classic CommandManager/Command*/EventHandler)", "lemmix/js/game.js", "command-fuzz", 2);
H("core.panel", "engine", "Skill panel 416x40: cells, split cells, info strip, hold-repeat", "lemmix/js/panel.js", "panel-pixels", 3);
H("core.picking", "engine", "Lemming under cursor, priority, direction/walker filters", "lemmix/js/lemgame.js", "pick-dump", 2);
H("core.indexes", "assets", "levels/styles/music index builders (sort order)", "tools/levels-index.js", "index-dump", 5);
H("render.terrain", "render", "Terrain chunks by depth class, greedy meshing, live re-mesh on dig/build", "3d/js/terrain.js", "geometry", 3);
H("render.depth", "render", "Depth classes from style profiles (3d/profiles)", "3d/js/depth.js", "geometry", 3);
H("render.effects", "render", "Effect switches: emboss, smooth, edge smoothing, colour blend, doors, skills-bar relief, flat skills, environment", "3d/js/app.js", "geometry+screenshot", 3);
H("render.portals", "render", "3D hatches with doors, exit funnels, water/lava slices", "3d/js/portals.js", "geometry", 3);
H("render.bridge", "render", "Lemmings and objects as voxel sprites, interpolation, particles", "3d/js/bridge.js", "geometry", 3);
H("render.decals", "render", "ONLY_ON_TERRAIN gadgets and one-way arrows on terrain", "3d/js/decals.js", "geometry", 3);
H("render.environment", "render", "Room around the board: rings, fog, skyline, floor, dome, background tiling, falling lemmings", "3d/js/environment.js", "screenshot", 3);
H("render.envgen", "render", "Seeded collage pictures from the style's pieces", "3d/js/envgen.js", "pixels", 3);
H("render.replay_markers", "render", "Replay markers: rings, skill icons, countdown, pulse, solid when played", "3d/js/replay-markers.js", "screenshot", 3);
H("render.clear_physics", "render", "Clear physics overlay", lineOf("3d/js/app.js", "makeClearPhysicsOverlay"), "pixels", 3);
H("render.cursor", "render", "NeoLemmix cursor at the end of the beam (cross, square, arrows)", "3d/js/cursor.js", "unit", 3);
H("render.minimap", "render", "Minimap in the panel, trigger-drag to centre", "3d/js/minimap.js", "pixels", 3);
H("render.flat_view", "render", "2D flat view inside VR and the animated transition", lineOf("3d/js/app.js", "toggleFlat"), "screenshot", 3);
H("vr.session", "vr", "OpenXR session, local-floor, 2.5 mm per pixel, focus-loss pause, recenter", "3d/js/vr.js", "scripted-pose", 4);
H("vr.interactions", "vr", "Trigger click, trigger-drag board, grip grab, two-grip scale, beam hand swap, sticks", "3d/js/vr.js", "scripted-pose", 4);
H("vr.toolbar", "vr", "VR toolbar: lock/move/park/settings, pause, worlds, prev/restart/solution/next, mute + volume", lineOf("3d/js/app.js", "lem3d-bar"), "view-model", 4);
H("vr.status_strip", "vr", "Status strip above the board, detail icon", "3d/js/app.js", "view-model", 4);
H("vr.modal", "vr", "Confirm (yes/no) and notice (OK) windows, holding the clock", lineOf("3d/js/app.js", "vrModal"), "view-model", 4);
H("vr.tooltip", "vr", "Skill tooltip strip after 1500 ms", "3d/js/app.js", "view-model", 4);
H("vr.replay_badge", "vr", "REPLAY badge above the status strip", "3d/js/app.js", "view-model", 4);
H("vr.keyboard", "vr", "Bluetooth keyboard hotkeys with the same table", "3d/js/hotkeys.js", "unit", 5);
H("ui.library", "ui", "World library: union of desktop library and VR catalog (breadcrumb, packs, ranks, tiles, search, recent, favorites, order, rescan)", "3d/js/library.js", "view-model", 5);
H("ui.vr_keyboard", "ui", "In-VR text keyboard for library search", "3d/js/library.js", "unit", 5);
H("ui.setup", "ui", "Setup: download engine/styles/Lemmings Plus, import zip, list/delete packs, storage, versions, config export/import, credits", "3d/js/setup.js", "view-model", 5);
H("ui.solutions", "ui", "Solutions list: search, pack filter, show filter, play", "3d/js/solutions.js", "view-model", 5);
H("ui.controls", "ui", "Controls dialog: VR and keyboard tabs, presets, unassigned, find key, import/export", "3d/js/hotkeys.js", "view-model", 5);
H("ui.key_hints", "ui", "Key hints", "3d/js/app.js", "view-model", 5);
H("ui.settings", "ui", "Settings window: 8 effect switches, recentre", lineOf("3d/js/app.js", "vrSettingRows"), "view-model", 5);
H("ui.level_text", "ui", "Level text window: PRETEXT, POSTTEXT, talismans, result", "3d/js/app.js", "view-model", 5);
H("ui.replay_files", "ui", "Load/save .nxrp through a device folder", "3d/js/app.js", "unit", 5);
H("audio.sfx", "audio", "SFX cues, spatial at the emitter", "3d/js/audio.js", "cue-sequence", 6);
H("audio.music", "audio", "Music: MUSIC fallback list, .nxmi rotation, pack folder, tracker via libopenmpt", "3d/js/audio.js", "render-checksum", 6);
H("flow.first_run", "flow", "Nothing installed: open Setup first", "3d/js/vfs.js", "unit", 7);
H("flow.level_end", "flow", "Win: auto-advance after 3 s; loss: restart; cancelled by rewind", "3d/js/app.js", "unit", 7);
H("flow.progress", "flow", "Progress: best time, clears, most saved, talismans; recorded except when watching a solution", "3d/js/config-store.js", "schema", 7);
H("flow.cli", "flow", "Command line: level id, .nxrp, solution, speed (the URL params)", "3d/js/app.js", "unit", 7);

// merge with the previous matrix
let prev = {};
if (fs.existsSync(OUT)) for (const r of JSON.parse(fs.readFileSync(OUT, "utf8")).rows) prev[r.id] = r;
const seen = new Set();
const merged = rows.map((r) => {
  seen.add(r.id);
  const p = prev[r.id];
  return p ? { ...r, status: p.status, notes: p.notes, webSha: p.webSha } : { ...r, webSha: sha };
});
for (const [id, p] of Object.entries(prev)) if (!seen.has(id)) merged.push({ ...p, status: "gone" });
merged.sort((a, b) => a.phase - b.phase || a.id.localeCompare(b.id));
fs.writeFileSync(OUT, JSON.stringify({ webSha: sha, generated: "node parity/gen-matrix.js", rows: merged }, null, 1) + "\n");
const by = {};
for (const r of merged) by[r.status] = (by[r.status] || 0) + 1;
console.log(`${merged.length} rows (web ${sha}):`, by);
