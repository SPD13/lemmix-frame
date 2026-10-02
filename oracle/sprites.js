#!/usr/bin/env node
"use strict";
/**
 * The lemming sprite oracle (web/lemmix/js/sprites.js): for every sprite set under
 * neolemmix/styles/<set>/lemmings (and a set that does not exist, which falls back to the
 * default), SpriteSet.frame for every action, both directions, every frame index (and a few past
 * the end and before the start, which wrap) and every variant - normal, athlete, zombie,
 * neutral, each +selected, and the flat clear-physics colours Lemming.render can ask for - one
 * hash per (action, direction, variant). Then generatePickupIcons on a sample of levels with
 * pickups: the generated frames and every pickup gadget's composite at its two frames (the gadget object's frame list
 * is the renderer's and is left out).
 *   node oracle/sprites.js
 * C# twin: core/Lemmix.Core.Tests/Ui/SpriteTests.cs
 */
const fs = require("fs");
const path = require("path");
const { Lemmix, nodeIO, ASSETS, WEB, OUT, hashFrameInto, hashBitmapInto, levelsWithPacks } = require("./lib/ui-env");
const { StateHash } = require("./lib/hash");

const ACTIONS = 35;
const VARIANTS = ["normal", "athlete", "zombie", "neutral"].flatMap((v) => [v, v + "+selected"]);

/** Every flat colour Lemming.render computes (game.js: perm, selected, neutral, zombie). */
function flatVariants() {
  const out = new Set();
  for (let bits = 0; bits < 16; bits++) {
    let c = bits & 1 ? 0x00FFFF : 0x0000FF;
    if (bits & 2) c |= 0x7F0000;
    if (bits & 4) c ^= 0xFFFFFF;
    if (bits & 8) c = (c | 0x007F00) & ~0x0000C0;
    out.add("flat:" + c.toString(16).padStart(6, "0"));
  }
  return [...out];
}

function frameIndices(anim) {
  if (!anim) return [0];
  const n = anim.frameCount;
  const list = [];
  for (let i = 0; i < n; i++) list.push(i);
  list.push(n, n + 1, 2 * n + 3, -1, -n - 2);
  return list;
}

async function main() {
  const io = nodeIO(ASSETS);
  const stylesDir = path.join(ASSETS, "neolemmix", "styles");
  const sets = fs.readdirSync(stylesDir).filter((d) => fs.existsSync(path.join(stylesDir, d, "lemmings", "scheme.nxmi"))).sort();
  sets.push("no_such_sprite_set");
  const variants = VARIANTS.concat(flatVariants());
  const out = { webSha: null, variants, sets: [], pickups: [] };
  for (const name of sets) {
    const sprites = await new Lemmix.SpriteSet(io).load(name);
    const row = { name, anims: {}, recolor: null, frames: {} };
    const rh = new StateHash();
    for (const kind of ["athlete", "zombie", "neutral", "selected"]) {
      const pairs = sprites.recolor[kind] || null;
      if (!pairs) { rh.word(0x7fffffff); continue; }
      rh.word(pairs.length); for (const [a, b] of pairs) { rh.num(a, "from"); rh.num(b, "to"); }
    }
    row.recolor = rh.hex();
    for (const [an, a] of Object.entries(sprites.anims).sort((x, y) => (x[0] < y[0] ? -1 : 1))) {
      row.anims[an] = [a.frameCount, a.frameDiff, a.width, a.height, a.right.footX, a.right.footY, a.left.footX, a.left.footY];
    }
    for (let action = 0; action < ACTIONS; action++) {
      const anim = sprites.animationFor(action);
      for (const dx of [1, -1]) for (const v of variants) {
        const h = new StateHash();
        for (const f of frameIndices(anim)) hashFrameInto(h, sprites.frame(action, dx, f, v));
        row.frames[action + "/" + (dx > 0 ? "r" : "l") + "/" + v] = h.hex();
      }
    }
    out.sets.push(row);
  }

  // pickup icons, on the levels that have pickups (every 3rd of them)
  const styles = new Lemmix.StyleManager(io);
  await Lemmix.loadMasks(io);
  const spriteSets = new Map();
  const candidates = levelsWithPacks().filter(({ level }) => /^\s*SKILL\s+[A-Z]/mi.test(fs.readFileSync(path.join(ASSETS, level.url), "utf8")));
  for (const [i, { level: entry }] of candidates.entries()) {
    if (i % 3 !== 0) continue;
    const row = { id: entry.id, url: entry.url };
    try {
      const data = Lemmix.LevelBuilder.parseLevel(fs.readFileSync(path.join(ASSETS, entry.url), "utf8"));
      const level = await Lemmix.LevelBuilder.build(data, styles, { seed: entry.id });
      const setName = (level.theme && level.theme.lemmings) || "default";
      if (!spriteSets.has(setName)) spriteSets.set(setName, await new Lemmix.SpriteSet(io).load(setName));
      Lemmix.generatePickupIcons(level, spriteSets.get(setName), level.theme);
      const h = new StateHash();
      let pickups = 0;
      const seen = new Set();
      for (const g of level.gadgets) {
        if (g.effectBase !== "PICKUP") continue;
        pickups++;
        const primary = g.meta.base.primary;
        if (!seen.has(primary)) {
          seen.add(primary);
          h.word(primary.frames.length); h.num(primary.width, "pw"); h.num(primary.height, "ph");
          for (const f of primary.frames) hashBitmapInto(h, f);
        }
        const pa = g.animations.find((a) => a.primary);
        const saved = pa ? pa.frame : 0;
        hashFrameInto(h, g.render());
        if (pa) { pa.frame = saved - 1; hashFrameInto(h, g.render()); pa.frame = saved; }
      }
      row.pickups = pickups;
      row.hash = h.hex();
    } catch (e) { row.error = String(e && e.stack || e); }
    out.pickups.push(row);
  }
  out.webSha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  fs.mkdirSync(OUT, { recursive: true });
  const file = path.join(OUT, "sprites.json");
  fs.writeFileSync(file, JSON.stringify(out).replace(/\},\{"(name|id)"/g, '},\n{"$1"') + "\n");
  const frames = out.sets.reduce((a, s) => a + Object.keys(s.frames).length, 0);
  console.log(`${out.sets.length} sprite sets, ${frames} action/direction/variant hashes, ${out.pickups.length} pickup levels ` +
    `(${out.pickups.filter((r) => r.error).length} errors) -> ${path.relative(process.cwd(), file)}`);
}
main().catch((e) => { console.error(e); process.exit(1); });
