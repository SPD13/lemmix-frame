#!/usr/bin/env node
"use strict";
/**
 * The skill panel oracle (web/lemmix/js/panel.js GamePanel, drawn into the classic
 * Lemmings.DisplayImage the 3D page hands it): on a sample of levels (lib/ui-game.js
 * sampleLevels - every Nth level of the index plus levels of the packs that ship their own
 * panel graphics), a Game built as the page builds it goes through a fixed script of states
 * and presses. After every step: the panel's pixels with its layout (cells, minimap window,
 * split cells, relief masks) in one hash, and the game around it in another - the sim's frame
 * state, the selected skill, direction filter, clear physics, nuke arming, timer, replay mode,
 * the command log, the held buttons and the load-replay requests. Some steps also hash what
 * Lemming.prototype.render draws for every lemming (the sprite variant, the countdown).
 *   node oracle/panel.js [count]
 * C# twin: core/Lemmix.Core.Tests/Ui/PanelTests.cs, which runs the same script.
 */
const fs = require("fs");
const path = require("path");
const { Lemmix, Lemmings, WEB, OUT, clock, hashFrameInto } = require("./lib/ui-env");
const { sampleLevels, GameFactory } = require("./lib/ui-game");
const { StateHash } = require("./lib/hash");
const { schemaOf, hashFrame } = require("./lib/state");

const CELL = 16;
// presses on every cell: y in the upper half, on the line between the halves, in the lower half
const PRESS_YS = [20, 27, 33];

class Run {
  constructor(game, gui, display, schema) {
    this.game = game; this.gui = gui; this.display = display; this.schema = schema;
    this.loadRequests = 0;
    game.onLoadReplayRequest = () => { this.loadRequests++; };
    this.steps = [];
  }

  panelHash() {
    const h = new StateHash();
    const img = this.display.getImageData();
    h.num(img.width, "w"); h.num(img.height, "h"); h.bytes(img.data);
    const L = this.gui.layout;
    for (const v of [L.buttons, L.digitButtons, L.width, L.height, L.sharedBorder, L.reliefFromMasks]) h.any(v, "layout");
    h.word(L.cells.length); for (const c of L.cells) h.str(c);
    const m = L.minimap;
    for (const v of [m.x, m.y, m.w, m.h, m.scaleX, m.scaleY, m.pad]) h.any(v, "minimap");
    h.word(L.splitCells.length); for (const c of L.splitCells) h.num(c, "split");
    h.num(L.halfRows.upperBottom, "ub"); h.num(L.halfRows.lowerTop, "lt");
    if (!L.reliefMasks) h.word(0x7fffffff);
    else { h.bytes(L.reliefMasks.art); h.bytes(L.reliefMasks.digits); }
    return h.hex();
  }

  gameHash() {
    const g = this.game, sim = g.sim, timer = g.getGameTimer();
    const h = hashFrame(sim, this.schema, false);
    h.str(sim.selectedSkill); h.num(sim.selectDx, "dx"); h.any(g.clearPhysics, "cpm"); h.any(g.nukePrepared, "nuke");
    h.any(timer.isRunning(), "running"); h.num(timer.speedFactor, "speed"); h.num(timer.tickIndex, "tick");
    if (g.replayMode) { h.str(g.replayMode.kind); h.num(g.replayMode.cutVersion, "cut"); h.num(g.replayMode.recordVersion, "rec"); }
    else h.word(0x7fffffff);
    h.any(sim.replayInsert, "insert"); h.any(sim.replaying, "replaying");
    h.str(g.commandManager.serialize());
    h.num(this.gui.rrHeld, "rr"); h.num(this.gui.rrNext, "rrn");
    if (this.gui.held) { h.num(this.gui.held.step, "step"); h.num(this.gui.held.next, "next"); } else h.word(0x7fffffff);
    h.num(this.loadRequests, "load");
    h.word(sim.recorded.length);
    for (const r of sim.recorded) { h.str(r.type); h.num(r.frame, "rf"); h.str(r.skill || null); h.num(r.type === "assignment" ? r.lemIndex : -1, "ri"); }
    return h.hex();
  }

