#!/usr/bin/env node
// pageshots.js - the web's desktop pages, as reference pictures for their in-headset ports
// (app/Ui/Pages): the setup page, the solutions page, the controls dialog, the key-hints drawer
// and the library's search field, each in the states the port's shots render
// (`--shot page-<name>`). Writes build/webshots/pages/<name>.png and texts.json (the visible
// text of each state, for checking the wording), and app/Test/Fixtures/pages.json: the solutions
// page's rows as solutions.js builds them (every Lemmix level, the solutions index) and the texts,
// which the port's tests and shots read.
//
// Needs the web working copy WITH assets served on 8124:
//   cd ../LemmingsJS && npx http-server -p 8124 -c-1 --silent &
//   node oracle/pageshots.js
"use strict";
const fs = require("fs");
const path = require("path");
const puppeteer = require("puppeteer-core");

const ROOT = path.resolve(__dirname, "..");
const OUT = path.join(ROOT, "build", "webshots", "pages");
const CHROME = process.env.CHROME || "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const BASE = process.env.WEB_URL || "http://127.0.0.1:8124/";
const LEVEL = "NeoLemmix_Introduction_Pack/Skills/Amphibious_Engineer_Squad.nxlv";
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function main() {
  fs.mkdirSync(OUT, { recursive: true });
  const browser = await puppeteer.launch({
    executablePath: CHROME, headless: "new",
    args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--no-first-run", "--window-size=1280,800"],
  });
  const texts = {};
  const fixture = {};
  try {
    const page = await browser.newPage();
    page.on("pageerror", (e) => console.log("[page error]", e.message));
    const shot = async (name, opts = {}) => {
      const file = path.join(OUT, name + ".png");
      if (opts.el) {
        const el = await page.$(opts.el);
        await el.screenshot({ path: file });
        texts[name] = await page.$eval(opts.el, (e) => e.innerText);
      } else {
        await page.screenshot({ path: file, fullPage: !!opts.full });
        texts[name] = await page.evaluate(() => document.body.innerText);
      }
      console.log("shot", name);
    };

    // ---- the setup page: what the server's folders hold, then the progress bar, a message,
    // the confirmation
    await page.setViewport({ width: 920, height: 900 });
    await page.goto(BASE + "setup.html?assets=server", { waitUntil: "networkidle0" });
    await wait(800);
    await shot("setup", { full: true });
    await page.evaluate(() => {
      const $ = (id) => document.getElementById(id);
      $("progress").hidden = false;
      $("progress-label").textContent = "unpacking NeoLemmix_V12.14.0.zip — 3.2 MB of 7.0 MB, 412 files";
      $("progress-fill").style.transition = "none";
      $("progress-fill").style.width = "45%";
      for (const b of document.querySelectorAll("button[data-busy]")) b.disabled = true;
      $("msg-progress").textContent = "lemmings-3d-progress.json: 12 levels merged";
      $("msg-progress").className = "msg ok";
      $("msg-prefs").textContent = "notes.json: notes.json is not a JSON file";
      $("msg-prefs").className = "msg err";
    });
    await shot("setup-progress", { full: true });
    await page.evaluate(() => {
      const $ = (id) => document.getElementById(id);
      $("confirm-title").textContent = "Replace NeoLemmix?";
      $("confirm-body").textContent = "The 1489 files installed on 10/2/2026, 3:19:00 PM on the server are removed first.";
      $("confirm-yes").textContent = "replace";
      $("confirm").hidden = false;
    });
    await page.setViewport({ width: 920, height: 600 });
    await shot("setup-confirm");
    await page.setViewport({ width: 920, height: 900 });
    await page.goto(BASE + "setup.html?assets=static", { waitUntil: "networkidle0" });
    await wait(1500);
    await shot("setup-static", { full: true });

    // ---- the solutions page
    await page.setViewport({ width: 1280, height: 800 });
    await page.goto(BASE + "solutions.html?assets=server", { waitUntil: "networkidle0" });
    await page.waitForFunction(() => document.querySelectorAll("#rows tr").length > 0, { timeout: 60000 });
    await wait(300);
    await shot("solutions");
    // the rows' data, as solutions.js builds it, for the port's fixture
    fixture.solutions = await page.evaluate(() => {
      const levels = [];
      for (const [id, rec] of LevelTree.byId) {
        if (rec.node.engine !== "lemmix") continue;
        const where = [];
        for (let n = rec.node; n && n.parent; n = n.parent) where.unshift(n.name);
        levels.push([id, rec.level.title || "", where, rec.pack ? rec.pack.name : where[0] || "", rec.node.levels.indexOf(rec.level) + 1,
          rec.level.lemmings == null ? null : rec.level.lemmings, rec.level.save == null ? null : rec.level.save]);
      }
      const index = {};
      for (const [id, r] of Object.entries(Solutions.index.levels)) {
        index[id] = [r.status, r.tier || 0, r.saved, r.count, r.needed, r.skillsUsed, r.completionFrame, r.elapsedMs];
      }
      return { levels, index, summary: document.getElementById("summary").innerText, note: document.getElementById("note").innerText };
    });
    await page.type("#search", "jst nk");
    await wait(400);
    await shot("solutions-search");
    await page.evaluate(() => { const s = document.getElementById("search"); s.value = ""; s.dispatchEvent(new Event("input")); });
    await page.select("#show", "solved");
    await wait(300);
    await shot("solutions-solved");
    await page.select("#show", "all");
    await page.evaluate(() => document.querySelector("th[data-sort=skills]").click());
    await wait(300);
    await shot("solutions-skills");

    // ---- the game page: the controls dialog, the key hints, the library's search
    await page.setViewport({ width: 1280, height: 800 });
    await page.goto(BASE + "index.html?assets=server", { waitUntil: "load" });
    await page.waitForFunction(() => window.__lem3d && window.__lem3d.hotkeyDialog, { timeout: 90000 });
    await wait(1500);
    await page.evaluate(() => { const s = document.getElementById("lib-search"); s.value = "jst nk"; s.dispatchEvent(new Event("input")); });
    await page.waitForFunction(() => !/scanning/.test(document.getElementById("lib-status").textContent), { timeout: 90000 });
    await wait(800);
    await shot("library-search");
    // a level playing, the key hints' drawer open
    await page.goto(BASE + "index.html?assets=server#" + LEVEL.split("/").map(encodeURIComponent).join("/"), { waitUntil: "load" });
    await page.waitForFunction(() => window.__lem3d && window.__lem3d.session && window.__lem3d.hotkeyDialog, { timeout: 90000 });
    await wait(1500);
    await page.evaluate(() => {
      document.getElementById("hud-keys").hidden = false;
      document.getElementById("hud-fx").hidden = true;
    });
    await wait(200);
    await shot("hints", { el: "#hud-right" });
    await page.evaluate(() => { document.getElementById("hud-keys").hidden = true; });

    const dlg = async (fn, arg) => { await page.evaluate(fn, arg); await wait(200); };
    await dlg(() => { const d = window.__lem3d.hotkeyDialog; d.manager.applyPreset("traditional"); d.manager.applyVrPreset(); d.open("keyboard"); });
    await shot("controls-keyboard", { el: "#hotkeys .hk-dlg" });
    await dlg(() => window.__lem3d.hotkeyDialog.select("Digit1"));
    await shot("controls-skill", { el: "#hotkeys .hk-dlg" });
    await dlg(() => window.__lem3d.hotkeyDialog.select("Space"));
    await shot("controls-frames", { el: "#hotkeys .hk-dlg" });
    await dlg(() => window.__lem3d.hotkeyDialog.select("KeyT"));
    await shot("controls-hold", { el: "#hotkeys .hk-dlg" });
    await dlg(() => window.__lem3d.hotkeyDialog.select("BracketLeft"));
    await shot("controls-special", { el: "#hotkeys .hk-dlg" });
    await dlg(() => { const d = window.__lem3d.hotkeyDialog; d.selected = null; d.dom.unassigned.checked = true; d.showAll = true; d.refresh(); });
    await shot("controls-unassigned", { el: "#hotkeys .hk-dlg" });
    await dlg(() => { const d = window.__lem3d.hotkeyDialog; d.dom.unassigned.checked = false; d.showAll = false; d._setFinding(true); d.refresh(); });
    await shot("controls-find", { el: "#hotkeys .hk-dlg" });
    await dlg(() => { const d = window.__lem3d.hotkeyDialog; d._setFinding(false); d.importText('{"format":"lemmings-3d-controls","version":1,"keys":{"KeyP":{"action":"pause","mod":0},"Nope":{"action":"pause"}}}', "mine.json"); });
    await shot("controls-imported", { el: "#hotkeys .hk-dlg" });
    await dlg(() => { const d = window.__lem3d.hotkeyDialog; d.manager.applyPreset("traditional"); d._status(""); d.showTab("vr"); d.select("VrPointA"); });
    await shot("controls-vr", { el: "#hotkeys .hk-dlg" });
  } finally {
    await browser.close();
  }
  fs.writeFileSync(path.join(OUT, "texts.json"), JSON.stringify(texts, null, 1));
  fixture.texts = { hints: texts.hints };
  // the index's records of the listed levels only
  const listed = new Set(fixture.solutions.levels.map((l) => l[0]));
  for (const id of Object.keys(fixture.solutions.index)) if (!listed.has(id)) delete fixture.solutions.index[id];
  fs.writeFileSync(path.join(ROOT, "app", "Test", "Fixtures", "pages.json"), JSON.stringify(fixture));
}

main().catch((e) => { console.error(e); process.exit(1); });
