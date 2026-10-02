#!/usr/bin/env node
"use strict";
/**
 * The environment oracle (web/3d/js/envgen.js, environment.js): for one level of each of the
 * first themes in the levels index, what the page builds round the board -
 *   - the gallery context (environment.js _galleryContext: every terrain piece of the theme
 *     style, a contact sheet of them), its palette, its pieces and collage mode;
 *   - the runtime pictures: with no pictures made offline (web/3d/env holds none) a gallery is
 *     "fog only" - the sky, floor0, bowl0 and ceiling0 in the haze (build with full: false,
 *     smoothFog);
 *   - the collage, every plane and the standing pieces as build draws them with full: true
 *     (what the page draws once a style has its pictures, and tools/env-gen.js --dry), with
 *     each standing piece's extruded geometry (Environment.pieceGeometry);
 *   - the level's own part: its wallpaper's kind, the prop backdrop, the backdrop and scene
 *     colours;
 *   - the room (roomFor for the level, canonicalRoom for the gallery) and the rings' geometry
 *     as placeDesktop lays them (ringGeometry, drumGeometry, the fog sphere).
 * Plus a hash of Math.sin / cos / exp / hypot over a fixed run of arguments, which the port's
 * V8Math must reproduce bit for bit. Hashes only: oracle/out/envgen.json.
 *   node oracle/envgen.js [--themes N]
 */
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const env = require("./lib/env");
const { StateHash } = require("./lib/hash");
const { Lemmix } = env;
const S = require("./lib/scene-env");

S.load();
for (const name of ["profile-store.js", "envgen.js", "environment.js"]) {
  const file = path.join(env.WEB, "3d", "js", name);
  vm.runInThisContext(fs.readFileSync(file, "utf8"), { filename: file });
}
const EnvGen = global.EnvGen, ProfileStore = global.ProfileStore, ProfileFiles = global.ProfileFiles;
const Environment = vm.runInThisContext("Environment");
const vrSrc = fs.readFileSync(path.join(env.WEB, "3d", "js", "vr.js"), "utf8");
const VR_PIXEL_SCALE = Number(/const VR_PIXEL_SCALE = ([0-9.]+)/.exec(vrSrc)[1]);
const PX_PER_METRE = 1 / VR_PIXEL_SCALE; // app.js: new Environment(..., { pxPerMetre: 1 / VR_PIXEL_SCALE })

/** app.js lemmixEngine.groundData, verbatim. */
function groundData(level) {
  const ids = new Map();
  const terraImages = {};
  const terrains = [];
  for (const p of level.pieces || []) {
    const d = p.drawn;
    if (!ids.has(d.variantKey)) {
      const id = ids.size;
      ids.set(d.variantKey, id);
      const frame = new Uint8Array(d.width * d.height);
      for (let i = 0, a = 3; i < frame.length; i++, a += 4) frame[i] = d.image.data[a] < 128 ? 0x80 : 0;
      terraImages[id] = { width: d.width, height: d.height, frames: [frame], name: d.key };
    }
    terrains.push({
      x: p.x, y: p.y, id: ids.get(d.variantKey), key: d.key,
      drawProperties: { isUpsideDown: false, noOverwrite: p.noOverwrite, onlyOverwrite: false, isErase: p.erase },
    });
  }
  return { lr: { levelWidth: level.width, levelHeight: level.height, terrains, graphicSet1: null, steel: [] }, terraImages };
}

/** The level's merged profile, the files read from web/3d/profiles as the page fetches them. */
async function loadProfile(gd) {
  const files = new ProfileFiles({
    fetch: async (url) => {
      const file = path.join(env.WEB, url);
      if (!fs.existsSync(file)) return { ok: false, status: 404 };
      const text = fs.readFileSync(file, "utf8");
      return { ok: true, json: async () => JSON.parse(text) };
    },
  });
  return files.loadAll(ProfileStore.urlsForGroundData(gd, null));
}

const F = (h, v) => S.hashF64(h, v);
const EXTRA_URLS = [
  "levels/LemmingsPlus_All_20201114/levels/Lemmings_Plus_IV/Smooth/I'll_Be_Back....nxlv",
  "levels/LemmingsPlus_All_20201114/levels/Lemmings_Plus_VI/Loopy/Fun_In_The_Flowers.nxlv",
  "levels/NeoLemmix_Introduction_Pack/Advanced_Training/Revenge_Of_The_Fiddler.nxlv",
  "levels/NeoLemmix_Introduction_Pack/Advanced_Training/Saving_Two_Lems_With_One_Bomb.nxlv",
  "levels/LemmingsPlus_All_20201114/levels/Lemmings_Plus_VI/Loopy/We_All_Fall_Down_2018.nxlv",
  "levels/NeoLemmix_Introduction_Pack/Basic_Training_1/Chalk_Walk.nxlv",
];

function hashBitmap(h, bmp) {
  if (!bmp) { h.word(0x7fffffff); return; }
  h.word(bmp.width); h.word(bmp.height); h.bytes(bmp.data);
}

