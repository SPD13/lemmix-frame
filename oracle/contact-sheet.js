#!/usr/bin/env node
// contact-sheet.js - the web's VR windows (build/webshots, oracle/webshots.js) beside the port's
// (build/shots/windows, `tools/render.sh build/shots/windows --shot windows /out/windows-scene.png`),
// state by state, web left and port right, on a checkerboard so the transparent corners show.
// Writes build/shots/windows-contact.png.
"use strict";
const fs = require("fs");
const path = require("path");
const puppeteer = require("puppeteer-core");

const ROOT = path.resolve(__dirname, "..");
const WEB = path.join(ROOT, "build", "webshots");
const PORT = path.join(ROOT, "build", "shots", "windows");
const OUT = path.join(ROOT, "build", "shots", "windows-contact.png");
const CHROME = process.env.CHROME || "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";

const uri = (file) => (fs.existsSync(file) ? "data:image/png;base64," + fs.readFileSync(file).toString("base64") : "");
const names = fs.readdirSync(WEB).filter((f) => f.endsWith(".png")).map((f) => f.slice(0, -4)).sort();
const icons = names.filter((n) => n.startsWith("icon-") || n.startsWith("volume-"));
const rest = names.filter((n) => !icons.includes(n));
const pair = (n, scale) => {
  const w = uri(path.join(WEB, n + ".png")), p = uri(path.join(PORT, n + ".png"));
  const img = (src) => (src ? `<img src="${src}" style="zoom:${scale}">` : `<div class="missing">missing</div>`);
  return `<figure><figcaption>${n}</figcaption><div class="pair">${img(w)}${img(p)}</div></figure>`;
};
const scene = uri(path.join(PORT, "windows-scene.png"));
const html = `<!doctype html><meta charset="utf-8"><style>
  body { margin: 0; padding: 16px; background: #1b1f27; color: #cdd6e4; font: 13px monospace; width: 2200px; }
  h1 { font-size: 18px; margin: 4px 0 12px; } h2 { font-size: 15px; margin: 20px 0 8px; color: #ffd866; }
  .grid { display: flex; flex-wrap: wrap; gap: 14px; }
  figure { margin: 0; padding: 6px; background: #252b36; border-radius: 6px; }
  figcaption { margin-bottom: 4px; }
  .pair { display: flex; gap: 8px; align-items: flex-start; }
  .pair img { background: repeating-conic-gradient(#3a3f4a 0% 25%, #2c313b 0% 50%) 0 0 / 16px 16px; image-rendering: auto; }
  .missing { color: #e07a6a; padding: 20px; }
</style>
<h1>VR windows - web (left, Chrome canvas) | port (right, Godot Canvas2D)</h1>
<h2>icons and the volume slider (x1.5)</h2><div class="grid">${icons.map((n) => pair(n, 1.5)).join("")}</div>
<h2>windows (x1)</h2><div class="grid">${rest.map((n) => pair(n, 1)).join("")}</div>
${scene ? `<h2>the port's windows in the room (catalog open, the beam on mute: slider and tooltip)</h2><img src="${scene}">` : ""}`;

(async () => {
  const browser = await puppeteer.launch({ executablePath: CHROME, headless: "new", args: ["--no-first-run"] });
  try {
    const page = await browser.newPage();
    await page.setViewport({ width: 2232, height: 1000 });
    await page.setContent(html, { waitUntil: "load" });
    await page.screenshot({ path: OUT, fullPage: true });
    console.log("contact sheet: " + names.length + " states -> " + path.relative(ROOT, OUT));
  } finally {
    await browser.close();
  }
})();
