#!/usr/bin/env node
// boardshots.js - the web version's diorama, shot the way the native app's "board*" shots are
// (app/Test/BoardShot.cs), and contact sheets of the two side by side.
//
// For every preset: the level opened with its switches in the URL (and ?solution=1: the stored
// solution replaying), the game put back to frame 0 and paused, ticked N times by hand, clear
// physics or a hovered lemming with a skill set as the shot does, the camera at frameDesktopCamera's
// framing, the clock frozen at 2500 ms, the skill bar hidden; the scene rendered once and the
// canvas read in the same task. Writes build/webshots-board/<name>.png. Then, for every name with
// a native shot in build/shots/<name>.png (tools/render.sh ... --shot <name>), the sheet
// build/shots/board-contact-<name>.png: web | native at half size, the difference x4, and the
// middle of the board at full size, web | native.
//
// Needs the web working copy WITH assets served on 8124:
//   cd ../LemmingsJS && npx http-server -p 8124 -c-1 --silent &
//   node oracle/boardshots.js [name...]        (SHEETS_ONLY=1: only the sheets)
"use strict";
const fs = require("fs");
const path = require("path");
const { PNG } = require("pngjs");

const ROOT = path.resolve(__dirname, "..");
const OUT = path.join(ROOT, "build", "webshots-board");
const NATIVE = path.join(ROOT, "build", "shots");
const CHROME = process.env.CHROME || "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const BASE = process.env.WEB_URL || "http://127.0.0.1:8124/";
const W = 1280, H = 720, CLOCK = 2500;

const BUILDERS = "Lemmings_Redux/Gentle/Builders_will_help_you_here.nxlv";
const BEAST = "Lemmings_Redux/Gentle/A_Beast_of_a_level.nxlv";
// the same presets as BoardShot.Presets
const PRESETS = {
  "board": { level: BUILDERS, ticks: 420, query: "environment=none" },
  "board-env": { level: BUILDERS, ticks: 420, query: "environment=full" },
  "board-cpm": { level: BUILDERS, ticks: 420, query: "environment=none", cpm: true },
  "board-shadows": { level: BUILDERS, ticks: 300, query: "environment=none", hoverSkill: "BUILDER" },
  "board-plain": { level: BUILDERS, ticks: 420, query: "environment=none&emboss=0&smooth=0&smoothterrain=0&colorblend=off&doors=0" },
  "board-beast": { level: BEAST, ticks: 300, query: "environment=full" },
};

async function shoot(page, name, p) {
  const url = BASE + "?assets=server&solution=1&" + p.query + "&level=" + encodeURIComponent(p.level);
  await page.goto(url, { waitUntil: "load" });
  await page.waitForFunction(() => window.__lem3d && window.__lem3d.session && window.__lem3d.session.game.sim, { timeout: 120000 });
  if (/environment=full/.test(p.query)) {
    await page.waitForFunction(() => window.__lem3d.environment.stats && window.__lem3d.environment.stats.totalMs !== undefined, { timeout: 180000 });
  }
  await new Promise((r) => setTimeout(r, 500));
  const info = await page.evaluate((p, clock) => {
    const L = window.__lem3d, s = L.session, game = s.game, timer = game.getGameTimer();
    game.gotoFrame(0, true);
    for (let i = 0; i < p.ticks; i++) timer.tick();
    if (p.cpm) game.setClearPhysics(true);
    // the camera at frameDesktopCamera's framing, the bar out of the picture, the clock frozen
    const level = s.level;
    L.controls.target.set(level.width / 2, level.height / 2, 8);
    L.camera.position.set(level.width / 2, level.height / 2 + 120, 420);
    L.controls.update();
    L.guiRoot.visible = false;
    if (L.cursor) L.cursor.ok = false; // the page's own ring rather than NeoLemmix's cursor sprite
    performance.now = () => clock;
    let hover = null;
    if (p.hoverSkill) {
      const k = game.sim.activeSkills.indexOf(p.hoverSkill);
      game.getGameSkills().setSelectedSkill(k);
      const lem = game.sim.lemmings.find((L) => !L.removed && L.action === 1 /* WALKING */);
      if (lem) {
        s.worldGroup.updateWorldMatrix(true, false);
        const v = s.worldGroup.localToWorld(new THREE.Vector3(lem.x, lem.y - 5, 7)); // LEMMING_Z
        v.project(L.camera);
        hover = { id: lem.id, x: lem.x, y: lem.y, px: (v.x + 1) / 2 * innerWidth, py: (1 - v.y) / 2 * innerHeight, skill: k };
      }
    }
    return { tick: timer.getGameTicks(), lemmings: s.lemmingPool.activeCount, hover, env: L.environment.stats };
  }, p, CLOCK);
  if (info.hover) await page.mouse.move(info.hover.px, info.hover.py);
  // a few frames for the per-frame work (the ring, the markers, the room's walls)
  for (let i = 0; i < 4; i++) await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => r())));
  const png = await page.evaluate(() => {
    const L = window.__lem3d;
    L.renderer.render(L.dioramaRoot.parent, L.camera);
    return L.renderer.domElement.toDataURL("image/png");
  });
  fs.writeFileSync(path.join(OUT, name + ".png"), Buffer.from(png.split(",")[1], "base64"));
  console.log(name, JSON.stringify({ tick: info.tick, lemmings: info.lemmings, hover: info.hover, env: info.env && info.env.gallery }));
}

