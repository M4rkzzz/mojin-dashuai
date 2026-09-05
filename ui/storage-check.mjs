import {chromium,expect} from '@playwright/test';
import fs from 'node:fs';

const browser=await chromium.launch({headless:true});
const errors=[];
const url=process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18473';
async function fixture({restored=false,configured=false}={}){
 const page=await browser.newPage({viewport:{width:1280,height:820},deviceScaleFactor:1.25,reducedMotion:'reduce'});
 page.on('pageerror',error=>errors.push(error.message));
 await page.addInitScript(({restored,configured})=>{
  const initial={profile:restored?{id:'test',loginName:'Player',gameName:'Player',kind:'hub'}:null,settings:{root:'C:\\Games\\魔金大帅',contentDirectoryConfigured:configured,memory:{m3e:8192,dc2:8192,mb:8736},java:{m3e:'',dc2:'',mb:''},jvm:{m3e:'',dc2:'',mb:''},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',reducedMotion:true,theme:'dark',selectedRoutes:{m3e:'auto',dc2:'auto',mb:'auto'}}};
  // Test-only state contains settings and a synthetic profile, never credentials.
  const state=JSON.parse(sessionStorage.getItem('storage-check')||JSON.stringify(initial));
  const listeners=[];
  window.testCommands=[];
  window.testFolder=null;
  const emit=data=>listeners.forEach(callback=>callback({data}));
  window.chrome={webview:{addEventListener:(_,callback)=>listeners.push(callback),postMessage:request=>queueMicrotask(()=>{
   window.testCommands.push(request.command);
   let result=null;
   if(request.command==='bootstrap')result={...state,installs:{}};
   if(['auth.login','auth.register','auth.microsoft'].includes(request.command)){
    state.profile={id:'test',loginName:'Player',gameName:'Player',kind:request.command==='auth.microsoft'?'microsoft':'hub'};
    result={profile:state.profile,...(request.command==='auth.register'?{recoveryCode:'TEST-RECOVERY-CODE'}:{})};
   }
   if(request.command==='auth.logout')state.profile=null;
   if(request.command==='account.password'){
    state.profile=null;emit({event:'account-signed-out',data:null});result={message:'密码已修改，请重新登录。'};
   }
   if(request.command==='directory.choose')result=window.testFolder;
   if(request.command==='directory.initialize'){
    if(request.args.root==='X:\\blocked')return emit({id:request.id,ok:false,error:'没有写入这个文件夹的权限，请选择其他位置。'});
    state.settings={...state.settings,root:request.args.root,contentDirectoryConfigured:true};
    result=state.settings;
   }
   sessionStorage.setItem('storage-check',JSON.stringify(state));
   emit({id:request.id,ok:true,result});
  })}};
 },{restored,configured});
 await page.goto(url);
 return page;
}
async function signIn(page,method='hub'){
 if(method==='microsoft')return page.getByRole('button',{name:'使用微软正版账号登录'}).click();
 if(method==='register')await page.getByRole('button',{name:'使用邀请码注册'}).click();
 await page.getByLabel('登录账号',{exact:true}).fill('Player');
 await page.getByLabel('密码',{exact:true}).fill('Testing-password-2026');
 if(method==='register'){
  await page.getByLabel('游戏名',{exact:true}).fill('Player');
  await page.getByLabel('邀请码',{exact:true}).fill('TEST-INVITE');
  await page.getByRole('button',{name:'注册并进入大厅'}).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button',{name:'我已安全保存'}).click();
 }else await page.getByRole('button',{name:'进入大厅',exact:true}).click();
}
try{
 for(const method of ['hub','microsoft','register']){
  const page=await fixture();
  await expect(page.getByRole('button',{name:'进入大厅',exact:true})).toBeEnabled();
  expect(await page.evaluate(()=>window.testCommands.includes('directory.choose'))).toBe(false);
  await signIn(page,method);
  await expect(page.getByRole('heading',{name:'游戏文件保存位置'})).toBeVisible();
  await expect(page.locator('.sidebar')).toHaveCount(0);
  await page.getByRole('button',{name:'选择文件夹',exact:true}).click();
  await expect(page.getByLabel('保存到',{exact:true})).toHaveValue('C:\\Games\\魔金大帅');
  await expect(page.getByRole('heading',{name:'游戏文件保存位置'})).toBeVisible();
  if(method==='hub'){
   for(const [width,height] of [[1280,820],[820,650],[640,600]]){
    await page.setViewportSize({width,height});
    expect(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth||document.documentElement.scrollHeight>innerHeight)).toBe(false);
    await expect(page.getByRole('button',{name:'确定并进入大厅'})).toBeInViewport();
   }
   await page.setViewportSize({width:1280,height:820});
   await page.screenshot({path:'../.local/storage-setup.png',animations:'disabled'});
   await page.getByLabel('保存到',{exact:true}).fill('X:\\blocked');
   await page.getByRole('button',{name:'确定并进入大厅'}).click();
   await expect(page.getByRole('alert')).toContainText('没有写入');
   await expect(page.getByRole('heading',{name:'游戏文件保存位置'})).toBeVisible();
   await page.getByRole('button',{name:'关闭错误'}).click();
  }
  await page.evaluate(()=>{window.testFolder='D:\\Minecraft\\魔金大帅';});
  await page.getByRole('button',{name:'选择文件夹',exact:true}).click();
  await expect(page.getByLabel('保存到',{exact:true})).toHaveValue('D:\\Minecraft\\魔金大帅');
  await page.getByLabel('保存到',{exact:true}).press('Enter');
  await expect(page.getByRole('heading',{name:'选择服务器'})).toBeVisible();
  expect(await page.evaluate(()=>window.testCommands.some(command=>command.startsWith('instance.')||command.startsWith('download.')))).toBe(false);
  await page.reload();
  await expect(page.getByRole('heading',{name:'选择服务器'})).toBeVisible();
  await page.getByRole('button',{name:'启动器设置'}).click();
  await page.getByRole('button',{name:'文件与内容',exact:true}).click();
  await expect(page.locator('input[readonly]')).toHaveValue('D:\\Minecraft\\魔金大帅');
  await page.close();
 }
 const resumed=await fixture({restored:true});
 await expect(resumed.getByRole('heading',{name:'游戏文件保存位置'})).toBeVisible();
 await resumed.getByRole('button',{name:'切换账号'}).click();
 await signIn(resumed);
 await expect(resumed.getByRole('heading',{name:'游戏文件保存位置'})).toBeVisible();
 await resumed.close();
 const existing=await fixture({restored:true,configured:true});
 await expect(existing.getByRole('heading',{name:'选择服务器'})).toBeVisible();
 expect(await existing.evaluate(()=>window.testCommands.includes('directory.choose'))).toBe(false);
 await existing.getByRole('button',{name:'启动器设置'}).click();
 await existing.getByRole('button',{name:'账号与诊断',exact:true}).click();
 await existing.locator('.field-row').filter({hasText:'修改密码'}).getByRole('button',{name:'修改密码',exact:true}).click();
 await expect(existing.getByRole('heading',{name:'登录',exact:true})).toBeVisible();
 await expect(existing.getByRole('button',{name:'启动器设置'})).toHaveCount(0);
 await existing.close();
 expect(errors).toEqual([]);
 fs.writeFileSync('../.local/storage-check.json',JSON.stringify({passed:true,loginMethods:['hub','microsoft','register'],cancel:true,writeError:true,restart:true,existingUser:true,viewports:[1280,820,640],nativeFolderDialogVerified:false,errors},null,2));
 console.log('First-login directory flow passed: login methods, cancellation, errors, persistence and layout.');
}finally{await browser.close();}
