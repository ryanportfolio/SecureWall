// ICO and BMP writers. Input pixels are straight (non-premultiplied) RGBA, top-down rows.

// 32-bit BGRA DIB for an ICO entry: BITMAPINFOHEADER with doubled height,
// bottom-up XOR bitmap, then a 1 bpp AND mask (1 = transparent) padded to 32 bits per row.
function icoDib(w, h, rgba) {
  const maskStride = Math.ceil(w / 32) * 4;
  const buf = Buffer.alloc(40 + w * h * 4 + maskStride * h);
  buf.writeUInt32LE(40, 0);
  buf.writeInt32LE(w, 4);
  buf.writeInt32LE(h * 2, 8);
  buf.writeUInt16LE(1, 12);
  buf.writeUInt16LE(32, 14);
  buf.writeUInt32LE(0, 16); // BI_RGB
  buf.writeUInt32LE(w * h * 4 + maskStride * h, 20);
  let p = 40;
  for (let y = h - 1; y >= 0; y--) {
    for (let x = 0; x < w; x++) {
      const i = (y * w + x) * 4;
      buf[p++] = rgba[i + 2];
      buf[p++] = rgba[i + 1];
      buf[p++] = rgba[i];
      buf[p++] = rgba[i + 3];
    }
  }
  for (let y = h - 1; y >= 0; y--) {
    const row = p;
    for (let x = 0; x < w; x++) {
      if (rgba[(y * w + x) * 4 + 3] === 0) buf[row + (x >> 3)] |= 0x80 >> (x & 7);
    }
    p += maskStride;
  }
  return buf;
}

// entries: [{ w, h, rgba, png }]. Entries of 256 px are stored PNG-compressed, the rest as DIBs.
export function buildIco(entries) {
  entries = [...entries].sort((a, b) => a.w - b.w);
  const images = entries.map((e) => (e.w >= 256 ? e.png : icoDib(e.w, e.h, e.rgba)));
  const head = Buffer.alloc(6 + 16 * entries.length);
  head.writeUInt16LE(0, 0);
  head.writeUInt16LE(1, 2); // type 1 = icon
  head.writeUInt16LE(entries.length, 4);
  let offset = head.length;
  entries.forEach((e, k) => {
    const d = 6 + 16 * k;
    head[d] = e.w >= 256 ? 0 : e.w;
    head[d + 1] = e.h >= 256 ? 0 : e.h;
    head[d + 2] = 0; // palette colors
    head[d + 3] = 0;
    head.writeUInt16LE(1, d + 4); // planes
    head.writeUInt16LE(32, d + 6); // bit count
    head.writeUInt32LE(images[k].length, d + 8);
    head.writeUInt32LE(offset, d + 12);
    offset += images[k].length;
  });
  return Buffer.concat([head, ...images]);
}

// 24-bit bottom-up BMP (BITMAPFILEHEADER + BITMAPINFOHEADER), as WiX UI bitmaps expect.
// Alpha is composited over white.
export function buildBmp24(w, h, rgba) {
  const stride = Math.ceil((w * 3) / 4) * 4;
  const size = 54 + stride * h;
  const buf = Buffer.alloc(size);
  buf.write('BM', 0, 'ascii');
  buf.writeUInt32LE(size, 2);
  buf.writeUInt32LE(54, 10);
  buf.writeUInt32LE(40, 14);
  buf.writeInt32LE(w, 18);
  buf.writeInt32LE(h, 22);
  buf.writeUInt16LE(1, 26);
  buf.writeUInt16LE(24, 28);
  buf.writeUInt32LE(0, 30);
  buf.writeUInt32LE(stride * h, 34);
  buf.writeInt32LE(3780, 38); // 96 dpi
  buf.writeInt32LE(3780, 42);
  for (let y = 0; y < h; y++) {
    let p = 54 + (h - 1 - y) * stride;
    for (let x = 0; x < w; x++) {
      const i = (y * w + x) * 4;
      const a = rgba[i + 3] / 255;
      const over = (c) => Math.round(c * a + 255 * (1 - a));
      buf[p++] = over(rgba[i + 2]);
      buf[p++] = over(rgba[i + 1]);
      buf[p++] = over(rgba[i]);
    }
  }
  return buf;
}
