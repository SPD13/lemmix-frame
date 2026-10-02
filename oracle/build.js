#!/usr/bin/env node
"use strict";
/**
 * The level-build oracle: every level parsed and built by the web engine, nothing simulated.
 * Writes oracle/out/build.json: per level the terrain hash (physics map, picture, ground mask),
 * and the numbers the build derives (counts, spawn order, start, skills, gadget effects and
 * trigger rectangles, receiver pairing) - so a mismatch in the port says what differs.
 *   node oracle/build.js [prefix]
 */
const fs = require("fs");
const path = require("path");
const { Lemmix, nodeIO, listLevels, ASSETS, WEB, OUT } = require("./lib/env");
const { StateHash } = require("./lib/hash");
const { terrainHash } = require("./lib/state");

function summary(level) {
  const h = new StateHash();
  for (const v of [level.width, level.height, level.releaseCount, level.needCount, level.zombieCount, level.neutralCount,
    level.spawnInterval, level.spawnLocked, level.timeLimitSeconds, level.startX, level.startY, level.screenPositionX]) h.any(v, "level");
  h.word(level.spawnOrder.length); for (const i of level.spawnOrder) h.num(i, "spawn");
  h.word(level.skills.length); for (const s of level.skills) { h.str(s.name); h.num(s.count, "skill"); }
  h.word(level.gadgets.length);
  for (const g of level.gadgets) {
    h.str(g.effect); h.str(g.effectBase); h.num(g.x, "x"); h.num(g.y, "y"); h.num(g.width, "w"); h.num(g.height, "h");
    const r = g.triggerRect; h.num(r.x0, "x0"); h.num(r.y0, "y0"); h.num(r.x1, "x1"); h.num(r.y1, "y1");
    h.num(g.receiverId, "recv"); h.num(g.pairingId, "pair"); h.any(g.flipLemming, "flip"); h.num(g.remainingLemmings, "rem");
    h.num(g.skill, "skill"); h.num(g.skillCount, "count"); h.num(g.angleSegment, "angle");
    h.word(g.animations.length); for (const a of g.animations) { h.num(a.frame, "frame"); h.str(a.state); h.any(a.visible, "vis"); }
  }
  h.word(level.background.color);
  h.any(level.background.image ? level.background.image.width : null, "bg");
  return h.hex();
}

async function main() {
  const prefix = process.argv[2];
  const io = nodeIO(ASSETS);
  const styles = new Lemmix.StyleManager(io);
  await Lemmix.loadMasks(io); // sets Lemmix.digitFont, as the sim and the page do
  const levels = listLevels(ASSETS, prefix);
  const t0 = Date.now();
  const rows = [];
  for (const entry of levels) {
    const row = { id: entry.id, url: entry.url };
    try {
      const data = Lemmix.LevelBuilder.parseLevel(fs.readFileSync(path.join(ASSETS, entry.url), "utf8"));
      const level = await Lemmix.LevelBuilder.build(data, styles, { seed: entry.id });
      row.terrain = terrainHash(level);
      row.summary = summary(level);
    } catch (e) { row.error = String(e && e.message || e); }
    rows.push(row);
  }
  const sha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  fs.mkdirSync(OUT, { recursive: true });
  const file = path.join(OUT, "build.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: sha, levels: rows }).replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} levels, ${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
}
main().catch((e) => { console.error(e); process.exit(1); });
