// Port of Client/MirObjects/MapCode.cs MapReader (map formats 0-7 and 100, picked by signature exactly as `initiate`).
// Cells are stored column-major like the C# CellInfo[x, y] walk, in typed arrays (index = x * height + y).
// C# integer semantics are mirrored where they matter: (short) casts sign-extend into the int image fields, (ushort)
// casts wrap, and the 0x8000 -> 0x20000000 back-tile flag rewrite. A truncated file keeps the cells read so far (the
// C# catches and logs), and a missing file is a blank 1000x1000 map.

export interface MapCells {
  width: number;
  height: number;
  type: number;
  backIndex: Int16Array; backImage: Int32Array;
  middleIndex: Int16Array; middleImage: Int32Array;
  frontIndex: Int16Array; frontImage: Int32Array;
  doorIndex: Uint8Array; doorOffset: Uint8Array;
  frontAnimationFrame: Uint8Array; frontAnimationTick: Uint8Array;
  middleAnimationFrame: Uint8Array; middleAnimationTick: Uint8Array;
  tileAnimationImage: Int16Array; tileAnimationOffset: Int16Array; tileAnimationFrames: Uint8Array;
  light: Uint8Array;
  fishing: Uint8Array;
}

function alloc(width: number, height: number, type: number): MapCells {
  const n = Math.max(0, width) * Math.max(0, height);
  return {
    width, height, type,
    backIndex: new Int16Array(n), backImage: new Int32Array(n),
    middleIndex: new Int16Array(n), middleImage: new Int32Array(n),
    frontIndex: new Int16Array(n), frontImage: new Int32Array(n),
    doorIndex: new Uint8Array(n), doorOffset: new Uint8Array(n),
    frontAnimationFrame: new Uint8Array(n), frontAnimationTick: new Uint8Array(n),
    middleAnimationFrame: new Uint8Array(n), middleAnimationTick: new Uint8Array(n),
    tileAnimationImage: new Int16Array(n), tileAnimationOffset: new Int16Array(n), tileAnimationFrames: new Uint8Array(n),
    light: new Uint8Array(n), fishing: new Uint8Array(n),
  };
}

const s16 = (v: number) => (v << 16) >> 16;
const u16 = (v: number) => v & 0xffff;

/** Which reader MapReader.initiate picks. */
export function detectMapType(b: Uint8Array): number {
  if (b[2] === 0x43 && b[3] === 0x23) return 100;
  if (b[0] === 0) return 5;
  if (b[0] === 0x0f && b[5] === 0x53 && b[14] === 0x33) return 6;
  if (b[0] === 0x15 && b[4] === 0x32 && b[6] === 0x41 && b[19] === 0x31) return 4;
  if (b[0] === 0x10 && b[2] === 0x61 && b[7] === 0x31 && b[14] === 0x31) return 1;
  // C# precedence: (b4 == 0x0F) || ((b4 == 0x03) && (b18 == 0x0D) && (b19 == 0x0A))
  if (b[4] === 0x0f || (b[4] === 0x03 && b[18] === 0x0d && b[19] === 0x0a)) {
    const w = b[0] + (b[1] << 8), h = b[2] + (b[3] << 8);
    return b.length > 52 + w * h * 14 ? 3 : 2;
  }
  if (b[0] === 0x0d && b[1] === 0x4c && b[7] === 0x20 && b[11] === 0x6d) return 7;
  return 0;
}

export function blankMap(): MapCells {
  return alloc(1000, 1000, -1);
}

