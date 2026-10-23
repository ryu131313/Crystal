// Crystal wire codec runtime. Lifted from WoOOLcraft src/WebClient/src/protocol/binaryCodec.ts (same .NET BinaryWriter /
// BinaryReader rules), with Crystal's framing and the Point/Color helpers Crystal packets use:
//   little-endian fixed-width ints; bool = 1 byte; string = 7-bit-encoded (LEB128) BYTE length + UTF-8;
//   DateTime = int64 DateTime.ToBinary(); Color = int32 ARGB; Point = two int32.
// Crystal packets hand-write ReadPacket/WritePacket, so each port in server.ts / client.ts mirrors one C# method.

/** lo/mid/hi/flags components of a System.Decimal (exactly what the wire carries). */
export interface WireDecimal {
  lo: number;
  mid: number;
  hi: number;
  flags: number;
}

/** Two-int32 shape of System.Drawing.Point on the wire. */
export interface WirePoint {
  X: number;
  Y: number;
}

/** Two-int32 shape of System.Drawing.Size on the wire. */
export interface WireSize {
  Width: number;
  Height: number;
}

const textEncoder = new TextEncoder();
// BinaryReader throws on malformed UTF-8; fatal:true keeps that loud.
const textDecoder = new TextDecoder("utf-8", { fatal: true });

/** Lone-surrogate parity: the desktop's BinaryWriter uses UTF8Encoding(false, true) and THROWS on lone surrogates, so none arrive from real .NET traffic.
 *  Substituting '?' only makes this encoder lenient TS -> server. Valid pairs encode normally. */
function netUtf8Bytes(s: string): Uint8Array {
  let hasLoneSurrogate = false;
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c < 0xd800 || c > 0xdfff) continue;
    const pairedHigh = c <= 0xdbff && i + 1 < s.length && s.charCodeAt(i + 1) >= 0xdc00 && s.charCodeAt(i + 1) <= 0xdfff;
    if (pairedHigh) {
      i++; // valid surrogate pair — skip the low half
      continue;
    }
    hasLoneSurrogate = true;
    break;
  }
  if (!hasLoneSurrogate) return textEncoder.encode(s);
  let sanitized = "";
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff && i + 1 < s.length) {
      const d = s.charCodeAt(i + 1);
      if (d >= 0xdc00 && d <= 0xdfff) {
        sanitized += s[i] + s[i + 1];
        i++;
        continue;
      }
    }
    sanitized += c >= 0xd800 && c <= 0xdfff ? "?" : s[i];
  }
  return textEncoder.encode(sanitized);
}

/** 7-bit (LEB128) encoded int32 — the string length prefix format. */
function write7BitEncodedInt(sink: (byte: number) => void, value: number): void {
  let v = value >>> 0;
  while (v >= 0x80) {
    sink((v & 0x7f) | 0x80);
    v >>>= 7;
  }
  sink(v);
}

// — Writer —

export class Writer {
  private _buf: Uint8Array;
  private _view: DataView;
  private _len = 0;

  constructor(initialCapacity = 512) {
    const cap = initialCapacity < 64 ? 64 : initialCapacity;
    this._buf = new Uint8Array(cap);
    this._view = new DataView(this._buf.buffer);
  }

  get length(): number {
    return this._len;
  }

  private ensure(n: number): void {
    const needed = this._len + n;
    if (needed <= this._buf.length) return;
    let cap = this._buf.length * 2;
    while (cap < needed) cap *= 2;
    const next = new Uint8Array(cap);
    next.set(this._buf.subarray(0, this._len));
    this._buf = next;
    this._view = new DataView(next.buffer);
  }

  private raw(bytes: Uint8Array): void {
    this.ensure(bytes.length);
    this._buf.set(bytes, this._len);
    this._len += bytes.length;
  }

  writeBool(v: boolean): void {
    this.ensure(1);
    this._view.setUint8(this._len, v ? 1 : 0);
    this._len += 1;
  }

