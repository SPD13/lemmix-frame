#!/usr/bin/env node
"use strict";
/**
 * The doors-and-water oracle (web/3d/js/portals.js), over the levels bridge.js samples: the
 * openings buildPortals makes - hatches (ceiling square, hinge, door rows, how far each frame
 * has them open, both flaps), exits (the funnel, the opening found in the artwork), the pieces
 * pulled into a door as a slab - with the edge-smoothing switch off and on, and how much
 * terrain each carves; the pools (waterObjectsFrom, buildPoolGeometry); and the surfaces drawn
 * as a stack of slices (stackedObjectsFrom: the phases, then for three animation steps each
 * slice's frame, cut to the level, and its geometry - square-edged and blended - and place).
 * Hashes only: oracle/out/portals.json.
 *   node oracle/portals.js [--every N]
 */
const fs = require("fs");
const path = require("path");
const env = require("./lib/env");
const { StateHash } = require("./lib/hash");
const S = require("./lib/scene-env");

const G = S.load();
const EXTRA = [
  "LemmingsPlus_All_20201114/Lemmings_Plus_II/Nice/No_Need_To_Think_Too_Hard.nxlv",
  "LemmingsPlus_All_20201114/Lemmings_Plus_III/Dodgy/Experimental_Materials.nxlv",
  "LemmingsPlus_All_20201114/Lemmings_Plus_IV/Insane/World_War_III.nxlv",
  "LemmingsPlus_All_20201114/Lemmings_Plus_Omega_II/Fluffy/Backroute_THIS!.nxlv",
];
// app.js: LEMMING_Z = TERRAIN_DEPTH / 2 - SPRITE_DEPTH / 2, OBJECT_Z = LEMMING_Z - 0.8,
// WAVE_FRONT_Z = OBJECT_DECAL_Z = TERRAIN_DEPTH + 0.25
const LEMMING_Z = G.TERRAIN_DEPTH / 2 - G.SPRITE_DEPTH / 2;
const OBJECT_Z = LEMMING_Z - 0.8;
const WAVE_FRONT_Z = G.TERRAIN_DEPTH + 0.25;

/** app.js lemmixEngine.objectData, verbatim. */
function objectData(level) {
  const T = Lemmings.TriggerTypes;
  const objects = level.objects.map((o, i) => ({ id: o.gadget.effectBase === "WINDOW" ? 1 : 100 + i }));
  const objectImg = {};
  level.objects.forEach((o, i) => {
    const g = o.gadget, r = g.triggerRect;
    const effect = g.effect === "EXIT" || g.effect === "LOCKEXIT" ? T.EXIT_LEVEL
      : g.effect === "WATER" ? T.DROWN : g.effect === "FIRE" ? T.KILL
      : g.effect === "TRAP" || g.effect === "TRAPONCE" ? T.TRAP : T.NO_TRIGGER;
    objectImg[objects[i].id] = {
      trigger_effect_id: effect, trigger_left: r.x0 - g.x, trigger_top: r.y0 - g.y,
      trigger_width: r.x1 - r.x0, trigger_height: r.y1 - r.y0,
    };
  });
  return { objects, objectImg };
}

/** The render-only depth map as buildDepthMap leaves it, as far as a carve can tell: solid or not. */
function depthMapOf(level) {
  const mask = level.getGroundMaskLayer().groundMask;
  const d = new Uint8Array(level.width * level.height);
  for (let i = 0; i < d.length; i++) d[i] = mask[i] ? G.DepthClass.TERRAIN : G.DepthClass.EMPTY;
  return d;
}

function portalsHash(level, smooth) {
  const depthMap = depthMapOf(level);
  const portals = G.buildPortals(level, null, depthMap, OBJECT_Z, smooth);
  const h = new StateHash();
  h.word(portals.length);
  for (const p of portals) {
    h.word(p.index); h.word(p.objectId); h.str(p.shape);
    S.hashGeometry(h, p.geometry);
    S.hashF64(h, p.originX); S.hashF64(h, p.originY); S.hashF64(h, p.sfxX); S.hashF64(h, p.sfxY);
    h.word(p.carved);
    S.hashFrame(h, p.closedFrame);
    if (p.rebuild) { S.hashF64(h, p.rebuild.depth); if (p.rebuild.opening) h.bytes(p.rebuild.opening); else h.word(0x7fffffff); }
    else h.word(0x7fffffff);
    if (!p.hatch) { h.word(0x7fffffff); continue; }
    const k = p.hatch;
    for (const v of [k.leftX, k.rightX, k.y, k.halfWidth, k.depth]) S.hashF64(h, v);
    h.word(k.doorRows.length);
    for (const r of k.doorRows) { h.word(r.y); h.word(r.min); h.word(r.max); }
    h.word(p.openness.length);
    for (const v of p.openness) S.hashF64(h, v);
    for (const sign of [1, -1]) S.hashGeometry(h, G.buildFlapGeometry(p.closedFrame, k.doorRows, k.halfWidth, k.depth, sign));
  }
  // the whole depth map after the carves
  h.bytes(depthMap);
  return h.hex();
}

