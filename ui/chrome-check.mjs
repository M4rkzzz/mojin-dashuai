import { chromium, expect } from '@playwright/test';
import fs from 'node:fs';

const browser = await chromium.launch({ headless: true });
const errors = [];
const results = [];
const page = await browser.newPage();
page.setDefaultTimeout(10000);
page.on('pageerror', error => errors.push(error.message));
await page.addInitScript(() => {
  const subscribers = [];
  let maximized = false;
  const settings = {root:'',memory:{m3e:8192,dc2:8192,mb:8736},java:{m3e:'',dc2:'',mb:''},jvm:{m3e:'',dc2:'',mb:''},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',reducedMotion:true,theme:'dark',selectedRoutes:{m3e:'auto',dc2:'auto',mb:'auto'}};
  const emit = data => subscribers.forEach(callback => callback({data}));
  window.chrome = {webview:{
    addEventListener: (_, callback) => subscribers.push(callback),
    postMessage: request => queueMicrotask(() => {
      let result = null;
      if (request.command === 'bootstrap') result = {profile:null,settings,installs:{}};
      if (request.command === 'window.maximize') {maximized = !maximized;emit({event:'window-state',data:{maximized}});}
      emit({id:request.id,ok:true,result});
    }),
  }};
});

try {
  for (const [width,height] of [[1280,820],[1024,656],[820,650],[900,600]]) {
    await page.setViewportSize({width,height});
    await page.goto(process.env.LAUNCHER_UI_URL || 'http://127.0.0.1:18473');
    await expect(page.getByRole('button',{name:'进入大厅'})).toBeEnabled();
    await expect(page.locator('.window-chrome')).toHaveCount(1);
    await expect(page.locator('.window-chrome')).toHaveCSS('-webkit-app-region','drag');
    await expect(page.getByRole('button',{name:'最大化',exact:true})).toHaveCSS('-webkit-app-region','no-drag');
    await page.getByRole('button',{name:'最大化',exact:true}).click();
    await expect(page.getByRole('button',{name:'还原',exact:true})).toBeVisible();
    await page.getByRole('button',{name:'还原',exact:true}).click();
    for (const mode of ['login','register','recover']) {
      if (mode === 'register') await page.getByRole('button',{name:'使用邀请码注册'}).click();
      if (mode === 'recover') {
        await page.getByRole('button',{name:'返回登录',exact:true}).click();
        await page.getByRole('button',{name:'忘记密码？',exact:true}).click();
      }
      const metrics = await page.evaluate(() => {
        const scene = document.querySelector('.login-scene');
        const chrome = document.querySelector('.window-chrome');
        return {documentOverflow:document.documentElement.scrollHeight>innerHeight,horizontalOverflow:document.documentElement.scrollWidth>innerWidth,sceneOverflow:scene.scrollHeight>scene.clientHeight+1,top:chrome.getBoundingClientRect().top,emoji:/\p{Extended_Pictographic}/u.test(document.body.innerText),background:getComputedStyle(document.querySelector('.app-shell')).backgroundImage};
      });
      expect(metrics.documentOverflow).toBe(false);
      expect(metrics.horizontalOverflow).toBe(false);
      expect(metrics.top).toBe(0);
      expect(metrics.emoji).toBe(false);
      expect(metrics.background).toContain('magic.webp');
      const sceneLoaded=await page.evaluate(async()=>{
        const image=new Image();image.src='./scenes/magic.webp';await image.decode();
        return image.naturalWidth>=1000&&image.naturalHeight>=500;
      });
      expect(sceneLoaded).toBe(true);
      if (mode === 'login') expect(metrics.sceneOverflow).toBe(false);
      results.push({width,height,mode,...metrics});
      if(width === 1024 && mode === 'login') await page.screenshot({path:'../.local/titlebar-login.png',animations:'disabled'});
      if(width === 820 && mode === 'register') await page.screenshot({path:'../.local/register-820.png',animations:'disabled'});
    }
  }
  expect(errors).toEqual([]);
  fs.writeFileSync('../.local/chrome-check.json',JSON.stringify({results,errors,nativeDragVerified:false},null,2));
  console.log(`${results.length} headless layout checks passed; native window interaction deferred.`);
} finally { await browser.close(); }
