#!/usr/bin/env node
// pages-contact.js - the web's desktop pages (build/webshots/pages, oracle/pageshots.js) beside
// their in-headset ports (build/shots/pages, `tools/render.sh build/shots/pages --shot pages
// /out/pages.png`), state by state, web left and port right; then the port's states the web has
// no picture of (the keyboard, the replay files, the popups). Writes build/shots/pages-contact.png.
"use strict";
const fs = require("fs");
const path = require("path");
const puppeteer = require("puppeteer-core");

const ROOT = path.resolve(__dirname, "..");
const WEB = path.join(ROOT, "build", "webshots", "pages");
const PORT = path.join(ROOT, "build", "shots", "pages");
const OUT = path.join(ROOT, "build", "shots", "pages-contact.png");
const CHROME = process.env.CHROME || "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";

const uri = (file) => (fs.existsSync(file) ? "data:image/png;base64," + fs.readFileSync(file).toString("base64") : "");
// the web's state -> the port's (the static setup page, nothing installed, is the port's "empty")
const PORT_OF = { "setup-static": "setup-empty" };
const web = fs.readdirSync(WEB).filter((f) => f.endsWith(".png")).map((f) => f.slice(0, -4)).sort();
const port = fs.readdirSync(PORT).filter((f) => f.startsWith("page-")).map((f) => f.slice(5, -4)).sort();
const paired = new Set(web.map((n) => PORT_OF[n] || n));
const img = (src, w) => (src ? `<img src="${src}" style="width:${w}px">` : `<div class="missing">missing</div>`);
const pair = (n) => {
  const p = PORT_OF[n] || n;
  return `<figure><figcaption>${n}${p !== n ? " / " + p : ""}</figcaption><div class="pair">${img(uri(path.join(WEB, n + ".png")), 900)}${img(uri(path.join(PORT, "page-" + p + ".png")), 900)}</div></figure>`;
};
const alone = (n) => `<figure><figcaption>${n} (the port's own)</figcaption>${img(uri(path.join(PORT, "page-" + n + ".png")), 900)}</figure>`;
const html = `<!doctype html><meta charset="utf-8"><style>
  body { margin: 0; padding: 16px; background: #1b1f27; color: #cdd6e4; font: 14px monospace; width: 1860px; }
  h1 { font-size: 18px; margin: 4px 0 12px; } h2 { font-size: 15px; margin: 20px 0 8px; color: #ffd866; }
  figure { margin: 0 0 14px; padding: 6px; background: #252b36; border-radius: 6px; }
  figcaption { margin-bottom: 4px; }
  .pair { display: flex; gap: 12px; align-items: flex-start; }
  .missing { color: #e07a6a; padding: 20px; width: 900px; }
  .grid { display: flex; flex-wrap: wrap; gap: 12px; }
</style>
<h1>Desktop pages - web (left, Chrome) | in the headset (right, Godot Canvas2D)</h1>
${web.map(pair).join("")}
<h2>the port's states with no web picture</h2><div class="grid">${port.filter((n) => !paired.has(n)).map(alone).join("")}</div>`;

(async () => {
  const browser = await puppeteer.launch({ executablePath: CHROME, headless: "new", args: ["--no-first-run"] });
  try {
    const page = await browser.newPage();
    await page.setViewport({ width: 1892, height: 1000 });
    await page.setContent(html, { waitUntil: "load" });
    await page.screenshot({ path: OUT, fullPage: true });
    console.log("pages contact sheet: " + web.length + " pairs, " + port.length + " port states -> " + path.relative(ROOT, OUT));
  } finally {
    await browser.close();
  }
})();