function roomHash(room) {
  const h = new StateHash();
  for (const v of [room.W, room.H, room.P, room.floorDrop, room.wallPx, room.center.x, room.center.z, room.reach]) F(h, v);
  h.word(room.layers.length);
  for (const l of room.layers) {
    for (const v of [l.i, l.rIn, l.rOut, l.circ, l.d, l.rBand, l.fogBand, l.fog, l.fogNear, l.skyline, l.swell]) F(h, v);
    for (const p of [l.floor, l.ceiling, l.wall]) { h.word(p.k); h.word(p.w); h.word(p.h); }
  }
  h.word(room.sphere.r); h.word(room.sphere.w); h.word(room.sphere.h);
  return h.hex();
}

function paletteHash(p) {
  const h = new StateHash();
  h.word(p.material.length); for (const c of p.material) h.word(c);
  for (const c of [p.bg, p.dark, p.light, p.accent, p.fog]) h.word(c);
  h.num(p.avg, "avg"); h.str(p.source);
  return h.hex();
}

function piecesHash(collected) {
  const h = new StateHash();
  h.word(collected.pieces.length);
  for (const p of collected.pieces) {
    h.str(String(p.key)); h.word(p.w); h.word(p.h); h.word(p.area); h.word(p.opaque); F(h, p.fill);
    h.word(p.bbox.x); h.word(p.bbox.y); h.word(p.bbox.w); h.word(p.bbox.h);
    h.word(p.flatTop ? 1 : 0); h.str(p.cls); h.word(p.steel ? 1 : 0); h.word(p.count); h.word(p.excluded ? 1 : 0);
  }
  h.word(collected.decor.length);
  return h.hex();
}

function layoutHashes(environment, ctx) {
  const room = EnvGen.roomFor(ctx.width, ctx.height, PX_PER_METRE);
  environment.level = { ctx, room, styles: null, geometries: [] };
  environment.placeDesktop();
  const out = {};
  for (const name of Object.keys(environment.planes)) {
    const h = new StateHash();
    S.hashGeometry(h, environment.planes[name].geometry);
    out[name] = h.hex();
  }
  const extra = new StateHash();
  F(extra, environment._yFloor); F(extra, environment._yCeil); F(extra, environment._wallHeight);
  out._heights = extra.hex();
  return { room, out };
}

function mathHash() {
  const h = new StateHash();
  let s = 7;
  const rnd = () => { s = (Math.imul(s, 1103515245) + 12345) >>> 0; return s / 4294967296; };
  for (let i = 0; i < 100000; i++) {
    const x = (rnd() * 2 - 1) * (i % 3 === 0 ? 8 : i % 3 === 1 ? 70 : 1e4);
    const e = -rnd() * 60 * (i % 2 ? 1 : 0.05);
    const a = rnd() * 3000 - 1500, b = rnd() * 3000;
    F(h, Math.sin(x)); F(h, Math.cos(x)); F(h, Math.exp(e)); F(h, Math.hypot(a, b));
  }
  return h.hex();
}

