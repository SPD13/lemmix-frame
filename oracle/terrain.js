#!/usr/bin/env node
"use strict";
/**
 * The terrain diorama oracle: depth.js, terrain.js and decals.js run under node on the web
 * engine's levels (lib/three-env.js), every buffer they produce hashed.
 *
 * Static, per level: the per-pixel maps (depth classes, piece ids, relief with the emboss
 * switch off and on, the surface-blend slots and donors, the colour-blend flags off and on),
 * then for every combination of the effect switches that shapes geometry - emboss, smooth,
 * edge smoothing (smoothterrain), colour blend off/soft/smooth - a TerrainMesh set up as
 * app.js loadLevel sets it up (constructor, setSmooth, setSmoothTerrain, setDecals) and the
 * hash of every chunk's geometry (position, color, uv, index with its type, groups), of every
 * decal skin's and of the level texture. One combination also goes through clear-physics
 * paint and back.
 *
 * Sim, per level and for a few combinations: a scripted player (destructive skills on the
 * panel, picked at random and written down as a script) digs, bashes, mines, builds and blows
 * up the terrain while the wrapper re-meshes; every tick the decals are painted from the
 * objects' current frames and flushDirty() runs with its budget, as app.js syncScene does,
 * and each rebuilt chunk is hashed in order. A state saved early is loaded at the end
 * (app.js's onSave/onLoad extras, then resync()), hashed whole, and the run goes on.
 *
 *   node oracle/terrain.js                     -> oracle/out/terrain.json
 *   node oracle/terrain.js --dump <id> <combo>  per-chunk hashes of one level, for diffing
 */
const fs = require("fs");
const path = require("path");
const { Lemmix, nodeIO, listLevels, ASSETS, WEB, OUT } = require("./lib/env");
const { StateHash, NULL_WORD } = require("./lib/hash");
const T = require("./lib/three-env");

// colour blend levels: app.js COLOR_BLEND_LEVELS
const SOFTNESS = { off: 0, soft: 0.5, smooth: 1 };
const COMBOS = [];
for (const emboss of [false, true])
  for (const smooth of [false, true])
    for (const smoothTerrain of [false, true])
      for (const colorBlend of ["off", "soft", "smooth"])
        COMBOS.push({ name: `e${+emboss}s${+smooth}t${+smoothTerrain}-${colorBlend}`, emboss, smooth, smoothTerrain, colorBlend });
const SIM_COMBOS = ["e0s0t0-off", "e1s0t0-off", "e1s1t1-soft", "e1s1t0-smooth"];
const PAINT_COMBO = "e1s1t1-soft";
const SIM_SKILLS = ["BOMBER", "STONER", "PLATFORMER", "BUILDER", "STACKER", "LASERER", "BASHER", "FENCER", "MINER", "DIGGER"];
const SIM_FRAMES = 600, SAVE_AT = 200, AFTER_FRAMES = 150, BLOCK = 100, DECAL_EVERY = 10, TEX_EVERY = 50;

