// Rebuild checked-in ICO/PNG assets from the editable SVG masters.
// Requires Node.js and sharp. No dependency is needed to build or run the app.
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
const index = args.indexOf('--sharp-module');
const sharp = require(index >= 0 ? path.resolve(args[index + 1]) : 'sharp');
const root = __dirname;
const exportsDir = path.join(root, 'png');
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

function ico(frames) {
  const header = Buffer.alloc(6 + 16 * frames.length);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(frames.length, 4);
  let offset = header.length;
  frames.forEach(({ size, data }, i) => {
    const at = 6 + i * 16;
    header[at] = header[at + 1] = size === 256 ? 0 : size;
    header.writeUInt16LE(1, at + 4);
    header.writeUInt16LE(32, at + 6);
    header.writeUInt32LE(data.length, at + 8);
    header.writeUInt32LE(offset, at + 12);
    offset += data.length;
  });
  return Buffer.concat([header, ...frames.map(frame => frame.data)]);
}

(async () => {
  fs.mkdirSync(exportsDir, { recursive: true });
  const frames = [];
  for (const size of sizes) {
    const input = path.join(root, size === 16 ? 'icon-tiny.svg' : size <= 40 ? 'icon-small.svg' : 'icon.svg');
    const data = await sharp(input, { density: 384 }).resize(size, size).png().toBuffer();
    fs.writeFileSync(path.join(exportsDir, `icon-${size}.png`), data);
    frames.push({ size, data });
  }
  const iconData = ico(frames);
  fs.writeFileSync(path.join(root, '../../src/icon.ico'), iconData);
  // A new resource path prevents Windows from reusing the former Q's EXE icon
  // cache. Keep this filename stable for this F design, change it on redesign.
  fs.writeFileSync(path.join(root, '../../src/framelet-f-62545da8.ico'), iconData);
  await sharp(path.join(root, 'icon.svg'), { density: 192 }).resize(1024, 1024).png().toFile(path.join(root, 'icon-1024.png'));
  console.log(`Generated ${frames.length} explicit ICO sizes and 1024px PNG.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