  /** What Lemming.render draws, for every lemming. */
  lemmingsHash() {
    const h = new StateHash();
    const display = { drawFrame: (frame, x, y) => { hashFrameInto(h, frame); h.num(x, "x"); h.num(y, "y"); } };
    for (const L of this.game.sim.lemmings) { h.word(L.index); L.render(display); }
    return h.hex();
  }

  record(label, withLemmings) {
    this.steps.push([label, this.panelHash() + ":" + this.gameHash() + (withLemmings ? ":" + this.lemmingsHash() : "")]);
  }

  ticks(n) { for (let i = 0; i < n; i++) this.game.getGameTimer().tick(); }
  down(x, y, button) { this.display.onMouseDown.trigger({ x, y, button }); }
  up() { this.display.onMouseUp.trigger({ x: 0, y: 0 }); }
  firstLive() { return this.game.sim.lemmings.find((L) => !L.removed) || null; }
}

/** The script; PanelTests.cs RunScript is its twin, step for step. */
async function script(game, gui, display, schema, flat) {
  const r = new Run(game, gui, display, schema);
  const timer = game.getGameTimer();
  r.record("start", true);
  gui.setFlatBackground(true); r.record("flat on");
  gui.setFlatBackground(false); r.record("flat off");
  gui.setFlatBackground(flat);
  game.start(); r.ticks(30); r.record("30 frames", true);
  for (let i = 0; i < 400 && !r.firstLive(); i++) r.ticks(1);
  r.ticks(10); r.record("lemming out", true);
  // a skill selected through its cell (the second panel skill), then the first (0 is a skill)
  const nSkills = game.sim.activeSkills.length;
  r.down(CELL * (2 + Math.min(1, Math.max(0, nSkills - 1))) + 3, 20, 0); r.up(); r.record("skill cell");
  r.down(CELL * 2 + 3, 20, 0); r.up(); r.record("first skill cell");
  // the lemming under the pointer, then made an athlete step by step
  const L = r.firstLive();
  game.cursorLemming = L; gui.render(true); r.record("cursor lemming", true);
  if (L) {
    L.isClimber = true; gui.render(true); r.record("climber", true);
    L.isFloater = true; gui.render(true); r.record("athlete", true);
    L.isSwimmer = true; L.isDisarmer = true; gui.render(true); r.record("quadathlete", true);
    game.showAthleteInfo = true; gui.render(true); r.record("athlete info");
    game.showAthleteInfo = false;
  }
  // a skill to that lemming, through the command the page queues on a click
  if (L) { game.queueCmmand(new Lemmings.CommandLemmingsAction(L.id)); }
  r.ticks(25); r.record("assigned + 25", true);
  // the selectors
  const press = (x, y, button, label) => { r.down(x, y, button); r.up(); r.record(label); };
  const cellX = (what) => gui.cells.indexOf(what) * CELL + 7;
  press(cellX("pause"), 20, 0, "pause");
  press(cellX("pause"), 20, 0, "unpause");
  for (let i = 0; i < 4; i++) press(cellX("speed"), 25, 0, "speed " + i);
  press(cellX("directional"), 20, 0, "dir left");
  press(cellX("directional"), 33, 0, "dir right");
  press(cellX("directional"), 27, 0, "dir line");
  press(cellX("directional"), 33, 0, "dir off");
  press(cellX("cpmreplay"), 18, 0, "clear physics"); r.record("clear physics lemmings", true);
  press(cellX("cpmreplay"), 36, 0, "load replay");
  press(cellX("cpmreplay"), 26, 0, "clear physics off");
  // release rate: one change on the press, then one per tick once held 250 ms
  clock.now = 500;
  r.down(cellX("rrplus"), 30, 0); r.ticks(3); r.record("rr+ press");
  clock.now = 749; r.ticks(1); r.record("rr+ 249 ms");
  clock.now = 750; r.ticks(3); r.record("rr+ held"); r.up(); r.record("rr+ up");
  r.ticks(2); r.record("rr+ released");
  clock.now = 800; r.down(cellX("rrminus"), 30, 2); r.ticks(2); clock.now = 1050; r.ticks(2); r.up(); r.record("rr- held");
  // the replay: restart, insert mode
  press(cellX("restart"), 20, 0, "restart");
  game.toggleReplayInsert(); r.record("insert mode");
  game.toggleReplayInsert();
  r.ticks(5); r.record("replaying", true);
  // frame skips, with the hold-repeat on the clock
  clock.now = 1000;
  r.down(cellX("frameskip"), 33, 0); r.record("forward 1");
  for (const t of [1100, 1249, 1250, 1300, 1350, 1449, 1450]) { clock.now = t; gui.poll(t); r.record("poll " + t); }
  r.up(); clock.now = 1700; gui.poll(1700); r.record("released");
  r.down(cellX("frameskip"), 20, 0); clock.now = 1950; gui.poll(1950); r.up(); r.record("back 1 held");
  press(cellX("frameskip"), 20, 2, "back 17");
  press(cellX("frameskip"), 33, 1, "forward 85");
  press(cellX("frameskip"), 20, 1, "back 85");
  press(cellX("frameskip"), 27, 0, "frameskip line");
  press(cellX("frameskip"), 33, 2, "forward 17");
  press(cellX("frameskip"), 33, 4, "forward other button");
  // every cell, every row, every button (the nuke apart)
  for (let c = 0; c < gui.cells.length; c++) {
    if (gui.cells[c] === "nuke" || gui.cells[c] === "speed") continue;
    for (const y of PRESS_YS) for (const b of [0, 1, 2]) press(c * CELL + 1 + ((y + b) % 14), y, b, "cell " + c + " y" + y + " b" + b);
    r.ticks(1);
  }
  // off the buttons: the strip, the minimap, left of the first cell (x -5 is cell 0 to Math.trunc)
  press(5, 10, 0, "strip");
  press(gui.cells.length * CELL + 20, 20, 0, "minimap");
  press(-5, 20, 0, "x -5");
  press(-20, 20, 0, "x -20");
  press(CELL * 2 + 0.5, 15.9, 0, "y 15.9");
  r.ticks(10); r.record("10 more", true);
  // the nuke: armed, then a double click
  press(cellX("nuke"), 20, 0, "nuke armed");
  display.onDoubleClick.trigger({ x: cellX("nuke"), y: 22 }); r.record("nuke double click");
  r.ticks(20); r.record("nuked", true);
  // a disposed panel still takes presses (its listeners stay) but draws nothing
  gui.dispose();
  press(cellX("pause"), 20, 0, "disposed pause");
  return r.steps;
}

