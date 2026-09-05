import {chromium,expect} from '@playwright/test';
import {spawn} from 'node:child_process';
import fs from 'node:fs/promises';

await fs.mkdir('../.local',{recursive:true});
const url=process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18478';
const server=process.env.LAUNCHER_UI_URL?null:spawn(process.execPath,['node_modules/vite/bin/vite.js','--host','127.0.0.1','--port','18478','--strictPort'],{stdio:'ignore',windowsHide:true});
let browser;
try{
 if(server){let ready=false;for(let index=0;index<60;index++){if(server.exitCode!==null)throw new Error('Content update test server exited.');try{if((await fetch(url)).ok){ready=true;break;}}catch{}await new Promise(resolve=>setTimeout(resolve,100));}if(!ready)throw new Error('Content update test server did not become ready.');}
 browser=await chromium.launch({headless:true});const page=await browser.newPage({viewport:{width:1280,height:820},reducedMotion:'reduce'});
 const errors=[];page.on('pageerror',error=>errors.push(error.message));await page.clock.install();
 await page.addInitScript(()=>{
  const listeners=[],pending={};window.contentUpdateRequests=[];window.catalogOffline=false;
  const settings={root:'D:\\isolated-fixture\\content',contentDirectoryConfigured:true,memory:{m3e:8192,dc2:8192,mb:8736},java:{m3e:'',dc2:'',mb:''},jvm:{m3e:'',dc2:'',mb:''},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',proxyMode:'direct',skinSource:'account',reducedMotion:true,theme:'dark',selectedRoutes:{m3e:'auto',dc2:'auto',mb:'auto'}};
  const installs={m3e:{version:'m3e-1',sequence:1,state:'installed'},dc2:{version:'中文 r3',sequence:3,state:'installed'},mb:{version:'mb-1',sequence:1,state:'installed'}};
  const states={m3e:'installed',dc2:'installed',mb:'installed'},progress={},availableUpdates={};
  const emit=data=>listeners.forEach(callback=>callback({data}));
  const respond=(request,result)=>emit({id:request.id,ok:true,result});
  const snapshot=()=>({installs,states,progress,availableUpdates});
  window.markContentUpdate=(id,version,sequence)=>{availableUpdates[id]={version,sequence};if(states[id]!=='running')states[id]='update-available';};
  window.gameRunning=(id,running)=>{states[id]=running?'running':availableUpdates[id]?'update-available':'installed';emit({event:'instance-state',data:{instance:id,state:states[id]}});};
  window.failContentUpdate=id=>{progress[id]={...progress[id],paused:true,bytesPerSecond:0};states[id]='paused';emit({event:'progress',data:progress[id]});emit({id:pending[id].id,ok:false,error:'模拟更新下载失败'});delete pending[id];};
  window.finishContentUpdate=id=>{installs[id]={...availableUpdates[id],state:'installed'};delete availableUpdates[id];delete progress[id];states[id]='installed';emit({event:'installed',data:{instance:id}});respond(pending[id],null);delete pending[id];};
  window.chrome={webview:{addEventListener:(_,callback)=>listeners.push(callback),postMessage:request=>queueMicrotask(()=>{
   window.contentUpdateRequests.push(request);let result=null;
   if(request.command==='bootstrap')result={profile:{id:'fixture',loginName:'Fixture',gameName:'Fixture',kind:'hub'},settings,...snapshot()};
   if(request.command==='instances.status')result=snapshot();
   if(request.command==='instances.updates.check'){
    if(window.catalogOffline){emit({id:request.id,ok:false,error:'offline'});return;}
    if(installs.dc2.sequence<4)window.markContentUpdate('dc2','中文 r4',4);result=snapshot();
   }
   if(request.command==='routes.probe')result=[30,45];
   if(request.command==='instance.update'||request.command==='download.resume'){
    const id=request.args.instance;pending[id]=request;progress[id]={instance:id,phase:'正在下载世界内容',completed:10,total:100,bytesPerSecond:5};states[id]='downloading';emit({event:'progress',data:progress[id]});return;
   }
   if(request.command==='download.cancel'){
    const id=request.args.instance;delete progress[id];states[id]=availableUpdates[id]?'update-available':'installed';emit({event:'download-cancelled',data:{instance:id}});
   }
   if(request.command==='instance.launch')throw new Error('The update workflow must never launch a game.');
   respond(request,result);
  })}};
 });
 await page.goto(url);await expect(page.getByRole('heading',{name:'选择服务器',exact:true})).toBeVisible();
 const sidebar=name=>page.locator('.mini-world').filter({hasText:name});
 await expect(sidebar('亡者世界').locator('small')).toHaveText('需更新');
 await expect(sidebar('亡者世界').locator('small')).toHaveCSS('color','rgb(242, 167, 89)');
 await expect(sidebar('魔法金属').locator('small')).toHaveText('已安装');await expect(sidebar('肉丸工艺').locator('small')).toHaveText('已安装');
 expect(await page.evaluate(()=>window.contentUpdateRequests.filter(request=>request.command==='instances.updates.check').length)).toBe(1);
 await page.clock.fastForward(4100);
 expect(await page.evaluate(()=>window.contentUpdateRequests.filter(request=>request.command==='instances.updates.check').length)).toBe(1);
 await sidebar('亡者世界').click();await expect(page.getByRole('button',{name:'更新',exact:true})).toBeEnabled();
 await expect(page.locator('.install-heading')).toContainText('需更新');await expect(page.locator('.launch-panel dl')).toContainText('中文 r3');
 await page.screenshot({path:'../.local/beta12-content-update.png',animations:'disabled'});
 await page.getByRole('button',{name:'更新',exact:true}).click();await expect(page.locator('.download-progress')).toBeVisible();
 await sidebar('魔法金属').click();await expect(page.getByRole('button',{name:'进入游戏',exact:true})).toBeEnabled();
 await page.evaluate(()=>window.failContentUpdate('dc2'));await sidebar('亡者世界').click();await expect(page.getByRole('button',{name:'继续下载',exact:true})).toBeEnabled();
 await page.getByRole('button',{name:'取消',exact:true}).click();await expect(page.getByRole('button',{name:'更新',exact:true})).toBeEnabled();
 await expect(sidebar('亡者世界').locator('small')).toHaveText('需更新');
 await page.evaluate(()=>window.catalogOffline=true);await page.locator('.sidebar .nav-item').filter({hasText:'游戏大厅'}).click();
 await expect(sidebar('亡者世界').locator('small')).toHaveText('需更新');await page.clock.fastForward(2100);
 expect(await page.evaluate(()=>window.contentUpdateRequests.filter(request=>request.command==='instances.updates.check').length)).toBe(2);
 await sidebar('亡者世界').click();await page.getByRole('button',{name:'更新',exact:true}).click();await page.evaluate(()=>window.finishContentUpdate('dc2'));
 await expect(page.getByRole('button',{name:'进入游戏',exact:true})).toBeEnabled();await expect(sidebar('亡者世界').locator('small')).toHaveText('已安装');
 await expect(page.locator('.launch-panel dl')).toContainText('中文 r4');await expect(sidebar('亡者世界').locator('.update-required')).toHaveCount(0);
 await page.evaluate(()=>{window.markContentUpdate('mb','mb-2',2);window.gameRunning('mb',true);});await page.clock.fastForward(2100);await sidebar('肉丸工艺').click();
 await expect(sidebar('肉丸工艺').locator('small')).toHaveText('正在运行');await expect(sidebar('肉丸工艺').locator('.update-required')).toHaveCount(0);
 await expect(page.getByRole('button',{name:'正在运行',exact:true})).toBeDisabled();
 await page.evaluate(()=>window.gameRunning('mb',false));await expect(page.getByRole('button',{name:'更新',exact:true})).toBeEnabled();
 await expect(sidebar('亡者世界').locator('small')).toHaveText('已安装');await expect(sidebar('魔法金属').locator('small')).toHaveText('已安装');
 await page.getByRole('button',{name:'更新',exact:true}).click();await page.evaluate(()=>window.finishContentUpdate('mb'));await expect(page.getByRole('button',{name:'进入游戏',exact:true})).toBeEnabled();
 const commands=await page.evaluate(()=>window.contentUpdateRequests.filter(request=>request.command.startsWith('instance.')).map(request=>({command:request.command,instance:request.args.instance})));
 expect(commands).toEqual([{command:'instance.update',instance:'dc2'},{command:'instance.update',instance:'dc2'},{command:'instance.update',instance:'mb'}]);
 expect(errors).toEqual([]);
 await fs.writeFile('../.local/beta12-content-update-check.json',JSON.stringify({passed:true,orangeUpdateStatus:true,oneCheckPerLobbyEntry:true,offlinePreservesKnownUpdates:true,failurePreservesUpdate:true,perInstanceIsolation:true,runningHasPriority:true,noAutomaticGameLaunch:true,commands,errors},null,2));
 console.log('Content update UI passed: orange pending status, one lobby check, offline/failure retention, per-instance isolation, running priority and update without auto-launch.');
}finally{await browser?.close();server?.kill();}
