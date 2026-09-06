// WPF's built-in decoder needs JPEG/PNG. Convert the existing four scenes without redesigning them.
import {createRequire} from 'node:module';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
import fs from 'node:fs/promises';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const sharp=createRequire(path.join(root,'ui/package.json'))('sharp');
const output=path.join(root,'src/Launcher.Desktop/Assets/GameLoading');
await fs.mkdir(output,{recursive:true});
for(const [id,scene] of Object.entries({m3e:'magic',dc2:'waste',mb:'industry',vw:'skyblock'})){
  await sharp(path.join(root,`ui/public/scenes/${scene}.webp`)).jpeg({quality:92,mozjpeg:true}).toFile(path.join(output,id+'.jpg'));
}