  writeU8(v: number): void {
    this.ensure(1);
    this._view.setUint8(this._len, v & 0xff);
    this._len += 1;
  }

  writeI8(v: number): void {
    this.ensure(1);
    this._view.setInt8(this._len, v);
    this._len += 1;
  }

  writeI16(v: number): void {
    this.ensure(2);
    this._view.setInt16(this._len, v, true);
    this._len += 2;
  }

  writeU16(v: number): void {
    this.ensure(2);
    this._view.setUint16(this._len, v & 0xffff, true);
    this._len += 2;
  }

  writeI32(v: number): void {
    this.ensure(4);
    this._view.setInt32(this._len, v | 0, true);
    this._len += 4;
  }

  writeU32(v: number): void {
    this.ensure(4);
    this._view.setUint32(this._len, v >>> 0, true);
    this._len += 4;
  }

  writeI64(v: bigint): void {
    this.ensure(8);
    this._view.setBigInt64(this._len, BigInt.asIntN(64, v), true);
    this._len += 8;
  }

  writeU64(v: bigint): void {
    this.ensure(8);
    this._view.setBigUint64(this._len, BigInt.asUintN(64, v), true);
    this._len += 8;
  }

  writeF32(v: number): void {
    this.ensure(4);
    this._view.setFloat32(this._len, v, true);
    this._len += 4;
  }

  writeF64(v: number): void {
    this.ensure(8);
    this._view.setFloat64(this._len, v, true);
    this._len += 8;
  }

  /** Single char — UTF-8 encoded, exactly like BinaryWriter.Write(char). */
  writeChar(ch: string): void {
    this.raw(netUtf8Bytes(ch));
  }

  /** LEB128 byte-length prefix + UTF-8 bytes, exactly like BinaryWriter.Write(string). */
  writeString(s: string): void {
    const bytes = netUtf8Bytes(s ?? "");
    write7BitEncodedInt((b) => this.writeU8(b), bytes.length);
    this.raw(bytes);
  }

  /** int32 count + bytes (null byte[] writes count 0), exactly like the runtime. */
  writeBytes(b: Uint8Array | null): void {
    this.writeI32(b ? b.length : 0);
    if (b) this.raw(b);
  }

  writeDecimal(d: WireDecimal): void {
    this.writeI32(d.lo);
    this.writeI32(d.mid);
    this.writeI32(d.hi);
    this.writeI32(d.flags);
  }

  toUint8Array(): Uint8Array {
    return this._buf.slice(0, this._len);
  }
}

// — Reader —

export class Reader {
  private _buf: Uint8Array;
  private _view: DataView;
  private _pos: number;
  private _end: number;

  constructor(data: Uint8Array, start = 0, end = data.length) {
    this._buf = data;
    this._view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    this._pos = start;
    this._end = end;
  }

  get position(): number {
    return this._pos;
  }

  get remaining(): number {
    return this._end - this._pos;
  }

  private take(n: number): number {
    if (this._end - this._pos < n) {
      throw new Error(`wire reader: unexpected end (need ${n} bytes at ${this._pos}, have ${this._end - this._pos})`);
    }
    const at = this._pos;
    this._pos += n;
    return at;
  }

  readBool(): boolean {
    return this._view.getUint8(this.take(1)) !== 0;
  }

  readU8(): number {
    return this._view.getUint8(this.take(1));
  }

  readI8(): number {
    return this._view.getInt8(this.take(1));
  }

  readI16(): number {
    return this._view.getInt16(this.take(2), true);
  }

  readU16(): number {
    return this._view.getUint16(this.take(2), true);
  }

  readI32(): number {
    return this._view.getInt32(this.take(4), true);
  }

  readU32(): number {
    return this._view.getUint32(this.take(4), true);
  }

  readI64(): bigint {
    return this._view.getBigInt64(this.take(8), true);
  }

