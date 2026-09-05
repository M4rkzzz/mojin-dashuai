import {chromium,expect} from '@playwright/test';
import fs from 'node:fs';

const browser=await chromium.launch({headless:true});
const page=await browser.newPage({viewport:{width:1280,height:820},reducedMotion:'reduce'});
const errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.clock.install();
await page.addInitScript(()=>{
 const listeners=[],pending={};window.instanceRequests=[];
 const settings={root:'D:\\魔金大帅\\content',contentDirectoryConfigured:true,memory:{m3e:8192,dc2:8192,mb:8736,vw:4096},java:{m3e:'',dc2:'',mb:'',vw:''},jvm:{m3e:'',dc2:'',mb:'',vw:''},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',proxyMode:'direct',skinSource:'account',reducedMotion:true,theme:'dark',selectedRoutes:{m3e:'auto',dc2:'auto',mb:'auto',vw:'auto'}};
 const installs={m3e:{version:'test',state:'installed'}},states={m3e:'installed',dc2:'not-installed',mb:'not-installed'},progress={};
 const emit=data=>listeners.forEach(cb=>cb({data}));
 const response=(r,result)=>emit({id:r.id,ok:true,result});
 const snapshot=()=>({installs,states,progress});
 window.progressFor=(id,completed)=>{progress[id]={instance:id,phase:'正在下载世界内容',completed,total:1500*1024*1024,bytesPerSecond:3.1*1024*1024};states[id]='downloading';emit({event:'progress',data:progress[id]});};
 window.finishInstance=id=>{delete progress[id];installs[id]={version:'test',state:'installed'};states[id]='installed';emit({event:'installed',data:{instance:id}});response(pending[id],null);delete pending[id];};
 window.gameRunning=(id,running)=>{states[id]=running?'running':'installed';emit({event:'instance-state',data:{instance:id,state:states[id]}});};
 window.chrome={webview:{addEventListener:(_,cb)=>listeners.push(cb),postMessage:r=>queueMicrotask(()=>{
  window.instanceRequests.push(r);let result=null;
  if(r.command==='bootstrap')result={profile:{id:'fixture',loginName:'Player',gameName:'Player',kind:'hub'},settings,...snapshot()};
  if(r.command==='instances.status')result=snapshot();
  if(r.command==='routes.probe')result=[35,61];
  if(r.command==='instance.install'||r.command==='download.resume'){pending[r.args.instance]=r;window.progressFor(r.args.instance,636*1024*1024);return;}
  if(r.command==='download.pause'){
   const id=r.args.instance;progress[id]={...progress[id],paused:true,bytesPerSecond:0,phase:'下载已暂停'};states[id]='paused';emit({event:'progress',data:progress[id]});if(pending[id]){response(pending[id],null);delete pending[id];}
  }
  if(r.command==='download.cancel'){const id=r.args.instance;delete progress[id];states[id]=installs[id]?'installed':'not-installed';emit({event:'download-cancelled',data:{instance:id}});if(pending[id]){response(pending[id],null);delete pending[id];}}
  if(r.command==='instance.launch'){window.gameRunning(r.args.instance,true);}
  if(r.command==='fixture.commit'){progress.mb={instance:'mb',phase:'保存安装记录',completed:0,total:0,bytesPerSecond:0};emit({event:'progress',data:progress.mb});return;}
  if(r.command==='settings.save')Object.assign(settings,r.args);
  if(r.command==='network.check')result=[{name:'账号与目录',host:'launcher-direct.boshan.uk:21708',ok:false,elapsedMs:8000,diagnostic:{id:'test-id',stage:'获取客户端清单',category:'DNS 解析失败',host:'launcher-direct.boshan.uk:21708',path:'/v1/catalog',code:'HostNotFound',proxyMode:'direct',attempt:2}}];
  response(r,result);
 })}};
});
try{
 await page.goto(process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18475');
 const select=id=>page.locator('.mini-world').filter({hasText:id}).click();
 await select('亡者世界');await page.getByRole('button',{name:'安装并开始',exact:true}).click();
 await expect(page.locator('.download-progress')).toContainText('正在下载世界内容');
 await select('肉丸工艺');await expect(page.getByRole('button',{name:'安装并开始',exact:true})).toBeEnabled();
 await page.getByRole('checkbox',{name:'仅下载',exact:true}).check();await page.getByRole('button',{name:'下载客户端',exact:true}).click();
 expect(await page.evaluate(()=>window.instanceRequests.filter(r=>r.command==='instance.install').map(r=>r.args))).toEqual([{instance:'dc2',downloadOnly:false},{instance:'mb',downloadOnly:true}]);
 await expect(page.locator('.mini-world').filter({hasText:'亡者世界'})).toContainText('正在下载');
 await expect(page.locator('.mini-world').filter({hasText:'肉丸工艺'})).toContainText('正在下载');
 await expect(page.locator('.download-progress small')).toContainText('3.1 MB / s');
 await page.clock.fastForward(4000);await expect(page.locator('.download-progress small')).toContainText('0.0 MB / s');
 await page.getByRole('button',{name:'暂停下载',exact:true}).click();await expect(page.getByRole('button',{name:'继续下载',exact:true})).toBeEnabled();
 await select('亡者世界');await expect(page.getByRole('button',{name:'暂停下载',exact:true})).toBeEnabled();
 await select('肉丸工艺');await page.getByRole('button',{name:'继续下载',exact:true}).click();
 await page.evaluate(()=>{
  window.chrome.webview.postMessage({id:'commit-fixture',command:'fixture.commit'});
 });
 await expect(page.locator('.download-progress')).toContainText('保存安装记录');
 await expect(page.locator('.download-actions')).toHaveCount(0);
 await expect(page.locator('.download-progress')).not.toContainText('100%');
 await page.evaluate(()=>window.finishInstance('mb'));await expect(page.getByRole('button',{name:'进入游戏',exact:true})).toBeEnabled();
 expect(await page.evaluate(()=>window.instanceRequests.filter(r=>r.command==='instance.launch'))).toHaveLength(0);
 await page.getByRole('button',{name:'进入游戏',exact:true}).click();await expect(page.getByRole('button',{name:'正在运行',exact:true})).toBeDisabled();
 await select('亡者世界');await expect(page.getByRole('button',{name:'暂停下载',exact:true})).toBeEnabled();
 await page.getByRole('button',{name:'取消',exact:true}).click();await expect(page.getByRole('button',{name:'安装并开始',exact:true})).toBeEnabled();
 await select('肉丸工艺');await page.evaluate(()=>window.gameRunning('mb',false));await expect(page.getByRole('button',{name:'进入游戏',exact:true})).toBeEnabled();
 await page.getByRole('button',{name:'工具',exact:true}).click();await expect(page.getByRole('heading',{name:'导入旧客户端配置',exact:true})).toBeVisible();
 await page.getByRole('button',{name:'启动器设置',exact:true}).click();await page.getByRole('button',{name:'下载',exact:true}).click();
 await expect(page.getByRole('combobox',{name:'网络连接方式'})).toHaveValue('direct');await expect(page.getByPlaceholder('http://127.0.0.1:7890')).toHaveCount(0);
 await page.getByRole('combobox',{name:'网络连接方式'}).selectOption('manual');await expect(page.getByRole('textbox',{name:'代理地址'})).toBeVisible();
 await page.getByRole('combobox',{name:'网络连接方式'}).selectOption('direct');await page.getByRole('button',{name:'检测网络',exact:true}).click();
 await page.getByText('诊断详情',{exact:true}).click();await expect(page.locator('.network-diagnostic')).toContainText('DNS 解析失败');await expect(page.locator('.network-diagnostic')).toContainText('HostNotFound');
 await page.getByRole('button',{name:'复制诊断',exact:true}).click();await expect(page.getByRole('button',{name:'已复制',exact:true})).toBeVisible();
 await page.screenshot({path:'../.local/beta8-instance-network-check.png',animations:'disabled'});
 expect(errors).toEqual([]);
 fs.writeFileSync('../.local/beta8-instance-ui-check.json',JSON.stringify({passed:true,parallelInstances:true,pauseCancelIsolation:true,runningState:true,downloadOnly:true,staleSpeedZero:true,proxyModes:true,diagnosticCopy:true,errors},null,2));
 console.log('Instance UI passed: parallel downloads, pause/cancel isolation, download-only, running state, stale speed, network diagnostics and proxy modes.');
}finally{await browser.close();}