async function main() {
  const args = process.argv.slice(2);
  const nThemes = (() => { const i = args.indexOf("--themes"); return i >= 0 ? +args[i + 1] : 40; })();
  const io = env.nodeIO(env.ASSETS);
  const styles = new env.Lemmix.StyleManager(io);
  await env.Lemmix.loadMasks(io);
  const environment = new Environment(new THREE.Scene(), new THREE.Group(), { pxPerMetre: PX_PER_METRE });
  const t0 = Date.now();
  // one level per theme, the first of each in the index
  const seen = new Set(), entries = [];
  for (const entry of env.listLevels(env.ASSETS)) {
    if (entries.length >= nThemes) break;
    const text = fs.readFileSync(path.join(env.ASSETS, entry.url), "utf8");
    const m = /^\s*THEME\s+(.+?)\s*$/mi.exec(text);
    const theme = (m ? m[1] : "").toLowerCase();
    if (seen.has(theme)) continue;
    seen.add(theme);
    entries.push(entry);
  }
  // and a few whose backgrounds are drawn differently (a prop placed once, a wide or a
  // translucent picture), whatever their theme
  for (const url of EXTRA_URLS) {
    const e = env.listLevels(env.ASSETS).find((l) => l.url === url);
    if (e && !entries.includes(e)) entries.push(e);
  }
  const rows = [];
  for (const entry of entries) {
    const row = { id: entry.id, url: entry.url };
    try {
      const level = await S.buildLevel(styles, entry);
      const gd = groundData(level);
      const profile = await loadProfile(gd);
      // app.js envCtx (the fields the room reads; donors only feed a level's own palette, which the page never asks for)
      const ctx = {
        engine: "lemmix", levelId: entry.id, width: level.width, height: level.height,
        themeName: level.themeName || null, theme: level.theme || null, pack: null,
        background: level.background || null, backgroundName: level.info && level.info.background,
        groundImage: level.groundImage, groundMask: level.groundMask && level.groundMask.groundMask,
        donors: null, groundData: gd, profile, lemmixPieces: level.pieces || null, lemmixObjects: level.objects,
        dosPalette: null,
      };
      row.theme = ctx.themeName;
      row.gallery = environment._galleryKey(ctx);
      const gctx = await environment._galleryContext(ctx, styles);
      row.sheet = (() => { const h = new StateHash(); h.word(gctx.width); h.word(gctx.height); h.bytes(gctx.groundImage); return h.hex(); })();
      const room = EnvGen.canonicalRoom(PX_PER_METRE);
      row.canonicalRoom = roomHash(room);
      const palette = EnvGen.derivePalette(gctx);
      row.palette = paletteHash(palette);
      const wallpaper = await environment._galleryWallpaper(gctx, styles);
      row.galleryWallpaper = wallpaper ? wallpaper.key + ":" + wallpaper.kind : null;
      const opts = { room, palette, wallpaper };
      const collected = EnvGen.collectPieces(gctx);
      row.pieces = piecesHash(collected);
      row.mode = EnvGen.chooseMode(collected.pieces, gctx.profile);
      // the runtime: no pictures made offline, so the fog alone
      row.fog = {};
      let fog = null;
      for (const name of ["sky", "floor0", "bowl0", "ceiling0"]) {
        const ambient = EnvGen.build(gctx, Object.assign({ full: false, smoothFog: true }, opts), [name]);
        fog = ambient.fog;
        const h = new StateHash(); hashBitmap(h, ambient.planes[name]); row.fog[name] = h.hex();
      }
      row.fogColour = fog;
      // the collage, every plane
      row.collage = {};
      for (const name of EnvGen.planeNames(room)) {
        if (name === "backdrop") continue;
        const built = EnvGen.build(gctx, Object.assign({ full: true, pieces: collected }, opts), [name]);
        const h = new StateHash(); hashBitmap(h, built.planes[name]); row.collage[name] = h.hex();
      }
      const builtProps = EnvGen.build(gctx, Object.assign({ full: true, pieces: collected }, opts), ["props"]);
      row.props = (builtProps.props || []).map((p) => {
        const h = new StateHash();
        h.word(p.ring); F(h, p.u); F(h, p.r); F(h, p.w); F(h, p.h); h.word(p.depth); F(h, p.yaw);
        hashBitmap(h, p.bitmap);
        S.hashGeometry(h, Environment.pieceGeometry(p));
        return h.hex();
      });
      // the level's own part
      const wp = await environment._wallpaper(ctx);
      row.wallpaper = wp ? wp.key + ":" + wp.kind : null;
      if (wp && wp.kind === "prop") {
        const prop = EnvGen.propBackdrop(ctx, wp.image, palette.bg);
        const h = new StateHash(); h.word(prop.k); hashBitmap(h, prop.bitmap); row.propBackdrop = h.hex();
      }
      // the paths no sampled level takes on its own, run on its background picture: a prop
      // backdrop, the gallery's sky with a wallpaper across its horizon, and the far wall's
      // picture as a prop and as a wallpaper (drawWallpaper, shrink)
      if (ctx.background && ctx.background.image) {
        const image = ctx.background.image;
        const prop = EnvGen.propBackdrop(ctx, image, palette.bg);
        const h = new StateHash(); h.word(prop.k); hashBitmap(h, prop.bitmap);
        const sky = EnvGen.build(gctx, Object.assign({}, opts, { wallpaper: { image, key: "x", kind: "wallpaper" }, full: false, smoothFog: true }), ["sky"]);
        h.word(sky.fog); hashBitmap(h, sky.planes.sky);
        for (const kind of ["prop", "wallpaper"]) {
          const layer = room.layers[2];
          const bmp = new Lemmix.Bitmap(layer.wall.w, layer.wall.h);
          EnvGen.drawWallpaper(bmp, room, layer, { image, key: "x", kind }, fog);
          hashBitmap(h, bmp);
        }
        row.backgroundPaths = h.hex();
      }
      row.backdropColour = wp && wp.kind === "wallpaper" ? 0x999999 : wp && wp.kind === "prop" ? 0xb0b0b0 : EnvGen.scale(palette.bg, 0.7);
      row.sceneColour = fog !== null ? fog : EnvGen.scale(palette.dark, 0.35);
      // the level's room and the rings laid round it on the desktop
      const layout = layoutHashes(environment, ctx);
      row.room = roomHash(layout.room);
      row.layout = layout.out;
    } catch (e) { row.error = String(e && e.stack || e); }
    rows.push(row);
    process.stdout.write(".");
  }
  process.stdout.write("\n");
  const file = path.join(env.OUT, "envgen.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: S.webSha(), pxPerMetre: PX_PER_METRE, math: mathHash(), levels: rows })
    .replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} levels, ${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
}
main().catch((e) => { console.error(e); process.exit(1); });
