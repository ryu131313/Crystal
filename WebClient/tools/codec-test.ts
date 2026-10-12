// Codec golden check: every test-data/codec/*.json was written by the REAL Shared codec (WebRelay --emit-fixtures).
//   server.*: decode `hex` with src/protocol/server.ts and compare every decoded field with `fields` (whole values).
//   client.*: re-encode `fields` with src/protocol/client.ts and compare the bytes with `hex`.
// Run: npm run codec:test   (regenerate goldens after a Shared change: npm run codec:regen)

import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { decodeServerPacket, decodeServerPacketAsync, isCompressedFrame } from "../src/protocol/server.ts";
import { ClientPackets } from "../src/protocol/client.ts";

const dir = join(import.meta.dirname, "..", "test-data", "codec");
const hexToBytes = (hex: string) => Uint8Array.from(hex.match(/../g) ?? [], (h) => parseInt(h, 16));
const bytesToHex = (b: Uint8Array) => Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("").toUpperCase();

/** bigint -> decimal string (the fixture writes Int64s as strings); everything else as is. */
function norm(v: unknown): unknown {
  if (typeof v === "bigint") return v.toString();
  if (Array.isArray(v)) return v.map(norm);
  if (v && typeof v === "object") return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, norm(x)]));
  return v;
}

/** Fixture Int64 fields are JSON numbers when C# typed them long (Time, ShockTime...); compare numerically. */
function same(a: unknown, b: unknown): boolean {
  if (typeof a === "string" && typeof b === "number") return a === String(b);
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((x, i) => same(x, b[i]));
  if (a && b && typeof a === "object" && typeof b === "object") {
    return Object.entries(a).every(([k, x]) => same(x, (b as Record<string, unknown>)[k]));
  }
  return a === b;
}

let failed = 0, passed = 0;
for (const file of readdirSync(dir).filter((f) => f.endsWith(".json")).sort()) {
  const fx = JSON.parse(readFileSync(join(dir, file), "utf8")) as { name: string; dir: string; hex: string; fields: Record<string, unknown> };
  try {
    if (fx.dir === "server") {
      const frame = hexToBytes(fx.hex);
      const p = isCompressedFrame(frame) ? await decodeServerPacketAsync(frame, true) : decodeServerPacket(frame, true);
      if (!p) throw new Error("not ported (decoded to null)");
      if (p.type !== fx.name) throw new Error(`decoded as ${p.type}`);
      const { type: _t, ...decoded } = norm(p) as Record<string, unknown>;
      for (const [k, v] of Object.entries(decoded)) {
        if (!(k in fx.fields)) throw new Error(`decoded field ${k} is not a C# field`);
        if (!same(v, fx.fields[k])) throw new Error(`${k}: got ${JSON.stringify(v)}, want ${JSON.stringify(fx.fields[k])}`);
      }
    } else {
      const encode = (ClientPackets as Record<string, (p: any) => Uint8Array>)[fx.name];
      if (!encode) throw new Error("not ported");
      // Int64/UInt64 fields are bigint in the TS encoders (the fixture writes numbers or decimal strings).
      const INT64 = new Set(["Time", "BirthDate", "UniqueID", "IDFrom", "IDTo", "ItemIndex", "ChatItemID", "AuctionID", "FromUniqueID", "ToUniqueID", "MailID"]); // client packets only (C.BuyItem.ItemIndex is a ulong)
      const f = Object.fromEntries(Object.entries(fx.fields).map(([k, v]) => [k, INT64.has(k) ? BigInt(v as string | number) : v]));
      const arg = fx.name === "KeepAlive" ? f.Time : f;
      const got = bytesToHex(encode(arg));
      if (got !== fx.hex) throw new Error(`bytes\n   got  ${got}\n   want ${fx.hex}`);
    }
    passed++;
  } catch (e) {
    failed++;
    console.log(`FAIL ${file}: ${(e as Error).message}`);
  }
}
console.log(`codec-test: ${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
