#!/usr/bin/env node
"use strict";
/**
 * The skill bar oracle (web/3d/js/gui.js GuiPanel): the Lemmix panel as a plane in the scene,
 * with its relief (layout.reliefMasks extruded by bridge.js buildExtrudedSpriteGeometry, the
 * counters strip bevelled), the hovered button's raised copy (texture crop, socket, raised
 * relief, split-cell halves), the minimap plane's placement (minimap.js layout()) and ray hits
 * turned into the panel's mouse events. gui.js, minimap.js and bridge.js run as the page runs
 * them, on three.js (web/3d/lib/three.min.js) and the canvas stand-in of lib/ui-env.js.
 *
 * For every sampled level and each of the four switch combinations (relief "lem3d-skillbar" on
 * or off, "lem3d-flatskills" on or off) a fixed script - placement in a headset and on a monitor,
 * relief depth, ticks, a hover over every cell and both halves of the split cells, switches
 * thrown while hovering, presses with each mouse button, a minimap drag - records after each
 * step: the panel texture, every mesh's visibility and transform, every relief geometry, the
 * hover state and label, the texture crop, and what reached the panel and the page.
 *   node oracle/skillbar.js
 * C# twin: core/Lemmix.Core.Tests/Render/SkillBarTests.cs
 */
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const ui = require("./lib/ui-env");
const { Lemmix, WEB, OUT, clock } = ui;
const { sampleLevels, GameFactory } = require("./lib/ui-game");
const { StateHash } = require("./lib/hash");

// the real three.js for gui.js (ui-env's stand-in only covers minimap.js's needs)
globalThis.THREE = require(path.join(WEB, "3d", "lib", "three.min.js"));
globalThis.ImageData = class ImageData {
  constructor(w, h) { this.width = w; this.height = h; this.data = new Uint8ClampedArray(w * h * 4); }
};
if (typeof globalThis.window === "undefined") globalThis.window = globalThis;
{
  const JS3D = path.join(WEB, "3d", "js");
  const run = (file) => vm.runInThisContext(fs.readFileSync(path.join(JS3D, file), "utf8"), { filename: file });
  run("depth.js");
  const m = /const TERRAIN_DEPTH = (\d+)/.exec(fs.readFileSync(path.join(JS3D, "terrain.js"), "utf8"));
  vm.runInThisContext("const TERRAIN_DEPTH = " + m[1] + ";");
  run("bridge.js");
  run("minimap.js");
  run("gui.js");
}
const GuiPanel = vm.runInThisContext("GuiPanel");

const f64 = new Float64Array(1), u32 = new Uint32Array(f64.buffer);
function F64(h, v) {
  if (v === null || v === undefined) { h.word(0x7fffffff); return; }
  f64[0] = v; h.word(u32[0]); h.word(u32[1]);
}
function hashGeometry(h, geom) {
  if (!geom || !geom.getAttribute("position")) { h.word(0x7fffffff); return; }
  for (const name of ["position", "color", "uv"]) {
    const a = geom.getAttribute(name);
    if (!a) { h.word(0x7fffffff); continue; }
    if (!(a.array instanceof Float32Array)) throw new Error(name + " is not a Float32Array");
    h.bytes(a.array);
  }
  const idx = geom.getIndex();
  if (!idx) { h.word(0x7fffffff); return; }
  h.word(idx.array.BYTES_PER_ELEMENT);
  h.bytes(idx.array);
}
function hashObject(h, o) {
  if (!o) { h.word(0x7fffffff); return; }
  h.any(!!o.visible, "visible");
  for (const v of [o.position.x, o.position.y, o.position.z, o.scale.x, o.scale.y, o.scale.z]) F64(h, v);
}

const W = 416, H = 40;
const uvAt = (px, py) => ({ x: px / W, y: 1 - py / H });

