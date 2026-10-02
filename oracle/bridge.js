#!/usr/bin/env node
"use strict";
/**
 * The sprite-geometry oracle (web/3d/js/bridge.js): for every gadget animation frame of a
 * sample of levels, every frame of the default lemming sprites and every gfx/mask picture,
 * what SpriteGeometryCache builds - the extruded relief (forFrame / forMask: geometry and
 * texture), the one-colour cut-out (flatMaterialFor), the colour blend baked into a texture
 * at both strengths (blendedMaterialFor), the body/loose split and the rounded, blended slice
 * between its neighbours (forFrameBlended, the "edge smoothing" switch) - plus each object
 * drawn through a SpriteCapture cut to the level (clipFrameToBounds) and placed as
 * BillboardPool places it, and a ParticleCloud sync. Hashes only (oracle/lib/hash.js; float
 * buffers by their Float32Array bytes): oracle/out/bridge.json.
 *   node oracle/bridge.js [--every N]
 */
const fs = require("fs");
const path = require("path");
const env = require("./lib/env");
const { StateHash } = require("./lib/hash");
const S = require("./lib/scene-env");

const G = S.load();
const SOFTNESS = [0.5, 1];
// a few levels with a stretch of fire (lava, acid) on top of the sample: the sample has water
const EXTRA = [
  "LemmingsPlus_All_20201114/Lemmings_Plus_II/Nice/No_Need_To_Think_Too_Hard.nxlv",
  "LemmingsPlus_All_20201114/Lemmings_Plus_III/Dodgy/Experimental_Materials.nxlv",
  "LemmingsPlus_All_20201114/Lemmings_Plus_IV/Insane/World_War_III.nxlv",
  "LemmingsPlus_All_20201114/Lemmings_Plus_Omega_II/Fluffy/Backroute_THIS!.nxlv",
];

function hashEntry(h, entry) {
  h.word(entry.w); h.word(entry.h);
  S.hashGeometry(h, entry.geometry.getAttribute("position") ? entry.geometry : null);
  const tex = entry.material.map;
  h.word(tex.image.width); h.word(tex.image.height); h.bytes(tex.image.data);
}

/** Everything the cache builds from one frame, in its own hash. */
function frameHashes(cache, frames, k) {
  const f = frames[k];
  const a = new StateHash();
  hashEntry(a, cache.forFrame(f));
  const flat = cache.flatMaterialFor(f).map.image;
  a.bytes(flat.data);
  const b = new StateHash();
  for (const s of SOFTNESS) {
    cache.setColorBlend(s);
    const tex = cache.blendedMaterialFor(f).map.image;
    b.word(tex.width); b.word(tex.height); b.bytes(tex.data);
  }
  cache.setColorBlend(0);
  const c = new StateHash();
  const parts = G.spriteBodyParts(f.getMask(), f.width, f.height);
  if (!parts) c.word(0x7fffffff);
  else {
    c.bytes(parts.body); c.word(parts.loose ? 1 : 0); if (parts.loose) c.bytes(parts.loose);
    c.word(parts.parts); c.word(parts.looseCount);
  }
  // the slice between its neighbours, and alone (the front and back of a stack)
  for (const [p, n] of [[frames[k - 1] || null, frames[k + 1] || null], [null, null], [frames[k + 1] || null, null]]) {
    const e = cache.forFrameBlended(p, f, n);
    S.hashGeometry(c, e.geometry.getAttribute("position") ? e.geometry : null);
  }
  return a.hex() + b.hex() + c.hex();
}

/** objectManager.render through a SpriteCapture cut to the level, placed as BillboardPool.sync places. */
function captureHash(level, cache) {
  const cap = new G.SpriteCapture();
  cap.setBounds(level.width, level.height);
  cap.begin();
  for (const obj of level.objects) {
    const g = obj.gadget;
    if (g.effectBase === "NONE" && g.effect === "NONE" && !g.animations.length) continue;
    cap.drawFrameFlags(g.render(), obj.x, obj.y, obj.drawProperties);
  }
  const h = new StateHash();
  h.word(cap.items.length);
  const zFor = (layer) => (layer < -1 ? -1.4 : layer < 0 ? 5.9 : layer > 0 ? 16.25 : 6.2);
  cap.items.forEach((item, i) => {
    h.word(item.off ? 1 : 0); h.num(item.layer, "layer"); h.word(item.oneWay ? 1 : 0); h.word(item.flipY ? 1 : 0);
    S.hashFrame(h, item.frame);
    if (item.off) return;
    const entry = cache.forFrame(item.frame);
    const src = item.frame;
    S.hashF64(h, item.x + src.offsetX);
    S.hashF64(h, item.y + src.offsetY + (item.flipY ? entry.h : 0));
    S.hashF64(h, zFor(item.layer) + i * 0.02);
  });
  return h.hex();
}

