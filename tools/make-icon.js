const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

const root = path.resolve(__dirname, '..');
const svg = path.join(root, 'assets', 'dsh-whale.svg');
const output = path.join(root, 'assets', 'dsh-whale.ico');
const sizes = [16, 24, 32, 48, 64, 128, 256];

async function main() {
  const pngs = await Promise.all(sizes.map(size =>
    sharp(svg, { density: 600 })
      .resize(size, size, { fit: 'contain' })
      .png()
      .toBuffer()
  ));
  const header = Buffer.alloc(6 + sizes.length * 16);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  sizes.forEach((size, index) => {
    const entry = 6 + index * 16;
    header[entry] = size === 256 ? 0 : size;
    header[entry + 1] = size === 256 ? 0 : size;
    header[entry + 2] = 0;
    header[entry + 3] = 0;
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(pngs[index].length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += pngs[index].length;
  });
  fs.writeFileSync(output, Buffer.concat([header, ...pngs]));
}

main().catch(error => { console.error(error); process.exit(1); });
