import {chromium,expect} from '@playwright/test';
import fs from 'node:fs';

const browser=await chromium.launch({headless:true});
const url=process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18473';
const errors=[];
async function fixture(){
 const page=await browser.newPage({viewport:{width:1280,height:820},deviceScaleFactor:1.25,reducedMotion:'reduce'});
 page.on('pageerror',error=>errors.push(error.message));
 await page.addInitScript(()=>{
  const listeners=[];let loginRequest=null;
  const emit=data=>listeners.forEach(callback=>callback({data}));
  window.testCommands=[];
  window.testCode=expires=>emit({event:'microsoft-code',data:{userCode:'TEST-CODE',verificationUrl:'https://www.microsoft.com/link',expiresAt:new Date(Date.now()+expires).toISOString()}});
  window.testComplete=error=>{
   emit({event:'microsoft-code',data:null});
   emit(error?{id:loginRequest.id,ok:false,error}:{id:loginRequest.id,ok:true,result:{profile:{id:'synthetic',loginName:'Test',gameName:'Test',kind:'microsoft'}}});
   loginRequest=null;
  };
  window.chrome={webview:{addEventListener:(_,callback)=>listeners.push(callback),postMessage:request=>queueMicrotask(()=>{
   window.testCommands.push(request.command);
   if(request.command==='auth.microsoft'){loginRequest=request;return;}
   if(request.command==='auth.microsoft.cancel'&&loginRequest){emit({event:'microsoft-code',data:null});emit({id:loginRequest.id,ok:true,result:{cancelled:true}});loginRequest=null;}
   const result=request.command==='bootstrap'?{profile:null,installs:{},settings:{root:'C:\\Games',contentDirectoryConfigured:false,memory:{},java:{},jvm:{},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',reducedMotion:true,theme:'dark',selectedRoutes:{}}}:null;
   emit({id:request.id,ok:true,result});
  })}};
 });
 await page.goto(url);
 await page.getByRole('button',{name:'使用微软正版账号登录'}).click();
 await expect(page.getByRole('status')).toHaveText('正在获取登录码');
 return page;
}
try{
 const page=await fixture();
 expect(await page.evaluate(()=>window.testCommands.includes('auth.microsoft.open'))).toBe(false);
 await page.evaluate(()=>window.testCode(600000));
 await expect(page.getByLabel('微软登录码',{exact:true})).toHaveText('TEST-CODE');
 for(const [width,height] of [[1280,820],[820,650],[640,600]]){
  await page.setViewportSize({width,height});
  await expect(page.getByRole('button',{name:'打开微软登录页'})).toBeInViewport();
  await expect(page.getByRole('button',{name:'取消登录'})).toBeInViewport();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth||document.documentElement.scrollHeight>innerHeight)).toBe(false);
 }
 await page.setViewportSize({width:1280,height:820});
 await page.screenshot({path:'../.local/microsoft-device-code.png',animations:'disabled'});
 await page.getByRole('button',{name:'复制登录码',exact:true}).click();
 await expect(page.getByRole('button',{name:'已复制登录码'})).toBeVisible();
 await page.getByRole('button',{name:'打开微软登录页'}).click();
 expect(await page.evaluate(()=>window.testCommands.filter(x=>x==='auth.microsoft.copy'||x==='auth.microsoft.open'))).toEqual(['auth.microsoft.copy','auth.microsoft.open']);
 expect(await page.evaluate(()=>[localStorage.length,sessionStorage.length])).toEqual([0,0]);
 await page.evaluate(()=>window.testComplete());
 await expect(page.getByRole('heading',{name:'游戏文件保存位置'})).toBeVisible();
 await page.close();

 for(const codeFirst of [false,true]){
  const cancel=await fixture();
  if(codeFirst)await cancel.evaluate(()=>window.testCode(600000));
  await cancel.keyboard.press('Escape');
  await expect(cancel.getByRole('button',{name:'使用微软正版账号登录'})).toBeEnabled();
  expect(await cancel.evaluate(()=>window.testCommands.filter(x=>x==='auth.microsoft.cancel').length)).toBe(1);
  await expect(cancel.getByRole('heading',{name:'游戏文件保存位置'})).toHaveCount(0);
  await expect(cancel.getByRole('alert')).toHaveCount(0);
  await cancel.close();
 }
 const expired=await fixture();
 await expired.evaluate(()=>window.testCode(-1000));
 await expect(expired.getByRole('alert')).toContainText('登录码已过期');
 await expect(expired.getByRole('button',{name:'使用微软正版账号登录'})).toBeEnabled();
 expect(await expired.evaluate(()=>window.testCommands.includes('auth.microsoft.cancel'))).toBe(true);
 await expired.close();

 const rejected=await fixture();
 await rejected.evaluate(()=>window.testComplete('正版登录应用配置未通过验证，请联系管理员。'));
 await expect(rejected.getByRole('alert')).toContainText('应用配置');
 await expect(rejected.getByRole('button',{name:'使用微软正版账号登录'})).toBeEnabled();
 await rejected.close();
 expect(errors).toEqual([]);
 fs.writeFileSync('../.local/microsoft-ui-check.json',JSON.stringify({passed:true,syntheticBridge:true,liveMicrosoftLoginVerified:false,states:['requesting','code','copy','open','success','cancelBeforeCode','cancelAfterCode','expired','applicationRejected'],noBrowserStorage:true,errors},null,2));
 console.log('Microsoft device-code UI passed: display, explicit browser action, cancellation, expiry and account transition (synthetic bridge).');
}finally{await browser.close();}