  readU64(): bigint {
    return this._view.getBigUint64(this.take(8), true);
  }

  readF32(): number {
    return this._view.getFloat32(this.take(4), true);
  }

  readF64(): number {
    return this._view.getFloat64(this.take(8), true);
  }

  readChar(): string {
    // UTF-8 decode of the next single encoded character (1-3 bytes).
    const first = this.readU8();
    if (first < 0x80) return String.fromCharCode(first);
    const extra = first >= 0xf0 ? 3 : first >= 0xe0 ? 2 : 1;
    const bytes = this._buf.subarray(this._pos - 1, this._pos + extra);
    this.take(extra);
    return textDecoder.decode(bytes);
  }

  read7BitEncodedInt(): number {
    let value = 0;
    let shift = 0;
    for (; ; shift += 7) {
      if (shift > 28) throw new Error("wire reader: malformed 7-bit encoded length");
      const b = this.readU8();
      value |= (b & 0x7f) << shift;
      if ((b & 0x80) === 0) break;
    }
    return value >>> 0;
  }

  readString(): string {
    const byteLen = this.read7BitEncodedInt();
    const at = this.take(byteLen);
    return textDecoder.decode(this._buf.subarray(at, at + byteLen));
  }

  /** BinaryReader.ReadBytes(count): exactly count bytes, no length prefix. */
  readFixedBytes(count: number): Uint8Array {
    const at = this.take(count);
    return this._buf.slice(at, at + count);
  }

  readBytes(): Uint8Array {
    const count = this.readI32();
    if (count < 0 || count > this.remaining) {
      throw new Error(`wire reader: byte[] length ${count} exceeds remaining ${this.remaining}`);
    }
    const at = this.take(count);
    return this._buf.slice(at, at + count);
  }

  readDecimal(): WireDecimal {
    const lo = this.readI32();
    const mid = this.readI32();
    const hi = this.readI32();
    const flags = this.readI32();
    return { lo, mid, hi, flags };
  }
}

// — gzip (Functions.CompressBytes / DecompressBytes) —

/** gzip -> bytes with the browser's (or Node's) DecompressionStream. */
export async function gunzip(bytes: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([bytes as BlobPart]).stream().pipeThrough(new DecompressionStream("gzip"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

// — Crystal helpers —

export function readPoint(r: Reader): WirePoint {
  const X = r.readI32();
  const Y = r.readI32();
  return { X, Y };
}

export function writePoint(w: Writer, p: WirePoint): void {
  w.writeI32(p.X);
  w.writeI32(p.Y);
}

// — Framing —
// Crystal frame (Shared/Packet.cs): [UInt16 LE total length incl. header][Int16 LE packet id][payload]. The relay sends
// exactly one frame per WebSocket message, so the browser never reassembles a stream.

export const FRAME_HEADER = 4;

/** Serialise one packet into a complete Crystal frame. */
export function framePacket(id: number, payload: Uint8Array): Uint8Array {
  const total = payload.length + FRAME_HEADER;
  if (total > 0xffff) throw new Error(`packet ${id} is ${total} bytes (> 65535)`);
  const out = new Uint8Array(total);
  const view = new DataView(out.buffer);
  view.setUint16(0, total, true);
  view.setInt16(2, id | 0, true);
  out.set(payload, FRAME_HEADER);
  return out;
}

export interface WireFrame {
  id: number;
  payload: Uint8Array;
}

/** Parse one whole frame (one WebSocket message). */
export function parseFrame(buf: Uint8Array): WireFrame {
  if (buf.length < FRAME_HEADER) throw new Error(`wire frame: ${buf.length} bytes`);
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  const total = view.getUint16(0, true);
  if (total !== buf.length) throw new Error(`wire frame: length prefix ${total} != message ${buf.length}`);
  return { id: view.getInt16(2, true), payload: buf.subarray(FRAME_HEADER) };
}
