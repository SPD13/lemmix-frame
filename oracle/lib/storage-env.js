"use strict";
/**
 * A browser just deep enough for the web's settings code to run in Node: a Map-backed
 * localStorage (the Storage contract: strings in, strings or null out), a fake DOM whose
 * elements keep their text, children and listeners (what setup.js says, what library.js
 * draws), a Blob/URL pair that catches what a page offers as a download, and a fetch that
 * serves the files given to it. Then the modules themselves: hotkeys.js, config-store.js,
 * library.js (its top-level consts returned from the script), audio.js's GameAudio and,
 * fresh on every call, setup.js.
 */
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { WEB } = require("./env");

class MemStorage {
  constructor() { this.m = new Map(); }
  getItem(k) { k = String(k); return this.m.has(k) ? this.m.get(k) : null; }
  setItem(k, v) { this.m.set(String(k), String(v)); }
  removeItem(k) { this.m.delete(String(k)); }
  clear() { this.m.clear(); }
  key(i) { return Array.from(this.m.keys())[i] ?? null; }
  get length() { return this.m.size; }
}

class FakeEl {
  constructor(tag, id) {
    this.tagName = String(tag || "div").toUpperCase();
    this.id = id || "";
    this.children = [];
    this.listeners = {};
    this.textContent = "";
    this.className = "";
    this.hidden = false;
    this.disabled = false;
    this.dataset = {};
    this.style = {};
    this.title = "";
    this.value = "";
    this.parentNode = null;
    this._html = "";
    this.classList = { add() {}, remove() {}, toggle() {}, contains() { return false; } };
  }
  set innerHTML(v) { this.children = []; this._html = v; }
  get innerHTML() { return this._html; }
  appendChild(c) { this.children.push(c); c.parentNode = this; return c; }
  insertBefore(c) { this.children.unshift(c); c.parentNode = this; return c; }
  remove() {}
  addEventListener(t, f) { (this.listeners[t] = this.listeners[t] || []).push(f); }
  removeEventListener() {}
  setAttribute() {}
  removeAttribute() {}
  getAttribute() { return null; }
  querySelector() { return null; }
  querySelectorAll() { return []; }
  focus() {}
  blur() {}
  scrollIntoView() {}
  click() { for (const f of this.listeners.click || []) f({ target: this, preventDefault() {}, stopPropagation() {} }); }
}

const byId = new Map();
const downloads = []; // {name, text}: what the page offered to save
let lastBlobText = null;
const files = new Map(); // url -> text, for fetch

function install() {
  const g = globalThis;
  g.window = g;
  g.localStorage = new MemStorage();
  g.document = {
    getElementById(id) {
      if (!byId.has(id)) { const e = new FakeEl("div", id); e.parentNode = new FakeEl("div"); byId.set(id, e); }
      return byId.get(id);
    },
    createElement(tag) {
      const e = new FakeEl(tag);
      if (e.tagName === "A") e.click = function () { downloads.push({ name: this.download, text: lastBlobText }); };
      return e;
    },
    createTextNode(t) { return { textContent: t }; },
    body: new FakeEl("body"),
    head: new FakeEl("head"),
    addEventListener() {},
    removeEventListener() {},
    querySelectorAll() { return []; },
    visibilityState: "visible",
  };
  g.addEventListener = () => {};
  g.removeEventListener = () => {};
  g.matchMedia = () => ({ matches: false });
  g.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
  g.Blob = class { constructor(parts) { this._text = parts.join(""); } };
  URL.createObjectURL = (b) => { lastBlobText = b._text; return "blob:oracle"; };
  URL.revokeObjectURL = () => {};
  g.fetch = async (url) => {
    const key = String(url).split("?")[0];
    if (!files.has(key)) return { ok: false, status: 404, json: async () => { throw new Error("404"); }, text: async () => "" };
    const text = files.get(key);
    return { ok: true, status: 200, json: async () => JSON.parse(text), text: async () => text };
  };
}
install();

const run = (file, tail) => vm.runInThisContext(fs.readFileSync(path.join(WEB, "3d", "js", file), "utf8") + (tail || ""), { filename: file });

const Hotkeys = require(path.join(WEB, "3d", "js", "hotkeys.js"));
globalThis.Hotkeys = Hotkeys;
run("config-store.js");
const ConfigStore = globalThis.ConfigStore;
const Library = run("library.js", "\n;({ LevelTree, LevelProgress, RecentLevels, FavoriteLevels, Solutions, WorldLibrary, fuzzyScore, worldName });");
const GameAudio = run("audio.js", "\n;GameAudio;");

/** setup.js evaluated afresh (its main() fails at once on the missing Vfs, quietly): window.__setup. */
function loadSetup() {
  run("setup.js");
  return globalThis.__setup;
}
// setup.js's main() fails on the missing Vfs, and says so on the console: that one is not news
const consoleError = console.error;
console.error = (...a) => { if (a[0] instanceof ReferenceError && /Vfs is not defined/.test(a[0].message)) return; consoleError(...a); };

module.exports = { MemStorage, FakeEl, byId, downloads, files, Hotkeys, ConfigStore, Library, GameAudio, loadSetup };
