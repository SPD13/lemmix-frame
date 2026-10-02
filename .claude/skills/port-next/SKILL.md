---
name: port-next
description: Take the next "todo" row of parity/matrix.json (lowest phase first), port that web feature into lemmix-frame against its oracle, run the gate, and mark it done. Use when asked to "port the next feature", "continue the port", "next parity row", or to keep the Steam Frame port moving autonomously.
---

# Port the next parity row

The Frame app replicates the web version exactly. `parity/matrix.json` lists every feature with
its web source, its oracle and its phase. One run of this skill moves one row (or a tight group
of rows sharing a source file) from `todo` to `done`.

## Steps

1. **Pick.** Read `parity/matrix.json`; take the lowest-phase row with `status: "todo"` whose
   dependencies are done (a phase-3 render row needs the phase-2 engine). Mark it `wip`.
2. **Read the source.** Open the cited web file (`web/…`) and read the whole function or module,
   not just the cited line. Note every JS-ism from `core/README.md` it contains.
3. **Oracle first.** If the row's oracle type has no dumper yet in `oracle/`, write it: it runs
   the web code in Node (`web/tools/lemmix-node.js` for the engine; `three.min.js` + `vm` for
   render buffers) and writes the reference to `oracle/out/<kind>/…`. Hash files (small) are
   committed; full dumps are not (they derive from copyrighted assets).
4. **Port.** Method for method into `core/Lemmix.Core` (or `app/` for Godot-side rows), following
   `core/README.md`. Add tests in `core/Lemmix.Core.Tests` comparing against the oracle output.
5. **Gate.** `make verify` must be green (Mac + sniper arm64 + app smoke). On a sim mismatch,
   find the first differing frame, dump both states in full and diff them field by field.
6. **Record.** Set the row to `done` with a one-line `notes` (what was ported, which oracle passed,
   anything deferred to a device session). Commit with a message naming the row ids.

## Rules

- Never weaken an oracle to make it pass; a real difference in the JS is ported, quirks included.
- Rows needing the headset (feel, comfort, performance) are done when their Mac test passes and
  are listed in `docs/device-session-1.md` for the user's check.
- Keep the web repo untouched; if the web code itself is wrong, note it in the row and port it as is.
- Disk is tight on this Mac: delete build outputs you created and do not keep full dumps around.
