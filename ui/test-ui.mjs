import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';

await fs.mkdir('../.local',{recursive:true});
const server = spawn(process.execPath,['node_modules/vite/bin/vite.js','preview','--outDir',process.env.LAUNCHER_WEB_ROOT||'dist','--host','127.0.0.1','--port','18475','--strictPort'],{stdio:'ignore',windowsHide:true});
const url = 'http://127.0.0.1:18475';
async function check(script) {
  const child = spawn(process.execPath,[script],{stdio:'inherit',windowsHide:true,env:{...process.env,LAUNCHER_UI_URL:url}});
  await new Promise((resolve,reject) => {child.on('error',reject);child.on('exit',code=>code===0?resolve():reject(new Error(`${script} exited ${code}`)));});
}
try {
  let ready = false;
  for(let attempt=0;attempt<60;attempt++) {
    if(server.exitCode!==null) throw new Error('UI preview server exited before becoming ready.');
    try {if((await fetch(url)).ok){ready=true;break;}} catch {}
    await new Promise(resolve=>setTimeout(resolve,200));
  }
  if(!ready) throw new Error('UI preview server did not become ready.');
  await check('chrome-check.mjs');
  await check('visual-check.mjs');
  await check('routes-check.mjs');
  await check('tools-check.mjs');
  await check('avatar-check.mjs');
  await check('storage-check.mjs');
  await check('microsoft-check.mjs');
  await check('update-check.mjs');
  await check('instance-check.mjs');
  await check('maintenance-check.mjs');
} finally {server.kill();}