function read(file) { return PNG.sync.read(fs.readFileSync(file)); }

// a contact sheet: web | native (half size), the difference x4 | its numbers, the middle at 1:1
function sheet(name) {
  const webFile = path.join(OUT, name + ".png"), natFile = path.join(NATIVE, name + ".png");
  if (!fs.existsSync(webFile) || !fs.existsSync(natFile)) return;
  const a = read(webFile), b = read(natFile);
  const w = Math.min(a.width, b.width), h = Math.min(a.height, b.height);
  const hw = w >> 1, hh = h >> 1;
  const out = new PNG({ width: w, height: hh * 2 + hh });
  out.data.fill(0);
  for (let i = 3; i < out.data.length; i += 4) out.data[i] = 255;
  const px = (img, x, y) => { const i = (y * img.width + x) * 4; return [img.data[i], img.data[i + 1], img.data[i + 2]]; };
  const put = (x, y, c) => { const i = (y * out.width + x) * 4; out.data[i] = c[0]; out.data[i + 1] = c[1]; out.data[i + 2] = c[2]; out.data[i + 3] = 255; };
  const half = (img, ox, oy) => {
    for (let y = 0; y < hh; y++) for (let x = 0; x < hw; x++) {
      const s = [0, 0, 0];
      for (const [dx, dy] of [[0, 0], [1, 0], [0, 1], [1, 1]]) { const c = px(img, 2 * x + dx, 2 * y + dy); s[0] += c[0]; s[1] += c[1]; s[2] += c[2]; }
      put(ox + x, oy + y, s.map((v) => Math.round(v / 4)));
    }
  };
  half(a, 0, 0);
  half(b, hw, 0);
  // the difference: per pixel, the largest channel gap x4
  let sum = 0, over16 = 0;
  for (let y = 0; y < hh; y++) for (let x = 0; x < hw; x++) {
    let m = 0;
    for (const [dx, dy] of [[0, 0], [1, 0], [0, 1], [1, 1]]) {
      const c = px(a, 2 * x + dx, 2 * y + dy), d = px(b, 2 * x + dx, 2 * y + dy);
      const g = Math.max(Math.abs(c[0] - d[0]), Math.abs(c[1] - d[1]), Math.abs(c[2] - d[2]));
      sum += g; if (g > 16) over16++;
      m = Math.max(m, g);
    }
    const v = Math.min(255, m * 4);
    put(x, hh + y, [v, v, v]);
  }
  // the middle of the board at 1:1
  const cx = (w - hw) >> 1, cy = (h - hh) >> 1;
  for (let y = 0; y < hh; y++) for (let x = 0; x < hw / 2; x++) {
    put(hw + x, hh + y, px(a, cx + hw / 4 + x, cy + y));
  }
  for (let y = 0; y < hh; y++) for (let x = 0; x < hw / 2; x++) {
    put(hw + hw / 2 + x, hh + y, px(b, cx + hw / 4 + x, cy + y));
  }
  // and the middle, full width: web over native
  for (let y = 0; y < hh / 2; y++) for (let x = 0; x < w; x++) {
    put(x, 2 * hh + y, px(a, x, cy + hh / 4 + y));
    put(x, 2 * hh + hh / 2 + y, px(b, x, cy + hh / 4 + y));
  }
  const file = path.join(NATIVE, "board-contact-" + name + ".png");
  fs.writeFileSync(file, PNG.sync.write(out));
  console.log("sheet", path.relative(ROOT, file), "mean diff", (sum / (w * h)).toFixed(2), "pixels off by >16:", (100 * over16 / (w * h)).toFixed(2) + "%");
}

async function main() {
  fs.mkdirSync(OUT, { recursive: true });
  const names = process.argv.slice(2).length ? process.argv.slice(2) : Object.keys(PRESETS);
  if (!process.env.SHEETS_ONLY) {
    const puppeteer = require("puppeteer-core");
    const browser = await puppeteer.launch({
      executablePath: CHROME, headless: "new",
      args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--no-first-run", "--window-size=" + W + "," + H],
    });
    try {
      const page = await browser.newPage();
      await page.setViewport({ width: W, height: H, deviceScaleFactor: 1 });
      page.on("pageerror", (e) => console.log("[page error]", e.message));
      if (process.env.VERBOSE) page.on("console", (m) => console.log("[page]", m.text().slice(0, 300)));
      for (const name of names) await shoot(page, name, PRESETS[name]);
    } finally {
      await browser.close();
    }
  }
  for (const name of names) sheet(name);
}

main().catch((e) => { console.error(e); process.exit(1); });