function mulberry32(a) {
  return () => {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const seedOf = (s) => { let h = 2166136261; for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619); return h >>> 0; };

// ---------------------------------------------------------------- hashing

/** A BufferGeometry: each attribute (item size, Float32 bytes), the index (element size, bytes), the groups. */
function hashGeom(h, geom) {
  if (!geom) { h.word(NULL_WORD); return; }
  for (const name of ["position", "color", "uv", "normal"]) {
    const a = geom.attributes[name];
    if (!a) { h.word(NULL_WORD); continue; }
    h.word(a.itemSize);
    h.bytes(a.array);
  }
  if (!geom.index) h.word(NULL_WORD);
  else { h.word(geom.index.array.BYTES_PER_ELEMENT); h.bytes(geom.index.array); }
  h.word(geom.groups.length);
  for (const g of geom.groups) { h.word(g.start); h.word(g.count); h.word(g.materialIndex); }
}
const meshGeom = (m) => (m ? m.geometry : null);

function hashChunks(h, terrain) {
  h.word(terrain.chunkMeshes.length);
  for (const m of terrain.chunkMeshes) hashGeom(h, meshGeom(m));
}
function hashDecalChunks(h, terrain) {
  h.word(terrain.decalMeshes.length);
  for (const m of terrain.decalMeshes) hashGeom(h, meshGeom(m));
}
function hashMaps(h, terrain) {
  h.bytes(terrain.depth);
  h.bytes(terrain.relief);
  if (terrain.blend) h.bytes(terrain.blend.slot); else h.word(NULL_WORD);
  if (terrain.color) h.bytes(terrain.color); else h.word(NULL_WORD);
}
const hex = (fn) => { const h = new StateHash(); fn(h); return h.hex(); };

function hashBlend(h, blend) {
  h.bytes(blend.slot);
  h.word(blend.donors.length);
  for (const palette of blend.donors) {
    h.word(palette.length);
    for (const d of palette) { h.word(d.index); h.word(d.r); h.word(d.g); h.word(d.b); }
  }
}

// ---------------------------------------------------------------- the level and the mesh

async function buildLevel(env, entry, sim) {
  const data = Lemmix.LevelBuilder.parseLevel(fs.readFileSync(path.join(ASSETS, entry.url), "utf8"));
  if (sim) {
    // the destructive skills on the panel, plenty of each
    data.skills = {};
    for (const s of SIM_SKILLS) data.skills[s] = 30;
  }
  const level = await Lemmix.LevelBuilder.build(data, env.styles, { seed: entry.id });
  const game = new Lemmix.LemGame(level, env.masks);
  game.start();
  return { level, game };
}

/** What app.js loadLevel builds, for one combination of the switches. */
function makeTerrain(level, gd, profile, combo) {
  const depthMap = T.buildDepthMap(level, gd, profile);
  const pieceMap = T.buildPieceMap(level, gd);
  const reliefMap = T.buildReliefMap(level, pieceMap, profile, combo.emboss, gd);
  const blendMap = T.buildBlendMap(level, pieceMap, profile, gd);
  const colorMap = T.buildColorBlendMap(level, pieceMap, profile, combo.colorBlend !== "off", gd);
  const terrain = new T.TerrainMesh(new THREE.Group(), level, depthMap, reliefMap, T.resources, blendMap, colorMap, SOFTNESS[combo.colorBlend]);
  if (combo.smooth) terrain.setSmooth(true);
  if (combo.smoothTerrain) terrain.setSmoothTerrain(true);
  const decals = T.TerrainDecals.forLevel(level, T.resources, level.physics);
  if (decals) terrain.setDecals(decals);
  return { terrain, decals };
}

/** The decal draws of a tick: ObjectManager.render through SpriteCapture.drawFrameFlags, layer 1. */
function decalItems(level) {
  const items = [];
  for (const obj of level.objects) {
    const g = obj.gadget;
    if (g.effectBase === "NONE" && g.effect === "NONE" && !g.animations.length) continue;
    const props = obj.drawProperties;
    const layer = props.noOverwrite ? -2 : props.onlyOverwrite ? 1 : props.low ? -1 : 0;
    if (layer !== 1) continue;
    items.push({ frame: g.render(), x: obj.x, y: obj.y, flipY: !!props.isUpsideDown, oneWay: !!props.oneWay });
  }
  return items;
}

function staticRow(level, gd, profile) {
  const maps = {};
  const pieceMap = T.buildPieceMap(level, gd);
  maps.depth = hex((h) => h.bytes(T.buildDepthMap(level, gd, profile)));
  maps.piece = hex((h) => h.bytes(pieceMap));
  maps.relief = [false, true].map((on) => hex((h) => h.bytes(T.buildReliefMap(level, pieceMap, profile, on, gd))));
  const blend = T.buildBlendMap(level, pieceMap, profile, gd);
  maps.blend = hex((h) => hashBlend(h, blend));
  maps.donors = blend.donors.length;
  maps.color = [false, true].map((on) => hex((h) => h.bytes(T.buildColorBlendMap(level, pieceMap, profile, on, gd))));

  const combos = {};
  let paint = null;
  for (const combo of COMBOS) {
    const { terrain } = makeTerrain(level, gd, profile, combo);
    let verts = 0;
    for (const m of terrain.chunkMeshes) if (m) verts += m.geometry.attributes.position.count;
    combos[combo.name] = [
      hex((h) => hashChunks(h, terrain)), hex((h) => hashDecalChunks(h, terrain)),
      hex((h) => h.bytes(terrain.texData)), verts,
    ];
    if (combo.name === PAINT_COMBO) {
      // clear physics: the texture repainted from the physics map with the one-way bits lit, then back
      terrain.setPhysicsPaint(level.physics, Lemmix.PM.ONEWAYLEFT);
      const on = hex((h) => { h.bytes(terrain.texData); hashChunks(h, terrain); });
      terrain.setPhysicsPaint(null);
      const off = hex((h) => { h.bytes(terrain.texData); hashChunks(h, terrain); });
      paint = [on, off];
    }
  }
  return { maps, combos, paint };
}

// ---------------------------------------------------------------- the scripted run

/** Assignments at random, kept when they take; `phase` tells the runs before and after the rewind apart. */
function maybeAssign(game, rand, script, phase) {
  if (rand() >= 1 / 6) return;
  const skills = game.activeSkills;
  if (!skills.length) return;
  const skill = skills[Math.floor(rand() * skills.length)];
  const action = Lemmix.SKILL_TO_ACTION[skill];
  const cands = game.lemmings.filter((L) => !L.removed && !L.teleporting && L.portalWarpFrame === 0 &&
    game.mayAssign(action, L) && game.checkSkillAvailable(action));
  if (!cands.length) return;
  const L = cands[Math.floor(rand() * cands.length)];
  if (game.assignSkillTo(L, skill)) script.push([phase, game.currentIteration, L.index, skill]);
}

/**
 * One run. With `script` null the player invents it (and returns it); given, it is replayed.
 * Each tick: assignments, update, decals painted, flushDirty(), the rebuilt chunks hashed.
 */
function simRun(level, game, gd, profile, combo, script, seed) {
  const { terrain, decals } = makeTerrain(level, gd, profile, combo);
  const inventing = !script;
  if (inventing) script = [];
  const rand = mulberry32(seedOf(seed + "#terrain"));
  // what app.js keeps per saved state (game.states.onSave / onLoad)
  const extraOf = () => ({ depth: terrain.depth.slice(), relief: terrain.relief ? terrain.relief.slice() : null });

  let block = new StateHash(), blocks = [], tick = 0;
  let rebuilt = [];
  const origRebuild = terrain._rebuildChunk.bind(terrain);
  terrain._rebuildChunk = (cx, cy) => { origRebuild(cx, cy); rebuilt.push(cy * terrain.chunksX + cx); };
  game.adjustSpawnInterval(4); // as fast as the level allows, recorded like a player's
  const step = (phase) => {
    if (inventing) maybeAssign(game, rand, script, phase);
    else for (const s of script) if (s[0] === phase && s[1] === game.currentIteration) {
      if (!game.assignSkillTo(game.lemmings[s[2]], s[3])) throw new Error("replayed assignment refused");
    }
    game.update();
    tick++;
    if (decals) decals.paint(decalItems(level), false);
    rebuilt = [];
    terrain.flushDirty();
    block.word(game.currentIteration);
    block.word(rebuilt.length);
    for (const id of rebuilt) { block.word(id); hashGeom(block, meshGeom(terrain.chunkMeshes[id])); hashGeom(block, meshGeom(terrain.decalMeshes[id])); }
    block.word(terrain.dirtyChunks.size);
    if (decals && tick % DECAL_EVERY === 0) block.bytes(decals.data);
    if (tick % TEX_EVERY === 0) { block.bytes(terrain.texData); hashMaps(block, terrain); }
    if (tick % BLOCK === 0) { blocks.push(block.hex()); block = new StateHash(); }
  };

  let saved = null;
  for (let f = 0; f < SIM_FRAMES && !game.gameFinished && !game.stateIsUnplayable; f++) {
    if (game.currentIteration === SAVE_AT) { saved = game.saveState(); saved.extra = extraOf(); }
    step(0);
  }
  const out = { frames: tick, blocks: null, rewind: null, end: null }; // frames: the ticks before the rewind
  if (saved) {
    game.loadState(saved);
    terrain.depth.set(saved.extra.depth);
    if (saved.extra.relief && terrain.relief) terrain.relief.set(saved.extra.relief);
    rebuilt = [];
    terrain.resync();
    out.rewind = hex((h) => {
      h.word(rebuilt.length); for (const id of rebuilt) h.word(id);
      hashChunks(h, terrain); hashDecalChunks(h, terrain); h.bytes(terrain.texData); hashMaps(h, terrain);
    });
    for (let f = 0; f < AFTER_FRAMES && !game.gameFinished && !game.stateIsUnplayable; f++) step(1);
  }
  blocks.push(block.hex());
  out.blocks = blocks;
  out.end = hex((h) => { hashChunks(h, terrain); hashDecalChunks(h, terrain); h.bytes(terrain.texData); hashMaps(h, terrain); });
  return { out, script };
}

// ---------------------------------------------------------------- the sample

/** ~60 levels: every one with a style that has a profile (sampled), then one per other style. */
function sample(levels) {
  const profiled = fs.readdirSync(path.join(WEB, "3d", "profiles")).filter((f) => f.startsWith("nx-"))
    .map((f) => f.slice(3, -5));
  const picked = [];
  const take = (e) => { if (!picked.includes(e)) picked.push(e); };
  for (const style of profiled) {
    const withIt = levels.filter((e) => (e.styles || []).includes(style));
    const every = Math.max(1, Math.ceil(withIt.length / 10));
    withIt.forEach((e, i) => { if (i % every === 0) take(e); });
  }
  const seen = new Set(profiled);
  for (const e of levels) {
    if (picked.length >= 60) break;
    const fresh = (e.styles || []).filter((s) => !seen.has(s));
    if (!fresh.length) continue;
    fresh.forEach((s) => seen.add(s));
    take(e);
  }
  return picked;
}

async function main() {
  const args = process.argv.slice(2);
  const io = nodeIO(ASSETS);
  const env = { styles: new Lemmix.StyleManager(io), masks: await Lemmix.loadMasks(io) };
  const all = listLevels(ASSETS);

  if (args[0] === "--dump") {
    // per-chunk hashes of one level and combination (static), for finding the first difference
    const entry = all.find((e) => e.id === args[1]);
    const combo = COMBOS.find((c) => c.name === args[2]);
    const { level } = await buildLevel(env, entry, false);
    const gd = T.groundData(level);
    const { profile } = await T.loadProfile(gd);
    const { terrain } = makeTerrain(level, gd, profile, combo);
    terrain.chunkMeshes.forEach((m, i) => console.log(i, hex((h) => hashGeom(h, meshGeom(m))), m ? m.geometry.attributes.position.count : 0));
    return;
  }

  const only = args.indexOf("--only");
  const levels = sample(all).slice(0, only >= 0 ? +args[only + 1] : undefined);
  const rows = [];
  const t0 = Date.now();
  for (const entry of levels) {
    const row = { id: entry.id, url: entry.url };
    try {
      const { level } = await buildLevel(env, entry, false);
      const gd = T.groundData(level);
      const { urls, profile } = await T.loadProfile(gd);
      row.profiles = urls.map((u) => path.basename(u));
      row.size = [level.width, level.height];
      Object.assign(row, staticRow(level, gd, profile));
      row.sim = { script: null, runs: {} };
      for (const name of SIM_COMBOS) {
        const { level: lv, game } = await buildLevel(env, entry, true);
        const gdv = T.groundData(lv);
        const { out, script } = simRun(lv, game, gdv, profile, COMBOS.find((c) => c.name === name), row.sim.script, entry.id);
        row.sim.script = script;
        row.sim.runs[name] = out;
      }
    } catch (e) { row.error = String(e && e.stack || e); }
    rows.push(row);
    process.stderr.write(`${rows.length}/${levels.length} ${entry.id} ${((Date.now() - t0) / 1000).toFixed(0)}s\n`);
  }
  const sha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  const file = path.join(OUT, "terrain.json");
  fs.writeFileSync(file, JSON.stringify({
    webSha: sha, combos: COMBOS, simCombos: SIM_COMBOS, paintCombo: PAINT_COMBO, simSkills: SIM_SKILLS,
    simFrames: SIM_FRAMES, saveAt: SAVE_AT, afterFrames: AFTER_FRAMES, block: BLOCK, decalEvery: DECAL_EVERY, texEvery: TEX_EVERY,
    levels: rows,
  }).replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} levels, ${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
}
main().catch((e) => { console.error(e); process.exit(1); });
