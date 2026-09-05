import {chromium,expect} from '@playwright/test';
import fs from 'node:fs';

const browser=await chromium.launch({headless:true});
try {
 const page=await browser.newPage({viewport:{width:1280,height:820},reducedMotion:'reduce'});
 const errors=[];page.on('pageerror',error=>errors.push(error.message));
 await page.addInitScript(()=>{
  const listeners=[];
  const texture=(width,color)=>{
   const image=document.createElement('canvas');image.width=width;image.height=width/2;
   const context=image.getContext('2d');context.fillStyle=color;context.fillRect(0,0,width,width/2);
   return {pngBase64:image.toDataURL().split(',')[1],model:'classic'};
  };
  const account=texture(64,'#339966'),little=texture(1024,'#cc6633');
  const settings={root:'D:\\fixture\\content',contentDirectoryConfigured:true,skinSource:sessionStorage.getItem('fixture-skin-source')||'account',reducedMotion:true};
  window.skinRequests=[];window.skinScenario='ready';window.heldSkinRequest=null;
  const respond=(request,result)=>listeners.forEach(fn=>fn({data:{id:request.id,ok:true,result}}));
  window.chrome={webview:{addEventListener:(_,fn)=>listeners.push(fn),postMessage:request=>queueMicrotask(()=>{
   window.skinRequests.push(request);let result=null;
   if(request.command==='bootstrap')result={profile:{id:'skin-test',gameName:'Fixture',loginName:'Fixture',kind:'microsoft'},settings,installs:{}};
   if(request.command==='account.skin.source'){
    settings.skinSource=request.args.source;sessionStorage.setItem('fixture-skin-source',settings.skinSource);
   }
   if(request.command==='account.skin.preview'){
    result={texture:settings.skinSource==='account'?account:little,status:'ready'};
    if(window.skinScenario==='hold'&&request.args.refresh){window.heldSkinRequest=()=>respond(request,result);return;}
    if(window.skinScenario==='missing')result={texture:null,status:'missing',message:'未找到 Fixture 的 LittleSkin 皮肤，请检查同名角色是否已设置皮肤。'};
    if(window.skinScenario==='cached')result={texture:little,status:'cached',message:'皮肤网络请求失败，正在显示此来源上次获取的皮肤。'};
    if(window.skinScenario==='error')result={texture:null,status:'error',message:'皮肤网络请求失败，请稍后刷新。'};
   }
   respond(request,result);
  })}};
 });
 await page.goto(process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18473');
 const avatar=page.locator('.account canvas');
 const face=canvas=>canvas.evaluate(node=>Array.from(node.getContext('2d').getImageData(75,35,1,1).data));
 await expect.poll(()=>face(avatar)).toEqual([51,153,102,255]);
 await page.getByRole('button',{name:'头像与皮肤',exact:true}).click();
 await expect(page.getByRole('button',{name:'管理正版皮肤',exact:true})).toBeVisible();
 await page.getByLabel('皮肤来源').selectOption('littleskin');
 await expect.poll(()=>face(avatar)).toEqual([204,102,51,255]);
 await expect.poll(()=>face(page.locator('dialog canvas'))).toEqual([204,102,51,255]);
 await expect(page.getByRole('button',{name:'管理正版皮肤',exact:true})).toHaveCount(0);
 expect(await page.evaluate(()=>window.skinRequests.filter(x=>x.command==='account.skin.preview').length)).toBe(2);
 await page.reload();
 await expect.poll(()=>face(avatar)).toEqual([204,102,51,255]);
 await page.getByRole('button',{name:'头像与皮肤',exact:true}).click();
 await expect(page.getByLabel('皮肤来源')).toHaveValue('littleskin');
 await page.evaluate(()=>window.skinScenario='missing');
 await page.getByRole('button',{name:'刷新皮肤',exact:true}).click();
 await expect(page.locator('.skin-feedback')).toContainText('未找到 Fixture');
 await expect(avatar).toHaveAttribute('data-state','default');
 await page.evaluate(()=>window.skinScenario='error');
 await page.getByRole('button',{name:'刷新皮肤',exact:true}).click();
 await expect(page.locator('.skin-feedback')).toContainText('皮肤网络请求失败');
 await expect(avatar).toHaveAttribute('data-state','default');
 await page.evaluate(()=>window.skinScenario='cached');
 await page.getByRole('button',{name:'刷新皮肤',exact:true}).click();
 await expect(page.locator('.skin-feedback')).toContainText('此来源');
 await expect.poll(()=>face(avatar)).toEqual([204,102,51,255]);
 // A delayed account refresh must not overwrite the newly selected LittleSkin texture.
 await page.evaluate(()=>window.skinScenario='ready');
 await page.getByLabel('皮肤来源').selectOption('account');
 await expect.poll(()=>face(avatar)).toEqual([51,153,102,255]);
 await page.evaluate(()=>window.skinScenario='hold');
 await page.getByRole('button',{name:'刷新皮肤',exact:true}).click();
 await expect(page.locator('.skin-feedback')).toHaveText('正在加载皮肤');
 await page.getByLabel('皮肤来源').selectOption('littleskin');
 await expect.poll(()=>face(avatar)).toEqual([204,102,51,255]);
 await page.evaluate(()=>window.heldSkinRequest());
 await expect(page.locator('.skin-feedback')).toHaveCount(0);
 await expect.poll(()=>face(avatar)).toEqual([204,102,51,255]);
 expect(errors).toEqual([]);
 await page.screenshot({path:'../.local/littleskin-switch.png',animations:'disabled'});
 fs.writeFileSync('../.local/skin-source-check.json',JSON.stringify({passed:true,hd:true,sourceSwitch:true,persisted:true,missing:true,error:true,sameSourceCache:true,staleRequestIgnored:true},null,2));
 console.log('Skin source switching, HD preview, refresh, restart, missing/error feedback and stale response isolation passed.');
} finally {await browser.close();}
