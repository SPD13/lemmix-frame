"use strict";
/**
 * The diorama's objects and surroundings under node, for the bridge / portals / envgen /
 * markers oracles: three.js (web/3d/lib/three.min.js, UMD) as the global THREE, then
 * web/3d/js/{depth,bridge,portals}.js run as the page runs them - plain scripts in one global
 * scope - over the smallest stand-ins for what they touch of the page (window,
 * Lemmings.TriggerTypes / Level, TERRAIN_DEPTH from terrain.js). Nothing is rendered: the
 * oracles only read the buffers and pixels the scripts build.
 */
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const env = require("./env");

const JS3D = path.join(env.WEB, "3d", "js");

let G = null;
function load() {
  if (G) return G;
  global.THREE = require(path.join(env.WEB, "3d", "lib", "three.min.js"));
  global.window = global;
  // what portals.js reads of the DOS engine: the trigger ids, and the Level it hooks
  global.Lemmings = {
    TriggerTypes: { NO_TRIGGER: 0, EXIT_LEVEL: 1, UNKNOWN_2: 2, UNKNOWN_3: 3, TRAP: 4, DROWN: 5, KILL: 6 },
    Level: function Level() {},
  };
  global.Lemmings.Level.prototype.setMapObjects = function () {};
  // terrain.js is a renderer; only its slab depth is read here, taken from the source
  const terrainSrc = fs.readFileSync(path.join(JS3D, "terrain.js"), "utf8");
  const m = /const TERRAIN_DEPTH = (\d+)/.exec(terrainSrc);
  if (!m) throw new Error("TERRAIN_DEPTH not found in terrain.js");
  const run = (file) => vm.runInThisContext(fs.readFileSync(path.join(JS3D, file), "utf8"), { filename: file });
  run("depth.js");
  vm.runInThisContext("const TERRAIN_DEPTH = " + m[1] + ";");
  run("bridge.js");
  run("portals.js");
  const names = [
    "SPRITE_DEPTH", "TERRAIN_DEPTH", "DepthClass", "DEPTH_BANDS",
    "buildExtrudedSpriteGeometry", "spriteBodyParts", "buildBlendedSpriteGeometry", "buildBlendedFrameRgba",
    "FRAME_BLEND_SCALE", "SpriteGeometryCache", "SessionResources", "clipFrameToBounds", "SpriteCapture",
    "portalConfigFor", "waveSliceCount", "waveRandom", "isStackedSurface", "waveFrameCount", "wavePhases",
    "stackedObjectsFrom", "frameAtPhase", "waterObjectsFrom", "poolRuns", "buildPoolGeometry", "averageFrameColour",
    "hatchOpenness", "buildFlapGeometry", "spriteOpeningRows", "buildCeilingGeometry", "isSkyColour", "isDarkColour",
    "maskPatches", "openMask", "patchShape", "nearestPatch", "spriteOpeningMask", "buildPortalGeometry",
    "carveTerrainForPortal", "isPortalCapCandidate", "portalObjectRect", "buildPortals", "WATER_OPACITY",
  ];
  G = {};
  for (const n of names) G[n] = vm.runInThisContext(n);
  return G;
}

/** A frame built by level.js's node stand-in has no getBuffer; the page's Lemmings.Frame does. */
function withBuffer(frame) {
  if (frame && !frame.getBuffer) frame.getBuffer = function () { return this.data; };
  return frame;
}

/** Every frame of every object of a level given getBuffer (and the gadget's later renders too). */
function patchLevelFrames(level) {
  for (const o of level.objects) {
    for (const f of o.animation.frames) withBuffer(f);
    const g = o.gadget;
    if (g && !g.__patched) {
      g.__patched = true;
      const render = g.render.bind(g);
      g.render = () => withBuffer(render());
    }
  }
}

/** A double as its two 32-bit words (little-endian), so fractions hash exactly. */
const f64 = new Float64Array(1), u32 = new Uint32Array(f64.buffer);
function hashF64(h, v) {
  if (v === null || v === undefined) { h.word(0x7fffffff); return; }
  f64[0] = v; h.word(u32[0]); h.word(u32[1]);
}

/**
 * A BufferGeometry: each attribute's Float32Array bytes (position, color, uv, absent ones as
 * the null word), then the index - its element width (2 or 4) and its bytes.
 */
function hashGeometry(h, geom) {
  if (!geom) { h.word(0x7fffffff); return; }
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

/** A frame's own data: size, offsets, ABGR words, mask. */
function hashFrame(h, f) {
  if (!f) { h.word(0x7fffffff); return; }
  h.word(f.width); h.word(f.height); h.word(f.offsetX | 0); h.word(f.offsetY | 0);
  if (f.data instanceof Uint32Array) h.bytes(f.data);
  else h.word(0x7fffffff);
  h.bytes(f.getMask());
}

/** The levels the 3D oracles sample: every `every`th of the index, plus the extra ids given. */
function sampleLevels(every, extra) {
  const all = env.listLevels(env.ASSETS);
  const out = all.filter((_, i) => i % every === 0);
  for (const id of extra || []) {
    const e = all.find((l) => l.id === id);
    if (e && !out.includes(e)) out.push(e);
  }
  return out;
}

async function buildLevel(styles, entry) {
  const data = env.Lemmix.LevelBuilder.parseLevel(fs.readFileSync(path.join(env.ASSETS, entry.url), "utf8"));
  const level = await env.Lemmix.LevelBuilder.build(data, styles, { seed: entry.id });
  patchLevelFrames(level);
  return level;
}

function webSha() {
  return require("child_process").execSync("git rev-parse --short HEAD", { cwd: env.WEB }).toString().trim();
}

module.exports = { load, withBuffer, patchLevelFrames, hashF64, hashGeometry, hashFrame, sampleLevels, buildLevel, webSha };
