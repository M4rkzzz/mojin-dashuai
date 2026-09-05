import sharp from 'sharp';
import fs from 'node:fs/promises';
import {fileURLToPath} from 'node:url';

const directory = new URL('../src/Launcher.Desktop/Assets/', import.meta.url);
const source = await fs.readFile(new URL('../assets/brand/square-source.png', import.meta.url));
const publicDirectory = new URL('./public/brand/', import.meta.url);
await fs.mkdir(publicDirectory,{recursive:true});
for(const [name,size] of [['logo.png',256],['favicon.png',32],['server-icon.png',64]])
  await sharp(source).resize(size,size).removeAlpha().png({compressionLevel:9}).toFile(fileURLToPath(new URL(name,publicDirectory)));
const sizes = [16,24,32,48,64,128,256];
const frames = await Promise.all(sizes.map(size => sharp(source).resize(size,size).png().toBuffer()));
const header = Buffer.alloc(6+16*sizes.length);
header.writeUInt16LE(1,2);
header.writeUInt16LE(sizes.length,4);
let offset = header.length;
frames.forEach((frame,index) => {
  const at = 6+16*index;
  header[at] = header[at+1] = sizes[index]===256 ? 0 : sizes[index];
  header.writeUInt16LE(1,at+4);
  header.writeUInt16LE(32,at+6);
  header.writeUInt32LE(frame.length,at+8);
  header.writeUInt32LE(offset,at+12);
  offset += frame.length;
});
await fs.writeFile(new URL('launcher.ico',directory),Buffer.concat([header,...frames]));
