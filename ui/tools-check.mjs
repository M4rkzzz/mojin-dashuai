import {chromium,expect} from '@playwright/test';
const browser=await chromium.launch({headless:true});
const page=await browser.newPage({viewport:{width:1280,height:820}});
const errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.addInitScript(()=>{
 const listeners=[];window.importRequests=[];window.importCancel=false;
 const settings={root:'D:\\魔金大帅\\content',contentDirectoryConfigured:true,memory:{m3e:8192,dc2:8192,mb:8736},java:{m3e:'',dc2:'',mb:''},jvm:{m3e:'',dc2:'',mb:''},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',reducedMotion:true,theme:'dark',selectedRoutes:{m3e:'auto',dc2:'auto',mb:'auto'}};
 window.chrome={webview:{addEventListener:(_,cb)=>listeners.push(cb),postMessage:r=>queueMicrotask(()=>{
  let result=null;
  if(r.command==='bootstrap')result={profile:{id:'test',gameName:'Player',kind:'hub'},settings,installs:location.search.includes('empty')?{}:{m3e:{version:'test',state:'installed'},mb:{version:'test',state:'installed'}}};
  if(r.command==='instance.import'){window.importRequests.push(r.args);if(!window.importCancel)result={source:'D:\\旧客户端\\.minecraft\\versions\\MSE',files:42,backupDirectory:'test-backup'};}
  listeners.forEach(cb=>cb({data:{id:r.id,ok:true,result}}));
 })}};
});
try{
 await page.goto(process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18475');
 await page.getByRole('button',{name:'工具',exact:true}).click();
 await expect(page.locator('.tools-servers').getByRole('button',{name:'亡者世界',exact:false})).toBeDisabled();
 await expect(page.locator('.tools-servers').getByRole('button',{name:'亡者世界',exact:false})).toContainText('下载后才可以导入');
 await expect(page.getByRole('checkbox',{name:'游戏设置',exact:true})).toBeChecked();
 await expect(page.getByRole('checkbox',{name:'地图与标点',exact:true})).toBeChecked();
 await expect(page.getByRole('checkbox',{name:'存档与截图',exact:true})).not.toBeChecked();
 await page.getByRole('button',{name:'选择旧客户端'}).click();
 await expect(page.getByRole('status')).toContainText('已导入 42 个文件');
 expect(await page.evaluate(()=>window.importRequests.at(-1))).toEqual({instance:'m3e',categories:['settings','maps']});
 await page.screenshot({path:'../.local/tools-import.png',animations:'disabled'});
 await page.locator('.tools-servers').getByRole('button',{name:'肉丸工艺',exact:true}).click();
 await expect(page.getByRole('status')).toHaveCount(0);
 await page.getByRole('checkbox',{name:'资源包与光影',exact:true}).check();
 await page.getByRole('button',{name:'选择旧客户端'}).click();
 expect(await page.evaluate(()=>window.importRequests.at(-1))).toEqual({instance:'mb',categories:['settings','maps','packs']});
 await page.evaluate(()=>{window.importCancel=true;});
 await page.getByRole('button',{name:'选择旧客户端'}).click();
 await expect(page.getByRole('status')).toHaveCount(0);
 await page.setViewportSize({width:820,height:650});
 expect(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth)).toBe(false);
 await page.goto((process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18475')+'?empty');
 await page.getByRole('button',{name:'工具',exact:true}).click();
 await expect(page.locator('.tools-servers button:disabled')).toHaveCount(3);
 await expect(page.getByRole('button',{name:'下载后才可以导入',exact:true})).toBeDisabled();
 expect(errors).toEqual([]);console.log('Personal import tool passed: server, categories, result, cancellation and layout.');
}finally{await browser.close();}
