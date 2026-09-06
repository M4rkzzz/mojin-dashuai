import {chromium,expect} from '@playwright/test';
import {spawn} from 'node:child_process';
import fs from 'node:fs/promises';
const catalogue=JSON.parse(await fs.readFile('../activities/catalog.json','utf8'));
const server=spawn(process.execPath,['node_modules/vite/bin/vite.js','preview','--host','127.0.0.1','--port','18487','--strictPort'],{stdio:'ignore',windowsHide:true});
const browser=await chromium.launch({headless:true});
try{
 for(let n=0;n<50;n++){try{if((await fetch('http://127.0.0.1:18487')).ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}
 const page=await browser.newPage({viewport:{width:1280,height:820}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(({catalogue})=>{
  const listeners=[],views={};window.activityRequests=[];window.failDraw=true;
  for(const w of catalogue.worlds)views[w.id]={...w,instance:w.id,stage:w.stages.at(-1).name,today:'2026-09-06',week:'2026-08-31',lastSeen:'2026-09-06T11:00:00Z',tickets:3,medals:68,misses:49,guaranteeWithin:1,
   actions:w.actions.map(a=>({...a,current:a.count,eligible:true})),dailyReady:true,claimedIn:null,pendingDays:[],weeklySteps:w.weeklyLabels.map(label=>({label,done:true})),weeklyDays:3,weeklyReady:true,weeklyClaimed:false,pendingWeeks:[],awards:[],pool:w.rewards.map(r=>({...r,eligible:true})),cosmetics:[],showcases:[]};
  window.chrome={webview:{addEventListener:(_,cb)=>listeners.push(cb),postMessage:r=>{
   let result=null,ok=true,error;
   if(r.command==='bootstrap')result={profile:{id:'activities-test',gameName:'Player',kind:'hub'},settings:{root:'D:\\Content',contentDirectoryConfigured:true,memory:{m3e:8192,dc2:8192,mb:8192,vw:4096},java:{},jvm:{},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',reducedMotion:false,theme:'dark',selectedRoutes:{}},installs:{}};
   if(r.command==='activities'){
    window.activityRequests.push(r.args);let v=views[r.args.instance];result=v;
    if(r.args.action==='daily'){v.claimedIn=v.name;v.tickets++;v.medals++;}
    if(r.args.action==='draw'){
     if(window.failDraw){window.failDraw=false;ok=false;error='连接暂时中断，请重试。';}
     else {v.resultAwardId='rare-1';v.tickets--;v.guaranteeWithin=50;v.misses=0;v.awards.unshift({id:'rare-1',tier:'rare',source:'抽奖',createdAt:'2026-09-06T11:00:00Z',status:'pending',items:[],choices:v.pool.filter(p=>p.tier==='rare')});}
    }
    if(r.args.action==='select'){let a=v.awards[0],reward=v.pool.find(p=>p.id===r.args.rewardId);a.name=reward.name;a.items=reward.items;a.status='queued';a.choices=[];}
   }
   setTimeout(()=>listeners.forEach(cb=>cb({data:{id:r.id,ok,result:structuredClone(result),error}})),r.command==='activities'?80:0);
  }}};
 },{catalogue});
 await page.goto('http://127.0.0.1:18487');await page.getByRole('button',{name:'活动',exact:true}).click();
 await expect(page.getByRole('button',{name:'领取今日签到'})).toBeEnabled();
 await page.screenshot({path:'../.local/activities-daily.png',animations:'disabled'});
 await page.getByRole('button',{name:'领取今日签到'}).click();await expect(page.getByRole('status')).toContainText('签到已领取');
 await page.getByRole('button',{name:'抽奖与奖励',exact:true}).click();
 await page.getByRole('button',{name:'使用 1 张抽奖券'}).click();await expect(page.getByRole('alert')).toContainText('连接暂时中断');
 await page.getByRole('button',{name:'使用 1 张抽奖券'}).click();await expect(page.locator('.draw-result')).toContainText('稀有推进奖励');
 const attempts=await page.evaluate(()=>window.activityRequests.filter(r=>r.action==='draw'));expect(attempts[0].operationId).toBe(attempts[1].operationId);
 await page.getByRole('button',{name:'选择这份材料'}).first().click();await expect(page.locator('.award-list')).toContainText('待入服领取');
 await page.getByText('查看奖池与领取条件',{exact:true}).click();
 await expect(page.locator('.activity-item').first()).toBeVisible();
 await expect(page.locator('.supply-chest')).toBeVisible();
 await page.screenshot({path:'../.local/activities-draw.png',animations:'disabled'});
 for(const world of catalogue.worlds){await page.locator('.activity-worlds').getByRole('button',{name:world.name,exact:true}).click();await expect(page.locator('.activity-draw')).toBeVisible();}
 await page.getByRole('button',{name:'纪念兑换',exact:true}).click();await expect(page.locator('.cosmetic-art')).toHaveCount(3);
 await page.screenshot({path:'../.local/activities-shop.png'});
 await page.setViewportSize({width:1024,height:720});expect(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth)).toBe(false);
 await page.emulateMedia({reducedMotion:'reduce'});expect(await page.locator('.cosmetic-grid .activity-panel').first().evaluate(e=>getComputedStyle(e).animationName)).toBe('none');
 const missing=await page.locator('.activities img').evaluateAll(images=>images.filter(i=>!i.complete||i.naturalWidth===0).map(i=>i.src));expect(missing).toEqual([]);expect(errors).toEqual([]);
 console.log('Activity UI smoke passed: four worlds, sign-in, retry ID, rare choice, real textures, cosmetics, compact layout, reduced motion.');
}finally{await browser.close();server.kill();}
