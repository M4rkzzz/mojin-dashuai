import {chromium,expect} from '@playwright/test';
import fs from 'node:fs';

const browser=await chromium.launch({headless:true});
try{
 const page=await browser.newPage({viewport:{width:1280,height:820},reducedMotion:'reduce'});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{
  const listeners=[];window.updateCommands=[];window.restartBlocked=true;
  const emit=data=>listeners.forEach(callback=>callback({data}));
  window.emitUpdate=data=>emit({event:'launcher-update',data});
  window.chrome={webview:{addEventListener:(_,callback)=>listeners.push(callback),postMessage:r=>queueMicrotask(()=>{
   window.updateCommands.push(r.command);let result=null;
   if(r.command==='bootstrap')result={profile:{id:'fixture',gameName:'Player',loginName:'Player',kind:'hub'},installs:{},settings:{root:'C:\\Games',contentDirectoryConfigured:true,memory:{m3e:8192,dc2:8192,mb:8736},java:{},jvm:{},width:1280,height:720,concurrency:4,limitMiB:0,selectedRoutes:{},reducedMotion:true,theme:'dark'}};
   if(r.command==='launcher.update.status')result={phase:'current'};
   if(r.command==='launcher.update.check'){result={phase:'checking'};window.emitUpdate(result);}
   if(r.command==='launcher.update.restart'&&window.restartBlocked)return emit({id:r.id,ok:false,error:'请先结束游戏、下载或登录任务。'});
   emit({id:r.id,ok:true,result});
  })}};
 });
 await page.goto(process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18475');
 await page.getByRole('button',{name:'启动器设置'}).click();
 const panel=page.locator('.launcher-update');
 await expect(panel.getByRole('status')).toHaveText('暂无更新');
 await panel.getByRole('button',{name:'检查更新'}).click();
 await expect(panel.getByRole('button',{name:'检查更新'})).toBeDisabled();
 await page.evaluate(()=>window.emitUpdate({phase:'downloading',downloaded:25,total:100,version:'0.2.0-beta.1'}));
 await expect(panel.getByRole('status')).toHaveText('正在下载 25%');
 await page.evaluate(()=>window.emitUpdate({phase:'ready',version:'0.2.0-beta.1'}));
 await panel.getByRole('button',{name:'重启更新'}).click();
 await expect(panel.getByRole('alert')).toHaveText('请先结束游戏、下载或登录任务。');
 await expect(panel.getByRole('button',{name:'重启更新'})).toBeEnabled();
 await page.evaluate(()=>{window.restartBlocked=false;});
 await panel.getByRole('button',{name:'重启更新'}).click();
 await expect(panel.getByRole('alert')).toHaveCount(0);
 await page.evaluate(()=>window.emitUpdate({phase:'failed',error:'启动器文件校验失败。'}));
 await expect(panel.getByRole('button',{name:'重启更新'})).toHaveCount(0);
 await expect(panel.getByRole('alert')).toHaveText('启动器文件校验失败。');
 await expect(panel.getByRole('button',{name:'检查更新'})).toBeEnabled();
 expect(errors).toEqual([]);
 fs.writeFileSync('../.local/launcher-update-ui-check.json',JSON.stringify({passed:true,progress:true,restartGuard:true,retryAfterError:true,errors},null,2));
 console.log('Launcher update UI passed: progress, readiness, restart guard and failure recovery.');
}finally{await browser.close();}
