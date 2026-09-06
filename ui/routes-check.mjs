import {chromium,expect} from '@playwright/test';

const browser=await chromium.launch({headless:true});
const page=await browser.newPage({viewport:{width:1280,height:820},reducedMotion:'reduce'});
const errors=[];page.on('pageerror',error=>errors.push(error.message));
await page.clock.install();
await page.addInitScript(()=>{
 const listeners=[];
 window.routeCalls=[];window.routeReplies={m3e:[{latency:42,onlinePlayers:7},{latency:138,onlinePlayers:7}],dc2:[{latency:256,onlinePlayers:0},{latency:-1,onlinePlayers:99}],mb:[{latency:99,onlinePlayers:null},{latency:200,onlinePlayers:12}],vw:[{latency:31,onlinePlayers:null},{latency:62,onlinePlayers:null}]};window.holdRoutes=true;window.pendingRoutes=[];
 const settings={root:'C:\\Games\\魔金大帅',contentDirectoryConfigured:true,memory:{m3e:8192,dc2:8192,mb:8736,vw:4096},java:{m3e:'',dc2:'',mb:'',vw:''},jvm:{m3e:'',dc2:'',mb:'',vw:''},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',reducedMotion:true,theme:'dark',selectedRoutes:{m3e:'auto',dc2:'auto',mb:'auto',vw:'auto'}};
 const emit=data=>listeners.forEach(callback=>callback({data}));
 window.chrome={webview:{addEventListener:(_,callback)=>listeners.push(callback),postMessage:request=>queueMicrotask(()=>{
  let result=null;
  if(request.command==='bootstrap')result={profile:{id:'fixture',loginName:'Player',gameName:'Player',kind:'hub'},settings,installs:{}};
  if(request.command==='routes.probe'){
   window.routeCalls.push(request.args.instance);
   const reply=()=>emit({id:request.id,ok:true,result:window.routeReplies[request.args.instance]});
   if(window.holdRoutes)window.pendingRoutes.push(reply);else reply();
   return;
  }
  if(request.command==='settings.save'){window.savedRouteSettings=request.args;result=request.args;}
  emit({id:request.id,ok:true,result});
 })}};
});
try{
 await page.goto(process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18475');
 const cards=page.locator('.world-card');
 await expect(cards).toHaveCount(4);
 await expect(page.locator('.card-routes .latency-pending')).toHaveCount(8);
 await expect(page.locator('.card-online')).toHaveText(['人数查询中','人数查询中','人数查询中','人数查询中']);
 await expect.poll(()=>page.evaluate(()=>window.routeCalls.length)).toBe(4);
 await page.evaluate(()=>{window.holdRoutes=false;window.pendingRoutes.splice(0).forEach(reply=>reply());});
 for(const [name,values] of [['魔法金属',['42 ms','138 ms']],['亡者世界',['256 ms','不可达']],['肉丸工艺',['99 ms','200 ms']],['虚空行者',['31 ms','62 ms']]]){
  const card=cards.filter({hasText:name});
  await expect(card.locator('.card-route').nth(0)).toHaveText('河北阿里云'+values[0]);
  await expect(card.locator('.card-route').nth(1)).toHaveText('宿迁三网'+values[1]);
 }
 await expect(page.locator('.card-routes .latency-good')).toHaveCount(4);
 await expect(page.locator('.card-routes .latency-fair')).toHaveCount(1);
 await expect(page.locator('.card-routes .latency-poor')).toHaveCount(2);
 await expect(page.locator('.card-routes .latency-offline')).toHaveCount(1);
 await expect(page.locator('.card-online')).toHaveText(['在线 7 人','在线 0 人','在线 12 人','人数暂不可用']);
 for(const viewport of [{width:1280,height:820},{width:1024,height:768}]){
  await page.setViewportSize(viewport);
  expect(await cards.evaluateAll(nodes=>nodes.every(card=>{const count=card.querySelector('.card-online').getBoundingClientRect(),footer=card.querySelector('.card-bottom').getBoundingClientRect();return count.left>=footer.left&&count.right<=footer.right&&count.bottom<=card.getBoundingClientRect().bottom;}))).toBe(true);
 }
 await page.setViewportSize({width:1280,height:820});
 expect(await cards.locator('.server-logo').evaluateAll(images=>images.every(img=>img.complete&&img.naturalWidth>0))).toBe(true);
 await page.screenshot({path:'../.local/lobby-routes.png',animations:'disabled'});
 await page.evaluate(()=>{window.routeReplies.m3e=[80,220];});
 await page.clock.fastForward(300000);
 expect(await page.evaluate(()=>window.routeCalls.length)).toBe(4);
 await expect(cards.filter({hasText:'魔法金属'}).locator('.card-route').nth(0)).toContainText('42 ms');
 await cards.filter({hasText:'肉丸工艺'}).click();
 await expect(page.locator('.routes .latency-good')).toHaveCount(1);
 const detailCalls=await page.evaluate(()=>window.routeCalls.length);
 await page.evaluate(()=>{window.holdRoutes=true;});
 await page.getByRole('button',{name:'加入QQ群 1105114550',exact:true}).click();
 await expect(page.locator('.routes .latency-pending')).toHaveCount(0);
 expect(await page.evaluate(()=>window.routeCalls.length)).toBe(detailCalls);
 await page.getByRole('button',{name:'宿迁三网',exact:false}).click();
 await expect.poll(()=>page.evaluate(()=>window.savedRouteSettings?.selectedRoutes.mb)).toBe('1');
 expect(await page.evaluate(()=>window.routeCalls.length)).toBe(detailCalls);
 await page.clock.fastForward(300000);
 expect(await page.evaluate(()=>window.routeCalls.length)).toBe(detailCalls);
 await page.getByRole('button',{name:'重新测速'}).click();
 await expect.poll(()=>page.evaluate(()=>window.routeCalls.length)).toBe(detailCalls+1);
 await expect(page.locator('.routes .latency-good')).toContainText('99 ms');
 await expect(page.locator('.routes .latency-poor')).toContainText('200 ms');
 await expect(page.locator('.routes .latency-pending')).toHaveCount(0);
 await page.clock.fastForward(30000);
 expect(await page.evaluate(()=>window.routeCalls.length)).toBe(detailCalls+1);
 await page.evaluate(()=>{window.holdRoutes=false;window.pendingRoutes.splice(0).forEach(reply=>reply());});
 await page.evaluate(()=>{window.routeReplies.mb=[-1,65];});
 await page.getByRole('button',{name:'重新测速'}).click();
 await expect(page.locator('.routes .latency-offline')).toHaveCount(1);
 await expect(page.locator('.routes .latency-good')).toContainText('65 ms');
 await page.evaluate(()=>{window.holdRoutes=true;});
 await page.getByRole('button',{name:'重新测速'}).click();
 await expect(page.locator('.routes .latency-offline')).toHaveCount(1);
 await expect(page.locator('.routes .latency-good')).toContainText('65 ms');
 await page.locator('.mini-world').filter({hasText:'魔法金属'}).click();
 await expect(page.locator('.routes .latency-pending')).toHaveCount(2);
 await page.evaluate(()=>{window.holdRoutes=false;window.pendingRoutes.splice(0).forEach(reply=>reply());});
 await expect(page.locator('.routes .latency-good')).toContainText('80 ms');
 await expect(page.locator('.routes .latency-poor')).toContainText('220 ms');
 expect(errors).toEqual([]);
 console.log('Server selection latency passed: no automatic polling after five minutes; manual refresh preserves results, rerenders do not restart probes, slow probes do not overlap, server changes reject stale results, eight routes and signal colors.');
}finally{await browser.close();}
