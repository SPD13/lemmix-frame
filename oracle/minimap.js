#!/usr/bin/env node
"use strict";
/**
 * The minimap oracle (web/3d/js/minimap.js MiniMap, on the canvas stand-in of lib/ui-env.js):
 * on every other level of the panel sample, two minimaps over one Game - the Lemmix panel's
 * window (104x34, 8 level px per map px, padded) and a 4x one without padding, so both the
 * centred and the scrolling map are seen - through a fixed script: no view yet, view
 * rectangles inside, across and outside the level (fractional too), a frozen offset, then the
 * game running with skills given (terrain changing under the map), and a rewind (the page marks
 * the terrain dirty, app.js refreshAfterRestore). After each step: the window's pixels, the
 * offset, the frame and a few points through pointToLevel/contains.
 *   node oracle/minimap.js
 * C# twin: core/Lemmix.Core.Tests/Ui/MinimapTests.cs
 */
const fs = require("fs");
const path = require("path");
const { Lemmings, WEB, OUT, loadScript } = require("./lib/ui-env");
const { sampleLevels, GameFactory } = require("./lib/ui-game");
const { StateHash } = require("./lib/hash");

loadScript("3d/js/minimap.js", ["MiniMap"]);

const SPECS = [
  { x: 19 * 16 + 1 + 3, y: 3, w: 104, h: 34, scaleX: 8, scaleY: 8, pad: 1 },
  { x: 0, y: 0, w: 104, h: 34, scaleX: 4, scaleY: 4, pad: 0 },
];
const DIG_SKILLS = ["DIGGER", "MINER", "BASHER", "BUILDER", "BOMBER", "PLATFORMER", "STACKER", "FENCER"];

function viewHash(m) {
  const h = new StateHash();
  h.bytes(m.view.data);
  h.num(m.offX, "ox"); h.num(m.offY, "oy");
  const f = m._frame();
  if (f) { h.num(f.l, "l"); h.num(f.t, "t"); h.num(f.r, "r"); h.num(f.b, "b"); } else h.word(0x7fffffff);
  const s = m.spec;
  for (const [px, py] of [[s.x, s.y], [s.x + 50.5, s.y + 17], [s.x + 103, s.y + 33], [s.x - 10, s.y + 5], [s.x + 104, s.y + 34]]) {
    h.any(m.contains(px, py), "in");
    const p = m.pointToLevel(px, py);
    h.num(Math.round(p.x * 16), "px"); h.num(Math.round(p.y * 16), "py");
  }
  return h.hex();
}

function script(game, maps) {
  const steps = [];
  const level = game.level, W = level.width, H = level.height;
  const rec = (label) => { for (const m of maps) m.update(); steps.push([label, maps.map(viewHash).join(":")]); };
  const view = (r) => { for (const m of maps) m.setViewRect(r ? { x0: r[0], y0: r[1], x1: r[2], y1: r[3] } : null); };
  rec("no view");
  const rects = [[0, 0, W, H], [0, 0, 160, 100], [W / 2 - 80, H / 2 - 50, W / 2 + 80, H / 2 + 50], [W - 100, H - 60, W + 40, H + 20],
    [-30.5, -10.25, 120.75, 90.5], [33.7, 12.2, 33.9, 12.4], [W * 0.75, 0, W * 0.75 + 320, 160]];
  rects.forEach((r, i) => { view(r); rec("rect " + i); });
  view(null); rec("rect null");
  for (const m of maps) m.setFreeze(true);
  view([0, 0, 160, 100]); rec("frozen");
  for (const m of maps) m.setFreeze(false);
  rec("unfrozen");
  game.start();
  const timer = game.getGameTimer();
  for (let f = 1; f <= 150; f++) {
    timer.tick();
    if (f === 40 || f === 80) {
      const names = game.sim.activeSkills;
      let i = names.findIndex((n) => DIG_SKILLS.includes(n) && game.sim.skillCount(n) > 0);
      if (i < 0) i = 0;
      game.getGameSkills().setSelectedSkill(i);
      const live = game.sim.lemmings.filter((L) => !L.removed);
      const L = live[f === 40 ? 0 : live.length - 1];
      if (L) game.queueCmmand(new Lemmings.CommandLemmingsAction(L.id));
    }
    if (f % 25 === 0) {
      const L = game.sim.lemmings.find((x) => !x.removed);
      if (L) view([L.x - 80, L.y - 50, L.x + 80, L.y + 50]);
      rec("frame " + f);
    }
  }
  game.backFrames(60);
  for (const m of maps) m.terrainDirty = true; // app.js refreshAfterRestore
  rec("back 60");
  for (let i = 0; i < 30; i++) timer.tick();
  rec("30 more");
  return steps;
}

async function main() {
  const levels = sampleLevels(34, 3).filter((_, i) => i % 2 === 0);
  const factory = new GameFactory();
  const rows = [];
  for (const entry of levels) {
    const row = { id: entry.id, url: entry.url };
    try {
      const game = await factory.create(entry);
      const gui = { mesh: null };
      const scene = { add() {}, remove() {} };
      const resources = { track: (x) => x };
      const maps = SPECS.map((s) => new globalThis.MiniMap(gui, game, game.level, s, scene, resources));
      row.steps = script(game, maps);
      for (const m of maps) m.dispose();
    } catch (e) { row.error = String(e && e.stack || e); }
    rows.push(row);
  }
  const sha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  fs.mkdirSync(OUT, { recursive: true });
  const file = path.join(OUT, "minimap.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: sha, specs: SPECS, levels: rows }).replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} levels, ${rows.reduce((a, r) => a + (r.steps || []).length, 0)} steps, ${rows.filter((r) => r.error).length} errors -> ${path.relative(process.cwd(), file)}`);
  for (const r of rows) if (r.error) console.log(r.id, r.error.split("\n").slice(0, 3).join(" | "));
}
main().catch((e) => { console.error(e); process.exit(1); });