class Rec {
  constructor(gui, game) {
    this.gui = gui; this.game = game; this.steps = []; this.log = [];
    gui.display.onMouseDown.on((p) => this.log.push("d " + p.x + " " + p.y + " " + p.button));
    gui.display.onMouseUp.on((p) => this.log.push("u " + p.x + " " + p.y));
    gui.display.onDoubleClick.on((p) => this.log.push("c " + p.x + " " + p.y));
    gui.onMinimapCenter = (pt) => this.log.push("m " + pt.x + " " + pt.y);
  }
  hash() {
    const gui = this.gui, h = new StateHash();
    // the texture
    h.bytes(gui.canvas.data);
    // the meshes
    hashObject(h, gui.mesh); hashObject(h, gui.hoverTile); hashObject(h, gui.socket); hashObject(h, gui.hoverRelief);
    hashObject(h, gui.textMesh); hashObject(h, gui.minimap ? gui.minimap.mesh : null);
    if (gui.hoverTexture) { F64(h, gui.hoverTexture.repeat.x); F64(h, gui.hoverTexture.repeat.y); F64(h, gui.hoverTexture.offset.x); F64(h, gui.hoverTexture.offset.y); }
    else h.word(0x7fffffff);
    if (gui.tileReliefs) {
      h.word(gui.tileReliefs.length);
      gui.tileReliefs.forEach((m, i) => { h.num(gui.reliefParts[i].index, "part"); h.str(gui.reliefParts[i].half); hashObject(h, m); });
    } else h.word(0x7fffffff);
    // the hover
    h.num(gui.hoverIndex, "hover"); h.str(gui.hoverHalf);
    const tip = gui.hoverTip();
    h.str(tip ? tip.text : null); F64(h, tip ? tip.since : null);
    h.any(gui.reliefOn, "relief"); F64(h, gui.reliefDepth); h.any(gui.flatSkills, "flat"); h.any(gui.minimapDrag, "drag");
    // what reached the panel and the page
    h.word(this.log.length); for (const s of this.log) h.str(s);
    this.log.length = 0;
    const g = this.game, sim = g.sim;
    h.str(sim.selectedSkill); h.num(sim.selectDx, "dx"); h.any(g.nukePrepared, "nuke"); h.any(g.getGameTimer().isRunning(), "run");
    h.num(g.getGameTimer().speedFactor, "speed"); h.num(sim.currentIteration, "it"); h.str(g.commandManager.serialize());
    const transforms = h.hex();
    // the geometries
    const gh = new StateHash();
    if (gui.tileReliefs) for (const m of gui.tileReliefs) hashGeometry(gh, m.geometry);
    else gh.word(0x7fffffff);
    hashGeometry(gh, gui.hoverRelief ? gui.hoverRelief.geometry : null);
    hashGeometry(gh, gui.textMesh ? gui.textMesh.geometry : null);
    return transforms + ":" + gh.hex();
  }
  record(label) { this.steps.push([label, this.hash()]); }
}

function labels(gui) {
  const h = new StateHash();
  for (let i = -1; i <= 20; i++) for (const half of [null, "upper", "lower"]) h.str(gui.buttonLabel(i < 0 ? null : i, half));
  F64(h, gui.raisedTileBottomOffset());
  return h.hex();
}

