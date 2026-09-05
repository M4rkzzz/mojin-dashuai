import {chromium,expect} from '@playwright/test';
import fs from 'node:fs';

const browser=await chromium.launch({headless:true});
try{
 const page=await browser.newPage({viewport:{width:1280,height:820},reducedMotion:'reduce'});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{
  const listeners=[];window.updateCommands=[];window.restartBlocked=true;window.updateStatusReplies=[];
  const emit=data=>listeners.forEach(callback=>callback({data}));
  window.emitUpdate=data=>emit({event:'launcher-update',data});
  window.chrome={webview:{addEventListener:(_,callback)=>listeners.push(callback),postMessage:r=>queueMicrotask(()=>{
   window.updateCommands.push(r.command);let result=null;
   if(r.command==='bootstrap')result={launcherVersion:'0.1.2-beta.13',profile:{id:'fixture',gameName:'Player',loginName:'Player',kind:'hub'},installs:{},settings:{root:'C:\\Games',contentDirectoryConfigured:true,memory:{m3e:8192,dc2:8192,mb:8736,vw:4096},java:{},jvm:{},width:1280,height:720,concurrency:4,limitMiB:0,selectedRoutes:{},reducedMotion:true,theme:'dark'}};
   if(r.command==='bootstrap'&&location.search.includes('signedOut'))result.profile=null;
   if(r.command==='launcher.update.status'&&location.search.includes('signedOut')){window.updateStatusReplies.push(()=>emit({id:r.id,ok:true,result:{phase:'current'}}));return;}
   if(r.command==='launcher.update.status')result={phase:'current'};
   if(r.command==='launcher.update.check'){result={phase:'checking'};window.emitUpdate(result);}
   if(r.command==='launcher.update.restart'&&window.restartBlocked)return emit({id:r.id,ok:false,error:'请先结束游戏、下载或登录任务。'});
   emit({id:r.id,ok:true,result});
  })}};
 });
 const url=process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18475';
 for(const width of [1280,820]){
  await page.setViewportSize({width,height:820});
  await page.goto(url+'?signedOut');
  await expect(page.getByRole('button',{name:'进入大厅',exact:true})).toBeEnabled();
  await page.getByRole('button',{name:'QQ群 1105114550',exact:true}).click();
  expect(await page.evaluate(()=>window.updateCommands.includes('community.join'))).toBe(true);
  const notice=page.locator('.window-update');
  await notice.click();
  await expect(page.getByRole('dialog',{name:'启动器更新'}).getByRole('status')).toHaveText('正在检查');
  expect(await page.evaluate(()=>window.updateCommands.filter(c=>c==='launcher.update.check').length)).toBe(1);
  await page.keyboard.press('Escape');
  await expect(notice).toBeVisible();await expect(notice).toHaveText('检查更新');
  await page.evaluate(()=>window.emitUpdate({phase:'failed',version:'0.1.2-beta.12',error:'下载超时，请重试。'}));
  await expect(notice).toHaveText('检查更新');
  await page.mouse.move(0,0);
  await expect(notice).toHaveCSS('color','rgb(146, 214, 162)');
  await expect(notice).toHaveCSS('-webkit-app-region','no-drag');
  const button=await notice.boundingBox(),minimize=await page.getByRole('button',{name:'最小化',exact:true}).boundingBox();
  expect(minimize.x-button.x-button.width).toBeGreaterThanOrEqual(0);expect(minimize.x-button.x-button.width).toBeLessThanOrEqual(8);await expect(notice).toHaveCSS('border-top-style','none');
  await notice.click();
  const popover=page.getByRole('dialog',{name:'启动器更新'});
  await expect(popover.getByRole('alert')).toHaveText('下载超时，请重试。');
  await page.evaluate(()=>window.updateStatusReplies.forEach(reply=>reply()));
  await expect(notice).toBeVisible(); // A late initial snapshot cannot erase a newer native event.
  await popover.getByRole('button',{name:'检查更新'}).click();
  await expect(popover.getByRole('status')).toHaveText('正在检查');
  await page.evaluate(()=>window.emitUpdate({phase:'downloading',version:'0.1.2-beta.12',downloaded:25,total:100}));
  await expect(popover.getByRole('status')).toHaveText('正在下载 25%');
  await expect(popover.getByRole('button',{name:'检查更新'})).toBeDisabled();
  await page.evaluate(()=>window.emitUpdate({phase:'ready',version:'0.1.2-beta.12'}));
  await popover.getByRole('button',{name:'重启更新'}).click();
  await expect(popover.getByRole('alert')).toHaveText('请先结束游戏、下载或登录任务。');
  if(width===1280)await page.screenshot({path:'../.local/beta13-update-login.png',animations:'disabled'});
  const bounds=await popover.boundingBox();expect(bounds.x).toBeGreaterThanOrEqual(0);expect(bounds.x+bounds.width).toBeLessThanOrEqual(width);
  await page.keyboard.press('Escape');await expect(popover).toHaveCount(0);await expect(notice).toBeFocused();
  await page.evaluate(()=>window.emitUpdate({phase:'current'}));await expect(notice).toBeVisible();await expect(notice).toHaveText('检查更新');
 }
 await page.setViewportSize({width:1280,height:820});
 await page.goto(process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18475');
 await page.getByRole('button',{name:'加入QQ群 1105114550'}).click();
 await page.getByRole('button',{name:'启动器设置'}).click();
 await expect(page.locator('.launcher-version')).toHaveText('当前版本 0.1.2-beta.13');
 await page.screenshot({path:'../.local/beta13-settings.png',animations:'disabled'});
 const panel=page.locator('.launcher-update');
 await expect(panel.getByRole('status')).toHaveText('暂无更新');
 await panel.getByRole('button',{name:'检查更新'}).click();
 await expect(panel.getByRole('button',{name:'检查更新'})).toBeDisabled();
 await page.evaluate(()=>window.emitUpdate({phase:'downloading',downloaded:25,total:100,version:'0.2.0-beta.1'}));
 await expect(page.locator('.window-update')).toBeVisible();
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
 fs.writeFileSync('../.local/launcher-update-ui-check.json',JSON.stringify({passed:true,progress:true,restartGuard:true,retryAfterError:true,greenTitlebarNotice:true,persistentCheckButton:true,currentVersionFromNative:true,groupJoinBeforeAndAfterLogin:true,beforeLogin:true,compactLayout:true,staleSnapshotIgnored:true,errors},null,2));
 console.log('Launcher update UI passed: progress, readiness, restart guard and failure recovery.');
}finally{await browser.close();}
