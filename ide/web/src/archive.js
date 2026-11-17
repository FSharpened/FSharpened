// A small ZIP reader and writer for projects (ADR-0021). Supported: stored and deflated entries, UTF-8 names. Refused,
// with a reason and never a crash: encryption, zip64, other compression methods, and anything that would expand past
// the limits below (a "zip bomb" is just a small file that claims to hold a large one).
// Pure functions over bytes, so it runs the same in the page and in Node tests.

export const LIMITS = Object.freeze({ entries: 2000, entryBytes: 5 * 1024 * 1024, totalBytes: 24 * 1024 * 1024, ratio: 200 });

const encoder = new TextEncoder();

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

export function crc32(bytes) {
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function dosDateTime(date) {
  const y = Math.max(1980, date.getFullYear());
  return {
    time: (date.getHours() << 11) | (date.getMinutes() << 5) | (date.getSeconds() >> 1),
    day: ((y - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate(),
  };
}

/** files: [{ path, data: Uint8Array }] -> Uint8Array. Entries are stored (not compressed): source files are small and a stored archive opens everywhere. */
export function buildZip(files, date = new Date()) {
  const { time, day } = dosDateTime(date);
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const f of files) {
    const name = encoder.encode(f.path);
    const crc = crc32(f.data);
    const local = new Uint8Array(30 + name.length);
    const lv = new DataView(local.buffer);
    lv.setUint32(0, 0x04034b50, true);
    lv.setUint16(4, 20, true);
    lv.setUint16(6, 0x0800, true); // bit 11: the name is UTF-8
    lv.setUint16(8, 0, true); // stored
    lv.setUint16(10, time, true);
    lv.setUint16(12, day, true);
    lv.setUint32(14, crc, true);
    lv.setUint32(18, f.data.length, true);
    lv.setUint32(22, f.data.length, true);
    lv.setUint16(26, name.length, true);
    lv.setUint16(28, 0, true);
    local.set(name, 30);

    const central = new Uint8Array(46 + name.length);
    const cv = new DataView(central.buffer);
    cv.setUint32(0, 0x02014b50, true);
    cv.setUint16(4, 20, true);
    cv.setUint16(6, 20, true);
    cv.setUint16(8, 0x0800, true);
    cv.setUint16(10, 0, true);
    cv.setUint16(12, time, true);
    cv.setUint16(14, day, true);
    cv.setUint32(16, crc, true);
    cv.setUint32(20, f.data.length, true);
    cv.setUint32(24, f.data.length, true);
    cv.setUint16(28, name.length, true);
    cv.setUint32(42, offset, true);
    central.set(name, 46);

    locals.push(local, f.data);
    centrals.push(central);
    offset += local.length + f.data.length;
  }
  const centralSize = centrals.reduce((n, c) => n + c.length, 0);
  const end = new Uint8Array(22);
  const ev = new DataView(end.buffer);
  ev.setUint32(0, 0x06054b50, true);
  ev.setUint16(8, files.length, true);
  ev.setUint16(10, files.length, true);
  ev.setUint32(12, centralSize, true);
  ev.setUint32(16, offset, true);

  const out = new Uint8Array(offset + centralSize + 22);
  let at = 0;
  for (const part of [...locals, ...centrals, end]) {
    out.set(part, at);
    at += part.length;
  }
  return out;
}

async function inflate(compressed, expected) {
  const stream = new Blob([compressed]).stream().pipeThrough(new DecompressionStream("deflate-raw"));
  const reader = stream.getReader();
  const chunks = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.length;
    if (total > expected) {
      await reader.cancel();
      throw new Error("expands past its declared size");
    }
    chunks.push(value);
  }
  const out = new Uint8Array(total);
  let at = 0;
  for (const c of chunks) {
    out.set(c, at);
    at += c.length;
  }
  return out;
}

/**
 * bytes -> { files: [{ path, data }], skipped: [{ path, reason }] }. Throws only when the bytes are not a ZIP at all or
 * hold more than the limits allow; a single bad entry is skipped and reported. `wanted(path)`, when given, picks the
 * entries to read: the others are passed over without being decompressed or counted against the limits.
 */
export async function readZip(bytes, limits = LIMITS, wanted = null) {
  if (!(bytes instanceof Uint8Array)) bytes = new Uint8Array(bytes);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let end = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 22 - 65535); i--) {
    if (view.getUint32(i, true) === 0x06054b50) {
      end = i;
      break;
    }
  }
  if (end < 0) throw new Error("this is not a ZIP archive");
  const count = view.getUint16(end + 10, true);
  const centralAt = view.getUint32(end + 16, true);
  if (count === 0xffff || centralAt === 0xffffffff) throw new Error("zip64 archives are not supported");
  if (count > limits.entries) throw new Error(`the archive has ${count} entries; the limit is ${limits.entries}`);

  const files = [];
  const skipped = [];
  let total = 0;
  let p = centralAt;
  const decoder = new TextDecoder("utf-8");
  for (let n = 0; n < count; n++) {
    if (p + 46 > bytes.length || view.getUint32(p, true) !== 0x02014b50) throw new Error("the archive's directory is damaged");
    const flags = view.getUint16(p + 8, true);
    const method = view.getUint16(p + 10, true);
    const crc = view.getUint32(p + 16, true);
    const csize = view.getUint32(p + 20, true);
    const usize = view.getUint32(p + 24, true);
    const nameLen = view.getUint16(p + 28, true);
    const extraLen = view.getUint16(p + 30, true);
    const commentLen = view.getUint16(p + 32, true);
    const localAt = view.getUint32(p + 42, true);
    const path = decoder.decode(bytes.subarray(p + 46, p + 46 + nameLen));
    p += 46 + nameLen + extraLen + commentLen;

    if (path.endsWith("/")) continue; // a folder entry; folders come from file paths
    if (wanted && !wanted(path)) continue; // an entry the caller does not need is not even inflated
    const skip = (reason) => skipped.push({ path, reason });
    if (flags & 1) { skip("encrypted"); continue; }
    if (csize === 0xffffffff || usize === 0xffffffff) { skip("zip64 entry"); continue; }
    if (method !== 0 && method !== 8) { skip(`unsupported compression method ${method}`); continue; }
    if (usize > limits.entryBytes) { skip(`larger than ${limits.entryBytes} bytes`); continue; }
    if (usize > csize * limits.ratio + 1024) { skip("compression ratio is implausible"); continue; }
    total += usize;
    if (total > limits.totalBytes) throw new Error(`the archive expands to more than ${limits.totalBytes} bytes`);
    if (localAt + 30 > bytes.length || view.getUint32(localAt, true) !== 0x04034b50) { skip("damaged local header"); continue; }
    const dataAt = localAt + 30 + view.getUint16(localAt + 26, true) + view.getUint16(localAt + 28, true);
    if (dataAt + csize > bytes.length) { skip("truncated"); continue; }
    const raw = bytes.subarray(dataAt, dataAt + csize);
    let data;
    try {
      data = method === 0 ? raw.slice() : await inflate(raw, usize);
    } catch (e) {
      skip("could not be decompressed: " + e.message);
      continue;
    }
    if (data.length !== usize || crc32(data) !== crc) { skip("failed its checksum"); continue; }
    files.push({ path, data });
  }
  return { files, skipped };
}

/** UTF-8 text, or null when the bytes are binary (a NUL byte or invalid UTF-8). A leading byte-order mark is dropped. */
export function decodeText(data) {
  if (data.includes(0)) return null;
  try {
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(data).replace(/^﻿/, "");
  } catch {
    return null;
  }
}

export const encodeText = (text) => encoder.encode(text);
