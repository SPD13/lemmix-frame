"use strict";
/**
 * StateHash: the 64-bit hash both sides compute (C# twin: Lemmix.Core Oracle/StateHash.cs).
 * Everything is fed as 32-bit words; two lanes, FNV-1a style with different primes, the
 * second with a xorshift. Not cryptographic - it only has to make two diverging runs differ.
 *   int / bool        one word (a bool is 0/1); a non-integer number is an error (the port
 *                     keeps integers where the JS has them, so a fraction must be seen)
 *   null / undefined  0x7fffffff
 *   string            its length, then each UTF-16 code unit
 *   bytes             byte length, then 4-byte little-endian words, the tail zero-padded
 */
const NULL_WORD = 0x7fffffff;

class StateHash {
  constructor() { this.h1 = 0x811c9dc5; this.h2 = 0x9747b28c; this.nonInteger = null; }
  word(w) {
    this.h1 = Math.imul(this.h1 ^ w, 0x01000193) >>> 0;
    let h2 = Math.imul(this.h2 ^ w, 0x5bd1e995);
    this.h2 = (h2 ^ (h2 >>> 13)) >>> 0;
  }
  num(v, where) {
    if (v === null || v === undefined) return this.word(NULL_WORD);
    if (typeof v === "boolean") return this.word(v ? 1 : 0);
    if (!Number.isInteger(v)) { if (!this.nonInteger) this.nonInteger = where + "=" + v; return this.word(Math.floor(v) | 0); }
    this.word(v | 0);
  }
  str(s) {
    if (s === null || s === undefined) return this.word(NULL_WORD);
    this.word(s.length);
    for (let i = 0; i < s.length; i++) this.word(s.charCodeAt(i));
  }
  any(v, where) {
    if (typeof v === "string") return this.str(v);
    if (Array.isArray(v)) { this.word(v.length); v.forEach((x, i) => this.any(x, where + "[" + i + "]")); return; }
    if (v !== null && typeof v === "object") throw new Error("object value at " + where);
    this.num(v, where);
  }
  bytes(view) {
    const u8 = new Uint8Array(view.buffer, view.byteOffset, view.byteLength);
    this.word(u8.length);
    const n4 = u8.length >> 2;
    const dv = new DataView(u8.buffer, u8.byteOffset, u8.byteLength);
    for (let i = 0; i < n4; i++) this.word(dv.getUint32(i * 4, true));
    const rest = u8.length & 3;
    if (rest) { let w = 0; for (let j = 0; j < rest; j++) w |= u8[n4 * 4 + j] << (8 * j); this.word(w >>> 0); }
  }
  hex() { return this.h1.toString(16).padStart(8, "0") + this.h2.toString(16).padStart(8, "0"); }
}

module.exports = { StateHash, NULL_WORD };
