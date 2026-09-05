import {chromium,expect} from '@playwright/test';

const browser=await chromium.launch({headless:true});
const page=await browser.newPage({viewport:{width:1280,height:820},reducedMotion:'reduce'});
const errors=[];page.on('pageerror',error=>errors.push(error.message));
await page.addInitScript(()=>{
 const listeners=[];let repair=null;window.importCalls=[];window.failSettings=false;
 const settings={root:'D:\\魔金大帅\\content',contentDirectoryConfigured:true,memory:{m3e:8192,dc2:8192,mb:8736,vw:4096},java:{m3e:'',dc2:'',mb:'',vw:''},jvm:{m3e:'',dc2:'',mb:'',vw:''},width:1280,height:720,fullscreen:false,windowBehavior:'keep',concurrency:4,limitMiB:0,proxy:'',proxyMode:'direct',skinSource:'account',preferDedicatedGpu:true,reducedMotion:true,theme:'dark',selectedRoutes:{m3e:'auto',dc2:'auto',mb:'auto',vw:'auto'}};
 const installs={m3e:{version:'fixture',state:'installed'},dc2:{version:'fixture',state:'running'}},states={m3e:'installed',dc2:'running',mb:'not-installed'},progress={};
 const emit=data=>listeners.forEach(callback=>callback({data}));
 const respond=(r,result)=>emit({id:r.id,ok:true,result});
 window.repairStage=(phase,completed,total)=>{progress.m3e={instance:'m3e',phase,completed,total,bytesPerSecond:0};states.m3e='downloading';emit({event:'progress',data:progress.m3e});};
 window.finishRepair=healthy=>{
  const summary={checkedFiles:4,restoredFiles:healthy?0:1,repairedFiles:healthy?0:1,removedFiles:0,runtimePrepared:false};
  const result={instance:'m3e',summary,message:healthy?'检查完成，4 个文件完整。':'已检查 4 个文件，补齐 1 个文件，修复 1 个文件。'};
  delete progress.m3e;states.m3e='installed';emit({event:'repair-result',data:result});emit({event:'installed',data:{instance:'m3e'}});respond(repair,result);repair=null;
 };
 window.chrome={webview:{addEventListener:(_,callback)=>listeners.push(callback),postMessage:r=>queueMicrotask(()=>{
  let result=null;
  if(r.command==='bootstrap')result={profile:{id:'fixture',gameName:'Player',kind:'hub'},settings,installs,states,progress};
  if(r.command==='instances.status')result={installs,states,progress};
  if(r.command==='routes.probe')result=[30,60];
  if(r.command==='instance.repair'){repair=r;window.repairStage('检查本地文件',2,4);return;}
  if(r.command==='instance.import')window.importCalls.push(r.args);
  if(r.command==='settings.save'){if(window.failSettings)return emit({id:r.id,ok:false,error:'测试网络异常'});Object.assign(settings,r.args);}
  respond(r,result);
 })}};
});
try{
 await page.goto(process.env.LAUNCHER_UI_URL||'http://127.0.0.1:18475');
 await page.getByRole('button',{name:'启动器设置',exact:true}).click();
 await expect(page.getByRole('switch',{name:'优先独立显卡',exact:true})).toBeChecked();
 await page.getByRole('button',{name:'文件与内容',exact:true}).click();
 const panel=page.locator('.maintenance-panel');
 await expect(panel.getByRole('button',{name:'打开',exact:true})).toHaveCount(0);
 await panel.getByRole('button',{name:'检查并修复',exact:true}).click();
 await expect(panel.locator('.download-progress')).toContainText('2 / 4 个文件');
 await expect(panel.getByRole('button',{name:'暂停',exact:true})).toBeVisible();
 await page.evaluate(()=>window.repairStage('应用更新',1,2));
 await expect(panel.locator('.download-progress')).toContainText('1 / 2 个文件');
 await expect(panel.getByRole('button',{name:'暂停',exact:true})).toHaveCount(0);
 await expect(panel.getByRole('button',{name:'取消',exact:true})).toHaveCount(0);
 await page.evaluate(()=>window.finishRepair(false));
 await expect(panel.locator('.repair-result')).toContainText('补齐 1 个文件，修复 1 个文件');
 await expect(panel.getByRole('button',{name:'检查并修复',exact:true})).toBeEnabled();
 await panel.getByRole('button',{name:'检查并修复',exact:true}).click();await page.evaluate(()=>window.finishRepair(true));
 await expect(panel.locator('.repair-result')).toContainText('4 个文件完整');
 await page.screenshot({path:'../.local/beta9-maintenance.png',animations:'disabled'});
 await page.getByRole('button',{name:'关闭提示',exact:true}).click();
 await expect(panel.getByText('导入旧客户端配置',{exact:true})).toHaveCount(0);
 await page.getByRole('button',{name:'工具',exact:true}).click();
 await expect(page.getByRole('heading',{name:'导入旧客户端配置',exact:true})).toBeVisible();
 await expect(page.getByRole('checkbox')).toHaveCount(4);
 await expect(page.locator('.tools-servers').getByRole('button',{name:'亡者世界',exact:false})).toBeDisabled();
 await expect(page.locator('.tools-servers')).toContainText('请先关闭游戏');
 await page.getByRole('button',{name:'选择旧客户端',exact:true}).click();
 await expect(page.getByRole('status')).toHaveCount(0);
 expect(await page.evaluate(()=>window.importCalls.at(-1))).toEqual({instance:'m3e',categories:['settings','maps']});
 await page.getByRole('button',{name:'启动器设置',exact:true}).click();await page.getByRole('button',{name:'文件与内容',exact:true}).click();
 await page.locator('.instance-picker').getByRole('button',{name:'亡者世界',exact:true}).click();
 await expect(page.locator('.maintenance-panel').getByRole('button',{name:'管理',exact:true})).toBeDisabled();
 await expect(page.locator('.maintenance-panel').getByRole('button',{name:'检查并修复',exact:true})).toBeDisabled();
 await page.locator('.instance-picker').getByRole('button',{name:'肉丸工艺',exact:true}).click();
 await expect(page.locator('.maintenance-panel').getByRole('button',{name:'检查并修复',exact:true})).toBeDisabled();
 await page.locator('.mini-world').filter({hasText:'魔法金属'}).click();await page.getByRole('button',{name:'宿迁三网',exact:false}).click();
 await expect(page.getByText('设置已保存',{exact:true})).toHaveCount(0);
 await page.evaluate(()=>{window.failSettings=true;});await page.getByRole('button',{name:'自动选择',exact:true}).click();
 await expect(page.getByRole('alert')).toContainText('测试网络异常');
 await page.getByRole('button',{name:'关闭错误',exact:true}).click();await page.evaluate(()=>{window.failSettings=false;});
 await page.getByRole('button',{name:'启动器设置',exact:true}).click();await page.getByRole('button',{name:'保存设置',exact:true}).click();
 await expect(page.getByText('设置已保存',{exact:true})).toBeVisible();
 expect(errors).toEqual([]);console.log('Maintenance UI passed: repair counts/results, commit controls, tools-only import flow, cancellation, disabled states, GPU default, silent routes and explicit save feedback.');
}finally{await browser.close();}
