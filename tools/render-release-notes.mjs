#!/usr/bin/env node
// Deterministic typography and existing game art; no image service or network required.
import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const args=process.argv.slice(2);
if(args.includes('--help')){
  console.log('node tools/render-release-notes.mjs [--data packs/changelogs/beta16.json] [--out artifacts/changelogs/beta16] [--scale 2]');
  process.exit(0);
}
const options={data:'packs/changelogs/beta16.json',scale:'2'};
for(let i=0;i<args.length;i+=2){
  const key=args[i]?.replace(/^--/,'');
  if(!['data','out','scale'].includes(key)||!args[i+1])throw new Error('Unknown or missing option. Use --help.');
  options[key]=args[i+1];
}
const resolve=value=>path.resolve(root,value);
const data=JSON.parse((await fs.readFile(resolve(options.data),'utf8')).replace(/^\uFEFF/,''));
const scale=Number(options.scale);
if(!Number.isFinite(scale)||scale<1||scale>3)throw new Error('--scale must be between 1 and 3.');
if(!Array.isArray(data.changes)||data.changes.length===0)throw new Error('Provide at least one changelog entry.');
if(data.changes.some(item=>typeof item!=='string'||!item.trim()))throw new Error('Each change must be a non-empty line of text.');
if(data.notes!==undefined&&(!Array.isArray(data.notes)||data.notes.some(item=>typeof item!=='string')))throw new Error('notes must be an array of text lines.');
if(!/^#[a-f0-9]{6}$/i.test(data.accent))throw new Error('accent must be a six-digit hex color.');
const output=resolve(options.out||'artifacts/changelogs/'+path.basename(options.data,'.json'));
const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
async function asset(name){
  const file=resolve(name),mime={'.png':'image/png','.webp':'image/webp','.jpg':'image/jpeg','.jpeg':'image/jpeg'}[path.extname(file).toLowerCase()];
  if(!mime)throw new Error('Use a local PNG, WebP or JPEG asset: '+name);
  return `data:${mime};base64,${(await fs.readFile(file)).toString('base64')}`;
}
const tokens={
  TITLE:escape(data.brand+' '+data.version+' 更新日志'),ACCENT:data.accent,BACKGROUND:await asset(data.background),LOGO:await asset(data.logo),
  BRAND:escape(data.brand),PRODUCT:escape(data.product),DATE:escape(data.date),HEADING:escape(data.title||data.version+' 更新日志'),VERSION:escape(data.version),
  CHANGES:data.changes.map(item=>`<li><span>${escape(item)}</span></li>`).join(''),
  NOTES:data.notes?.length?`<div class="notes">${data.notes.map(item=>`<p>${escape(item)}</p>`).join('')}</div>`:'',
  COMMUNITY:escape(data.community),FOOTER:escape(data.footer),
};
const template=await fs.readFile(path.join(root,'tools/templates/release-notes.html'),'utf8');
const html=template.replace(/\{\{([A-Z_]+)\}\}/g,(_,name)=>{if(!(name in tokens))throw new Error('Unknown template token '+name);return tokens[name];});
await fs.mkdir(path.dirname(output),{recursive:true});
await fs.writeFile(output+'.html',html,'utf8');
const require=createRequire(path.join(root,'ui/package.json'));
const {chromium}=require('@playwright/test');
const sharp=require('sharp');
const browser=await chromium.launch({headless:true});
try{
  const page=await browser.newPage({viewport:{width:1080,height:900},deviceScaleFactor:scale});
  await page.route(/^https?:\/\//,route=>route.abort());
  await page.setContent(html,{waitUntil:'load'});
  await page.evaluate(async()=>{await document.fonts.ready;await Promise.all([...document.images].map(image=>image.decode()));});
  const problems=await page.evaluate(()=>[...document.querySelectorAll('h1,p,.changes li,.version,.brand-row,.footer')].filter(el=>el.scrollWidth>el.clientWidth+1).map(el=>el.className||el.tagName));
  if(problems.length)throw new Error('Text exceeds its layout; shorten the copy: '+problems.join(', '));
  await page.locator('.poster').screenshot({path:output+'.png'});
  await sharp(output+'.png').resize({width:1080}).jpeg({quality:92,mozjpeg:true}).toFile(output+'.jpg');
  const metadata=await sharp(output+'.png').metadata();
  const report={version:data.version,input:path.relative(root,resolve(options.data)),width:metadata.width,height:metadata.height,scale,files:[output+'.png',output+'.jpg',output+'.html']};
  await fs.writeFile(output+'.json',JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify(report,null,2));
}finally{await browser.close();}
