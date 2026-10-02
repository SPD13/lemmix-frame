#!/usr/bin/env node
"use strict";
/**
 * The cursor oracle (web/3d/js/cursor.js GameCursor.load): the six composed pictures, 16 px and
 * 32 px, hashed per key - with the assets as they are, with gfx/cursor-hr missing a file (no 32 px
 * twins), and with gfx/cursor missing one (no cursor at all). cursor.js runs on the canvas
 * stand-in of lib/ui-env.js.
 *   node oracle/cursor.js
 * C# twin: core/Lemmix.Core.Tests/Ui/CursorTests.cs
 */
const fs = require("fs");
const path = require("path");
const { WEB, OUT, canvases, missingImages, loadScript } = require("./lib/ui-env");
const { StateHash } = require("./lib/hash");

loadScript("3d/js/cursor.js", ["GameCursor"]);

const SCENARIOS = {
  assets: [],
  "no-hr": ["neolemmix/gfx/cursor-hr/direction_right.png"],
  "no-cursor": ["neolemmix/gfx/cursor/focused.png"],
};

async function main() {
  const out = { webSha: null, scenarios: {} };
  for (const [name, missing] of Object.entries(SCENARIOS)) {
    missingImages.clear(); for (const m of missing) missingImages.add(m);
    canvases.length = 0;
    const cursor = await globalThis.GameCursor.load("");
    const made = canvases.slice();
    const row = { ok: cursor.ok, keys: {} };
    let i = 0;
    for (const kind of ["standard", "focused"]) for (const side of ["", "left", "right"]) {
      if (!cursor.ok) break;
      const key = kind + (side ? "-" + side : "");
      if (globalThis.GameCursor.key(kind === "focused", side === "left" ? -1 : side === "right" ? 1 : 0) !== key) throw new Error("key " + key);
      const c16 = made[i++];
      const c32 = c16.width === 16 && made[i] && made[i].width === 32 ? made[i++] : null;
      if (cursor.canvases[key] !== (c32 || c16)) throw new Error("canvas order for " + key);
      const h = new StateHash();
      for (const c of [c16, c32]) { if (!c) { h.word(0x7fffffff); continue; } h.num(c.width, "w"); h.num(c.height, "h"); h.bytes(c.data); }
      row.keys[key] = h.hex();
    }
    out.scenarios[name] = row;
  }
  missingImages.clear();
  out.webSha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  fs.mkdirSync(OUT, { recursive: true });
  const file = path.join(OUT, "cursor.json");
  fs.writeFileSync(file, JSON.stringify(out, null, 1) + "\n");
  console.log(Object.entries(out.scenarios).map(([k, v]) => `${k}: ok=${v.ok} ${Object.keys(v.keys).length} pictures`).join(", ") + " -> " + path.relative(process.cwd(), file));
}
main().catch((e) => { console.error(e); process.exit(1); });