/** Frames of the default lemming sprites, cut as sprites.js cuts them (scheme.nxmi FRAMES). */
async function lemmingAnims(io) {
  const set = await new env.Lemmix.SpriteSet(io).load("default");
  const out = [];
  for (const name of Object.keys(set.anims).sort()) {
    const a = set.anims[name];
    const frames = [];
    for (const side of [a.right, a.left]) for (const bmp of side.frames) frames.push(S.withBuffer(env.Lemmix.LevelBuilder.frameFromBitmap(bmp, 0, 0)));
    out.push({ name, frameCount: a.frameCount, frames });
  }
  return out;
}

/** A gfx/mask picture as a Mask stand-in: a 1-bit stencil, set where the picture is opaque. */
function maskOf(bmp) {
  const bits = new Int8Array(bmp.width * bmp.height);
  for (let i = 0; i < bits.length; i++) bits[i] = bmp.data[i * 4 + 3] !== 0 ? 1 : 0;
  return { width: bmp.width, height: bmp.height, offsetX: 0, offsetY: 0, getMask() { return bits; } };
}

function particlesHash() {
  const flat = [];
  let s = 12345;
  const next = () => { s = (Math.imul(s, 1103515245) + 12345) >>> 0; return s; };
  for (let i = 0; i < 500; i++) flat.push(next() % 2000 - 300, next() % 900 - 100, next() & 255, next() & 255, next() & 255);
  // ParticleCloud.sync's two buffers, without the scene it adds them to
  const count = flat.length / 5, z = 8.0;
  const pos = new Float32Array(count * 3), col = new Float32Array(count * 3);
  for (let i = 0; i < count; i++) {
    pos[i * 3] = flat[i * 5]; pos[i * 3 + 1] = flat[i * 5 + 1]; pos[i * 3 + 2] = z;
    col[i * 3] = flat[i * 5 + 2] / 255; col[i * 3 + 1] = flat[i * 5 + 3] / 255; col[i * 3 + 2] = flat[i * 5 + 4] / 255;
  }
  const h = new StateHash(); h.bytes(pos); h.bytes(col);
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
  for (const entry of S.sampleLevels(every, EXTRA)) {
    const row = { id: entry.id, url: entry.url };
    try {
      const level = await S.buildLevel(styles, entry);
      const cache = new G.SpriteGeometryCache(new G.SessionResources());
      row.objects = level.objects.map((o) => {
        const frames = o.animation.frames;
        return frames.map((_, k) => frameHashes(cache, frames, k)).join(",");
      });
      row.capture = captureHash(level, cache);
    } catch (e) { row.error = String(e && e.stack || e); }
    rows.push(row);
  }
  const cache = new G.SpriteGeometryCache(new G.SessionResources());
  const lemmings = (await lemmingAnims(io)).map((a) => ({
    name: a.name, frameCount: a.frameCount,
    hashes: a.frames.map((_, k) => frameHashes(cache, a.frames, k)),
  }));
  const masks = [];
  const maskDir = path.join(env.ASSETS, "neolemmix", "gfx", "mask");
  for (const file of fs.readdirSync(maskDir).filter((f) => f.endsWith(".png")).sort()) {
    const bmp = await io.image("neolemmix/gfx/mask/" + file);
    const mask = maskOf(bmp);
    const h = new StateHash();
    hashEntry(h, cache.forMask(mask));
    // cut to a box over its middle, as a draw half off the level is
    const cut = G.clipFrameToBounds(mask, -(mask.width >> 2), -(mask.height >> 2), false, mask.width, mask.height);
    if (!cut) h.word(0x7fffffff);
    else { h.word(cut.width); h.word(cut.height); h.word(cut.offsetX); h.word(cut.offsetY); h.bytes(cut.getMask()); hashEntry(h, cache.forMask(cut)); }
    masks.push({ file, hash: h.hex() });
  }
  fs.mkdirSync(env.OUT, { recursive: true });
  const file = path.join(env.OUT, "bridge.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: S.webSha(), every, softness: SOFTNESS, particles: particlesHash(), lemmings, masks, levels: rows })
    .replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  const nObj = rows.reduce((a, r) => a + (r.objects || []).length, 0);
  console.log(`${rows.length} levels, ${nObj} objects, ${lemmings.length} lemming anims, ${masks.length} masks, ${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
}
main().catch((e) => { console.error(e); process.exit(1); });
