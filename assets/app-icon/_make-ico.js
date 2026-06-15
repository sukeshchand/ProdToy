// Build a multi-resolution Windows .ico (PNG-compressed entries) from the
// rendered icon-<size>.png files. No external dependencies.
const fs = require('fs');
const path = require('path');

const dir = __dirname;
const sizes = [16, 24, 32, 48, 64, 128, 256];
const imgs = sizes.map(s => ({ s, data: fs.readFileSync(path.join(dir, `icon-${s}.png`)) }));
const count = imgs.length;

const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);      // reserved
header.writeUInt16LE(1, 2);      // type = icon
header.writeUInt16LE(count, 4);  // image count

let offset = 6 + 16 * count;
const entries = [];
for (const im of imgs) {
  const e = Buffer.alloc(16);
  e.writeUInt8(im.s >= 256 ? 0 : im.s, 0);   // width  (0 = 256)
  e.writeUInt8(im.s >= 256 ? 0 : im.s, 1);   // height (0 = 256)
  e.writeUInt8(0, 2);                         // palette
  e.writeUInt8(0, 3);                         // reserved
  e.writeUInt16LE(1, 4);                      // color planes
  e.writeUInt16LE(32, 6);                     // bits per pixel
  e.writeUInt32LE(im.data.length, 8);         // image data size
  e.writeUInt32LE(offset, 12);                // offset
  offset += im.data.length;
  entries.push(e);
}

const out = Buffer.concat([header, ...entries, ...imgs.map(i => i.data)]);
fs.writeFileSync(path.join(dir, 'ProdToy.ico'), out);
console.log(`Wrote ProdToy.ico — ${out.length} bytes, ${count} sizes: ${sizes.join(', ')}`);
