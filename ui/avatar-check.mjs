import {chromium,expect} from '@playwright/test';
import fs from 'node:fs';

const browser=await chromium.launch({headless:true});
const checks=[];
try {
  for(const [model,height] of [['classic',64],['slim',64],['classic',32]]) {
    const page=await browser.newPage({viewport:{width:1280,height:820}});
    const errors=[];page.on('pageerror',error=>errors.push(error.message));
    await page.addInitScript(({model,height})=>{
      const listeners=[];
      const fixture=document.createElement('canvas');fixture.width=64;fixture.height=height;
      const ctx=fixture.getContext('2d');
      ctx.fillStyle='#c99063';ctx.fillRect(8,8,8,8);ctx.fillRect(44,20,4,12);if(height===64)ctx.fillRect(36,52,4,12);
      ctx.fillStyle='#34241b';ctx.fillRect(8,8,8,2);ctx.fillRect(8,10,1,2);ctx.fillRect(15,10,1,2);ctx.fillRect(11,14,2,1);
      ctx.fillStyle='#f5efdf';ctx.fillRect(9,12,2,1);ctx.fillRect(13,12,2,1);
      ctx.fillStyle='#252a35';ctx.fillRect(10,12,1,1);ctx.fillRect(13,12,1,1);
      ctx.fillStyle='#335d93';ctx.fillRect(20,20,8,12);ctx.fillRect(44,20,4,4);if(height===64)ctx.fillRect(36,52,4,4);
      const skin={pngBase64:fixture.toDataURL().split(',')[1],model};
      const settings={root:'',memory:{m3e:8192,dc2:8192,mb:8736},java:{m3e:'',dc2:'',mb:''},jvm:{m3e:'',dc2:'',mb:''},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',reducedMotion:true,theme:'dark',selectedRoutes:{m3e:'auto',dc2:'auto',mb:'auto'}};
      window.chrome={webview:{addEventListener:(_,fn)=>listeners.push(fn),postMessage:request=>queueMicrotask(()=>{
        const result=request.command==='bootstrap'?{profile:{id:'skin-test',gameName:'Mojin_QA',loginName:'skin-test',kind:'hub'},settings,installs:{}}:request.command==='account.avatar'?skin:request.command==='account.skin'?{...skin,model:request.args.model}:null;
        listeners.forEach(fn=>fn({data:{id:request.id,ok:true,result}}));
      })}};
    },{model,height});
    await page.goto(process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18473');
    const avatar=page.locator('.account canvas');
    await expect(avatar).toHaveAttribute('data-state','skin');
    await expect(page.locator('.app-shell')).toHaveCSS('background-color','rgb(9, 9, 9)');
    const pixels=await avatar.evaluate(canvas=>{
      const context=canvas.getContext('2d');
      return {face:[...context.getImageData(75,35,1,1).data],body:[...context.getImageData(75,125,1,1).data],outerArm:[...context.getImageData(5,155,1,1).data]};
    });
    expect(pixels.face).toEqual([201,144,99,255]);
    expect(pixels.body).toEqual([51,93,147,255]);
    expect(pixels.outerArm[3]).toBe(model==='slim'?0:255);
    await page.getByRole('button',{name:'头像与皮肤',exact:true}).click();
    await expect(page.getByLabel('模型')).toHaveValue(model);
    await expect(page.locator('dialog canvas')).toHaveAttribute('data-state','skin');
    if(model==='classic'&&height===64)await page.screenshot({path:'../.local/skin-halfbody.png',animations:'disabled'});
    await page.getByRole('button',{name:'关闭对话框'}).click();
    expect(errors).toEqual([]);checks.push({model,height,pixels});
    await page.close();
  }
  fs.writeFileSync('../.local/avatar-check.json',JSON.stringify({checks,source:'synthetic skin fixture; no player account used'},null,2));
  console.log('Classic, slim and legacy half-body skins rendered with verified texture pixels.');
} finally {await browser.close();}