async function main() {
  const count = +(process.argv[2] || 34);
  const levels = sampleLevels(count, 3);
  const factory = new GameFactory();
  const rows = [];
  const t0 = Date.now();
  let schema = null;
  for (const [i, entry] of levels.entries()) {
    const row = { id: entry.id, url: entry.url, packDir: entry.packDir, flat: i % 2 === 0 };
    try {
      clock.now = 0;
      const game = await factory.create(entry);
      if (!schema) schema = schemaOf(Lemmix, game.sim);
      const stage = { createImage: (d, w, h) => ({ width: w, height: h, data: new Uint8ClampedArray(w * h * 4) }), setGameViewPointPosition() {}, redraw() {} };
      const display = new Lemmings.DisplayImage(stage);
      game.setGuiDisplay(display);
      await Lemmix.loadPanelAssets(Lemmix.io, game.packDir || null);
      await new Promise((resolve) => setImmediate(resolve)); // the panel's .then has run
      if (!game.gui.assets) throw new Error("panel assets not in");
      row.steps = await script(game, game.gui, display, schema, row.flat);
    } catch (e) { row.error = String(e && e.stack || e); }
    rows.push(row);
  }
  const sha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  fs.mkdirSync(OUT, { recursive: true });
  const file = path.join(OUT, "panel.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: sha, levels: rows }).replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} levels, ${rows.reduce((a, r) => a + (r.steps || []).length, 0)} steps, ` +
    `${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
  for (const r of rows) if (r.error) console.log(r.id, r.error.split("\n").slice(0, 3).join(" | "));
}
main().catch((e) => { console.error(e); process.exit(1); });