function script(gui, game, relief, flat) {
  const r = new Rec(gui, game);
  const timer = game.getGameTimer();
  r.record("constructed");
  gui.update(); r.record("first update");
  gui.place(0.6 * 1.3, -0.3, -0.75); gui.setReliefDepth(1); gui.update(); r.record("vr placed");
  game.start(); for (let i = 0; i < 40; i++) timer.tick(); gui.update(); r.record("40 ticks");
  // a hover over every cell, and the split cells' halves and the line between them
  const points = [];
  for (let c = 0; c < 19; c++) points.push([c * 16 + 8, 30]);
  for (const c of [16, 17, 18]) for (const y of [16.5, 18, 26.9, 27, 27.5, 28, 39.9]) points.push([c * 16 + 3, y]);
  points.push([100, 8], [40, 16], [350, 20], [415.9, 39.9], [0.2, 20], [303.99, 20], [304, 20]);
  for (const [px, py] of points) { clock.now += 7; gui.setHover(uvAt(px, py)); gui.update(); r.record("hover " + px + "," + py); }
  gui.setHover(null); gui.update(); r.record("hover off");
  gui.setHover(uvAt(17 * 16 + 8, 35)); gui.setRelief(!relief); gui.update(); r.record("relief flipped while hovering");
  gui.setRelief(relief); gui.setFlatSkills(!flat); gui.update(); r.record("flat flipped while hovering");
  gui.setFlatSkills(flat); gui.update(); r.record("flat back");
  gui.setReliefDepth(3); gui.update(); r.record("depth 3");
  gui.place(500, -180, -600); gui.update(); r.record("monitor placed");
  // presses: skill cells, a split cell's halves and line, the buttons of the mouse
  for (const [px, py, b] of [[2 * 16 + 5, 30, 0], [3 * 16 + 5, 30, 0], [17 * 16 + 5, 20, 0], [17 * 16 + 5, 27, 2], [17 * 16 + 5, 33, 0],
    [17 * 16 + 5, 33, 0], [16 * 16 + 5, 33, 2], [16 * 16 + 5, 20, 1], [12 * 16 + 5, 30, 0], [100, 8, 0], [13 * 16 + 4, 20, 0]]) {
    gui.onMouseDown(uvAt(px, py), b); gui.onMouseUp(uvAt(px, py)); gui.update(); r.record("press " + px + "," + py + " b" + b);
  }
  gui.onMouseUp(null); gui.update(); r.record("up null");
  gui.onDoubleClick(uvAt(13 * 16 + 4, 20)); gui.update(); r.record("double click nuke");
  // the minimap: a view, a drag across it, off it, and a double click on it
  gui.setViewRect({ x0: 10, y0: 5, x1: 330, y1: 165 });
  gui.onMouseDown(uvAt(340, 20), 0); gui.update(); r.record("map down");
  gui.onMouseMove(uvAt(360.5, 25)); gui.update(); r.record("map move");
  gui.onMouseMove(uvAt(200, 25)); gui.update(); r.record("map move off");
  gui.onMouseMove(uvAt(360, 25)); gui.onMouseUp(uvAt(360, 25)); gui.update(); r.record("map up");
  gui.onMouseDown(uvAt(330, 30), 2); gui.onMouseMove(null); gui.onMouseUp(uvAt(330, 30)); gui.update(); r.record("map down, null move");
  gui.onDoubleClick(uvAt(330, 30)); r.record("map double click");
  r.log.push("isMinimap " + gui.isMinimap(uvAt(330, 30)) + " " + gui.isMinimap(uvAt(30, 30)) + " " + gui.isMinimap(null));
  // counts change under a hovered button
  gui.setHover(uvAt(3 * 16 + 8, 30));
  timer.continue(); for (let i = 0; i < 30; i++) timer.tick(); gui.update(); r.record("30 more, hovering");
  gui.setHover(null); gui.update(); r.record("end");
  return r.steps;
}

async function main() {
  const levels = sampleLevels(34, 3).filter((_, i) => i % 2 === 0);
  const factory = new GameFactory();
  const rows = [];
  for (const entry of levels) {
    for (const [relief, flat] of [[true, true], [true, false], [false, true], [false, false]]) {
      const row = { id: entry.id, url: entry.url, packDir: entry.packDir, relief, flat };
      try {
        clock.now = 0;
        const game = await factory.create(entry);
        const scene = { add() {}, remove() {} };
        const resources = { track: (x) => x };
        const gui = new GuiPanel(scene, game, resources);
        gui.setRelief(relief);
        gui.setFlatSkills(flat);
        await Lemmix.loadPanelAssets(Lemmix.io, game.packDir || null);
        await new Promise((resolve) => setImmediate(resolve)); // the panel's .then has run
        row.labels = labels(gui);
        row.steps = script(gui, game, relief, flat);
        gui.dispose();
      } catch (e) { row.error = String(e && e.stack || e); }
      rows.push(row);
    }
  }
  const sha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  fs.mkdirSync(OUT, { recursive: true });
  const file = path.join(OUT, "skillbar.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: sha, levels: rows }).replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} runs, ${rows.reduce((a, r) => a + (r.steps || []).length, 0)} steps, ${rows.filter((r) => r.error).length} errors -> ${path.relative(process.cwd(), file)}`);
  for (const r of rows) if (r.error) console.log(r.id, r.error.split("\n").slice(0, 4).join(" | "));
}
main().catch((e) => { console.error(e); process.exit(1); });