export function readMap(bytes: Uint8Array): MapCells {
  if (bytes.length < 20) return blankMap();
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const I16 = (o: number) => dv.getInt16(o, true);
  const U16 = (o: number) => dv.getUint16(o, true);
  const I32 = (o: number) => dv.getInt32(o, true);
  const B = (o: number) => {
    if (o >= bytes.length) throw new RangeError("map truncated");
    return bytes[o];
  };
  const type = detectMapType(bytes);
  let m: MapCells = blankMap();

  try {
    switch (type) {
      case 0: case 4: {
        // type 0: header w,h at 0; cells at 52.   type 4 (antihack): xor'd w,h at 31; cells at 64, images xor'd.
        let xor = 0, o: number;
        if (type === 0) { m = alloc(I16(0), I16(2), type); o = 52; }
        else { const w = I16(31); xor = I16(33); const h = I16(35); m = alloc(w ^ xor, h ^ xor, type); o = 64; }
        for (let x = 0; x < m.width; x++) for (let y = 0; y < m.height; y++) {
          const i = x * m.height + y;
          m.backIndex[i] = 0; m.middleIndex[i] = 1;
          let back = s16(I16(o) ^ xor); o += 2;
          m.middleImage[i] = s16(I16(o) ^ xor); o += 2;
          m.frontImage[i] = s16(I16(o) ^ xor); o += 2;
          m.doorIndex[i] = B(o++) & 0x7f;
          m.doorOffset[i] = B(o++);
          m.frontAnimationFrame[i] = B(o++);
          m.frontAnimationTick[i] = B(o++);
          m.frontIndex[i] = B(o++) + 2;
          m.light[i] = B(o++);
          if ((back & 0x8000) !== 0) back = (back & 0x7fff) | 0x20000000;
          m.backImage[i] = back;
          if (m.light[i] >= 100 && m.light[i] <= 119) m.fishing[i] = 1;
        }
        break;
      }
      case 1: case 7: {
        // type 1 (2010): xor'd w,h at 21; type 7 (3/4 heroes): w at 21, h at 25. Cells at 54, 15 bytes.
        let xor = 0;
        if (type === 1) { const w = I16(21); xor = I16(23); const h = I16(25); m = alloc(w ^ xor, h ^ xor, type); }
        else m = alloc(I16(21), I16(25), type);
        let o = 54;
        for (let x = 0; x < m.width; x++) for (let y = 0; y < m.height; y++) {
          const i = x * m.height + y;
          m.backIndex[i] = 0; m.middleIndex[i] = 1;
          let back = type === 1 ? (I32(o) ^ 0xaa38aa38) | 0 : I32(o);
          o += 4;
          m.middleImage[i] = s16(I16(o) ^ xor); o += 2;
          m.frontImage[i] = s16(I16(o) ^ xor); o += 2;
          m.doorIndex[i] = B(o) & 0x7f;
          m.doorOffset[i] = B(++o);
          m.frontAnimationFrame[i] = B(++o);
          m.frontAnimationTick[i] = B(++o);
          let front = B(++o) + 2;
          m.light[i] = B(++o);
          ++o; // Unknown
          o++;
          if (type === 1) {
            if (front === 102) front = 90;
            if (front >= 255) front = -1;
          } else if ((back & 0x8000) !== 0) back = (back & 0x7fff) | 0x20000000;
          m.frontIndex[i] = front;
          m.backImage[i] = back;
          if (m.light[i] >= 100 && m.light[i] <= 119) m.fishing[i] = 1;
        }
        break;
      }
      case 2: case 3: {
        m = alloc(I16(0), I16(2), type);
        let o = 52;
        for (let x = 0; x < m.width; x++) for (let y = 0; y < m.height; y++) {
          const i = x * m.height + y;
          let back = I16(o); o += 2;
          m.middleImage[i] = I16(o); o += 2;
          m.frontImage[i] = I16(o); o += 2;
          m.doorIndex[i] = B(o++) & 0x7f;
          m.doorOffset[i] = B(o++);
          m.frontAnimationFrame[i] = B(o++);
          m.frontAnimationTick[i] = B(o++);
          m.frontIndex[i] = B(o++) + 120;
          m.light[i] = B(o++);
          m.backIndex[i] = B(o++) + 100;
          m.middleIndex[i] = B(o++) + 110;
          if (type === 3) {
            m.tileAnimationImage[i] = I16(o);
            o += 7;
            m.tileAnimationFrames[i] = B(o++);
            m.tileAnimationOffset[i] = I16(o);
            o += 14;
          }
          if ((back & 0x8000) !== 0) back = (back & 0x7fff) | 0x20000000;
          m.backImage[i] = back;
          if (m.light[i] >= 100 && m.light[i] <= 119) m.fishing[i] = 1;
        }
        break;
      }
      case 5: {
        m = alloc(I16(22), I16(24), type);
        let o = 28;
        for (let x = 0; x < (m.width >> 1); x++) for (let y = 0; y < (m.height >> 1); y++) {
          for (let k = 0; k < 4; k++) {
            const i = (x * 2 + (k % 2)) * m.height + (y * 2 + (k >> 1));
            m.backIndex[i] = B(o) !== 255 ? B(o) + 200 : -1;
            m.backImage[i] = U16(o + 1) + 1;
          }
          o += 3;
        }
        o = 28 + 3 * ((m.width >> 1) + (m.width % 2)) * (m.height >> 1);
        for (let x = 0; x < m.width; x++) for (let y = 0; y < m.height; y++) {
          const i = x * m.height + y;
          const flag = B(o++);
          m.middleAnimationFrame[i] = B(o++);
          m.frontAnimationFrame[i] = (B(o) === 255 ? 0 : B(o)) & 0x8f;
          o++;
          m.middleAnimationTick[i] = 0;
          m.frontAnimationTick[i] = 0;
          m.frontIndex[i] = B(o) !== 255 ? B(o) + 200 : -1; o++;
          m.middleIndex[i] = B(o) !== 255 ? B(o) + 200 : -1; o++;
          m.middleImage[i] = u16(U16(o) + 1); o += 2;
          let front = u16(U16(o) + 1);
          if (front === 1 && m.frontIndex[i] === 200) m.frontIndex[i] = -1;
          o += 2;
          o += 3;
          let light = B(o) & 0x0f;
          o += 2;
          if ((flag & 0x01) !== 1) m.backImage[i] |= 0x20000000;
          if ((flag & 0x02) !== 2) front = u16(front | 0x8000);
          m.frontImage[i] = front;
          if (light >= 100 && light <= 119) m.fishing[i] = 1;
          else light = (light * 2) & 0xff;
          m.light[i] = light;
        }
        break;
      }
      case 6: {
        m = alloc(I16(16), I16(18), type);
        let o = 40;
        for (let x = 0; x < m.width; x++) for (let y = 0; y < m.height; y++) {
          const i = x * m.height + y;
          const flag = B(o++);
          m.backIndex[i] = B(o) !== 255 ? B(o) + 300 : -1; o++;
          m.middleIndex[i] = B(o) !== 255 ? B(o) + 300 : -1; o++;
          m.frontIndex[i] = B(o) !== 255 ? B(o) + 300 : -1; o++;
          let back = s16(I16(o) + 1); o += 2;
          m.middleImage[i] = s16(I16(o) + 1); o += 2;
          let front = s16(I16(o) + 1); o += 2;
          if (front === 1 && m.frontIndex[i] === 200) m.frontIndex[i] = -1;
          m.middleAnimationFrame[i] = B(o++);
          let faf = B(o) === 255 ? 0 : B(o);
          if (faf > 0x0f) faf = faf & 0x0f;
          m.frontAnimationFrame[i] = faf;
          o++;
          m.middleAnimationTick[i] = 1;
          m.frontAnimationTick[i] = 1;
          m.light[i] = ((B(o) & 0x0f) * 4) & 0xff;
          o += 8;
          if ((flag & 0x01) !== 1) back |= 0x20000000;
          if ((flag & 0x02) !== 2) front = s16(u16(front) | 0x8000);
          m.backImage[i] = back;
          m.frontImage[i] = front;
        }
        break;
      }
      case 100: {
        if (bytes[0] !== 1 || bytes[1] !== 0) return alloc(0, 0, type);
        m = alloc(I16(4), I16(6), type);
        let o = 8;
        for (let x = 0; x < m.width; x++) for (let y = 0; y < m.height; y++) {
          const i = x * m.height + y;
          m.backIndex[i] = I16(o); o += 2;
          m.backImage[i] = I32(o); o += 4;
          m.middleIndex[i] = I16(o); o += 2;
          m.middleImage[i] = I16(o); o += 2;
          m.frontIndex[i] = I16(o); o += 2;
          m.frontImage[i] = I16(o); o += 2;
          m.doorIndex[i] = B(o++) & 0x7f;
          m.doorOffset[i] = B(o++);
          m.frontAnimationFrame[i] = B(o++);
          m.frontAnimationTick[i] = B(o++);
          m.middleAnimationFrame[i] = B(o++);
          m.middleAnimationTick[i] = B(o++);
          m.tileAnimationImage[i] = I16(o); o += 2;
          m.tileAnimationOffset[i] = I16(o); o += 2;
          m.tileAnimationFrames[i] = B(o++);
          m.light[i] = B(o++);
          if (m.light[i] >= 100 && m.light[i] <= 119) m.fishing[i] = 1;
        }
        break;
      }
    }
  } catch (e) {
    if (!(e instanceof RangeError)) throw e;
    console.warn(`[map] type ${type} truncated; keeping the cells read so far`);
  }
  return m;
}
