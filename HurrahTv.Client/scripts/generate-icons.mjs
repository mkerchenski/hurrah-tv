import sharp from 'sharp';
import { readFileSync, writeFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const wwwroot = join(__dirname, '..', 'wwwroot');
const svgPath = join(wwwroot, 'icon.svg');
const svgBuffer = readFileSync(svgPath);

const sizes = [
  { name: 'favicon-32.png', size: 32 },
  { name: 'icon-192.png', size: 192 },
  { name: 'icon-512.png', size: 512 },
  { name: 'apple-touch-icon.png', size: 180 },
];

for (const { name, size } of sizes) {
  await sharp(svgBuffer)
    .resize(size, size)
    .png()
    .toFile(join(wwwroot, name));
  console.log(`Generated ${name} (${size}x${size})`);
}

// Generate favicon.png (32x32) as the main favicon
await sharp(svgBuffer)
  .resize(32, 32)
  .png()
  .toFile(join(wwwroot, 'favicon.png'));
console.log('Generated favicon.png (32x32)');

// Generate ICO file (16x16 + 32x32 + 48x48)
// ICO is just a container with multiple PNGs — we'll create a simple one
const icoSizes = [16, 32, 48];
const pngBuffers = await Promise.all(
  icoSizes.map(s => sharp(svgBuffer).resize(s, s).png().toBuffer())
);

// ICO file format: header + directory entries + image data
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);     // reserved
header.writeUInt16LE(1, 2);     // type: ICO
header.writeUInt16LE(pngBuffers.length, 4);

let offset = 6 + (pngBuffers.length * 16); // header + directory entries
const dirEntries = [];
for (let i = 0; i < pngBuffers.length; i++) {
  const entry = Buffer.alloc(16);
  const s = icoSizes[i];
  entry.writeUInt8(s >= 256 ? 0 : s, 0);   // width
  entry.writeUInt8(s >= 256 ? 0 : s, 1);   // height
  entry.writeUInt8(0, 2);                    // color palette
  entry.writeUInt8(0, 3);                    // reserved
  entry.writeUInt16LE(1, 4);                 // color planes
  entry.writeUInt16LE(32, 6);                // bits per pixel
  entry.writeUInt32LE(pngBuffers[i].length, 8);  // image size
  entry.writeUInt32LE(offset, 12);           // image offset
  dirEntries.push(entry);
  offset += pngBuffers[i].length;
}

const ico = Buffer.concat([header, ...dirEntries, ...pngBuffers]);
writeFileSync(join(wwwroot, 'favicon.ico'), ico);
console.log('Generated favicon.ico (16+32+48)');

console.log('All icons generated!');
