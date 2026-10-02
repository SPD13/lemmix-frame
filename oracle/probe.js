#!/usr/bin/env node
"use strict";
/**
 * The picking and shadows oracle: no-input runs where, every 60 frames, a few cursor points
 * around the lemmings are probed - for every active skill and for none, which lemming
 * getPriorityLemming picks and how many are under the cursor, with the direction filter off,
 * left and right and with walkers only - and the skill shadows (shadows.js compute) of the
 * picked lemming for each active skill. All of it goes into one hash per probe frame; the run's
 * state after the probes is hashed too, so a probe that changed the game shows.
 *   node oracle/probe.js [prefix] [--every N]   (default: every 4th level)
 */
const fs = require("fs");
const path = require("path");
const { Lemmix, nodeIO, listLevels, ASSETS, WEB, OUT } = require("./lib/env");
const { StateHash } = require("./lib/hash");
const { schemaOf, hashFrame } = require("./lib/state");

const PROBE_EVERY = 60, FRAMES = 900;
const OFFSETS = [[0, 0], [-6, -5], [3, 2], [-9, -9], [5, -12]];

function probeFrame(sim) {
  const h = new StateHash();
  const skills = [null].concat(sim.activeSkills);
  const live = sim.lemmings.filter((L) => !L.removed).slice(0, 6);
  for (const L of live) for (const [ox, oy] of OFFSETS) {
    const mx = L.x + ox, my = L.y + oy;
    for (const dx of [0, -1, 1]) for (const walkers of [false, true]) {
      sim.selectDx = dx; sim.selectWalkerOnly = walkers;
      for (const s of skills) {
        const action = s ? Lemmix.SKILL_TO_ACTION[s] : Lemmix.BA.NONE;
        const p = sim.getPriorityLemming(action, mx, my);
        h.num(p.lemming ? p.lemming.index : -1, "pick"); h.num(p.count, "count");
      }
    }
    sim.selectDx = 0; sim.selectWalkerOnly = false;
    const p = sim.getPriorityLemming(Lemmix.BA.NONE, mx, my);
    if (!p.lemming) continue;
    for (const s of sim.activeSkills) {
      const r = Lemmix.Shadows.compute(sim, p.lemming, s);
      for (const list of [r.low, r.high, r.bricks]) {
        h.word(list.length);
        for (const [x, y] of list) { h.num(x, "sx"); h.num(y, "sy"); }
      }
    }
  }
  return h.hex();
}

async function main() {
  const args = process.argv.slice(2);
  const every = (() => { const i = args.indexOf("--every"); return i >= 0 ? +args[i + 1] : 4; })();
  const prefix = args.find((a, i) => !a.startsWith("--") && !(args[i - 1] || "").startsWith("--"));
  const io = nodeIO(ASSETS);
  const styles = new Lemmix.StyleManager(io), masks = await Lemmix.loadMasks(io);
  const levels = listLevels(ASSETS, prefix).filter((_, i) => i % every === 0);
  const rows = [];
  const t0 = Date.now();
  for (const entry of levels) {
    const row = { id: entry.id, url: entry.url };
    try {
      const data = Lemmix.LevelBuilder.parseLevel(fs.readFileSync(path.join(ASSETS, entry.url), "utf8"));
      const level = await Lemmix.LevelBuilder.build(data, styles, { seed: entry.id });
      const sim = new Lemmix.LemGame(level, masks);
      sim.start();
      const schema = schemaOf(Lemmix, sim);
      row.probes = [];
      for (let f = 1; f <= FRAMES && !sim.gameFinished && !sim.stateIsUnplayable; f++) {
        sim.update();
        if (f % PROBE_EVERY === 0) row.probes.push(probeFrame(sim) + ":" + hashFrame(sim, schema, true).hex());
      }
    } catch (e) { row.error = String(e && e.stack || e); }
    rows.push(row);
  }
  const sha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  const file = path.join(OUT, "probe.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: sha, probeEvery: PROBE_EVERY, frames: FRAMES, offsets: OFFSETS, levels: rows })
    .replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} levels, ${rows.reduce((a, r) => a + (r.probes || []).length, 0)} probe frames, ${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s`);
}
main().catch((e) => { console.error(e); process.exit(1); });