function waterHash(level) {
  const h = new StateHash();
  const pools = G.waterObjectsFrom(level, null);
  h.word(pools.length);
  for (const p of pools) {
    h.word(p.index); h.num(p.y0, "y0"); h.word(p.colour); S.hashF64(h, p.z0); S.hashF64(h, p.z1);
    h.word(p.runs.length);
    for (const r of p.runs) { h.word(r.x0); h.word(r.x1); h.word(r.floor); }
    S.hashGeometry(h, G.buildPoolGeometry(p.runs, p.y0, p.z0, p.z1));
  }
  return h.hex();
}

function stacksHash(level) {
  const cache = new G.SpriteGeometryCache(new G.SessionResources());
  const h = new StateHash();
  const stacks = G.stackedObjectsFrom(level, null);
  h.word(stacks.length);
  const cut = (stack, frame) => (frame
    ? G.clipFrameToBounds(frame, stack.mapObject.x, stack.mapObject.y, stack.flipY, level.width, level.height) : null);
  for (const stack of stacks) {
    h.word(stack.index); h.word(stack.flipY ? 1 : 0); h.bytes(stack.phases);
    const object = stack.mapObject, g = object.gadget;
    // the mesh as first built, wearing the first frame
    const first = cut(stack, object.animation.frames[0]);
    S.hashFrame(h, first);
    if (first) {
      for (const smooth of [false, true]) {
        const e = smooth ? cache.forFrameBlended(null, first, null) : cache.forFrame(first);
        S.hashGeometry(h, e.geometry.getAttribute("position") ? e.geometry : null);
      }
    }
    // three steps of the animation, each slice its own frame on
    const saved = g.currentFrame, count = g.frameCount || 1;
    for (let t = 0; t < 3; t++) {
      g.currentFrame = (saved + t) % count;
      const shown = [];
      for (let k = 0; k < stack.phases.length; k++) shown.push(cut(stack, G.frameAtPhase(object, t, stack.phases[k])));
      for (let k = 0; k < shown.length; k++) {
        const frame = shown[k];
        S.hashFrame(h, frame);
        if (!frame) continue;
        for (const smooth of [false, true]) {
          const entry = smooth ? cache.forFrameBlended(shown[k - 1] || null, frame, shown[k + 1] || null) : cache.forFrame(frame);
          S.hashGeometry(h, entry.geometry.getAttribute("position") ? entry.geometry : null);
          S.hashF64(h, object.x + frame.offsetX);
          S.hashF64(h, object.y + frame.offsetY + (stack.flipY ? entry.h : 0));
          S.hashF64(h, WAVE_FRONT_Z - (k + 1) * G.SPRITE_DEPTH);
        }
      }
    }
    g.currentFrame = saved;
  }
  return h.hex();
}

async function main() {
  const args = process.argv.slice(2);
  const every = (() => { const i = args.indexOf("--every"); return i >= 0 ? +args[i + 1] : 18; })();
  const io = env.nodeIO(env.ASSETS);
  const styles = new env.Lemmix.StyleManager(io);
  await env.Lemmix.loadMasks(io);
  const t0 = Date.now();
  const rows = [];
  let nPortals = 0, nStacks = 0, nPools = 0;
  for (const entry of S.sampleLevels(every, EXTRA)) {
    const row = { id: entry.id, url: entry.url };
    try {
      const level = await S.buildLevel(styles, entry);
      window.__lem3dObjectData = objectData(level);
      row.portals = portalsHash(level, false);
      row.portalsSmooth = portalsHash(level, true);
      row.water = waterHash(level);
      row.stacks = stacksHash(level);
      nPortals += G.buildPortals(level, null, depthMapOf(level), OBJECT_Z, false).length;
      nStacks += G.stackedObjectsFrom(level, null).length;
      nPools += G.waterObjectsFrom(level, null).length;
    } catch (e) { row.error = String(e && e.stack || e); }
    rows.push(row);
  }
  const file = path.join(env.OUT, "portals.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: S.webSha(), every, objectZ: OBJECT_Z, waveFrontZ: WAVE_FRONT_Z, levels: rows })
    .replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} levels, ${nPortals} openings, ${nPools} pools, ${nStacks} stacks, ${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
}
main().catch((e) => { console.error(e); process.exit(1); });
