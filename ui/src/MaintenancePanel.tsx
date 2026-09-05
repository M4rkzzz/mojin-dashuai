import {useState} from 'react';
import {Check,FolderOpen,RefreshCw} from 'lucide-react';
import {invoke,type Progress,type Settings} from './bridge';
import {TaskProgress} from './TaskProgress';

export type RepairResult={message:string;summary?:{checkedFiles:number;restoredFiles:number;repairedFiles:number;removedFiles:number;runtimePrepared:boolean}};
export function MaintenancePanel({id,name,settings,installed,states,operations,progress,result,busy,act,setSettings,runInstance}:{id:string;name:string;settings:Settings;installed:Record<string,unknown>;states:Record<string,string>;operations:Record<string,boolean>;progress?:Progress&{observedAt:number};result?:RepairResult;busy:boolean;act:(fn:()=>Promise<any>)=>Promise<any>;setSettings:(value:Settings)=>void;runInstance:(id:string,command:string)=>Promise<any>}){
 const [working,setWorking]=useState(''),[messages,setMessages]=useState<Record<string,string>>({});
 const active=!!operations[id]||['preparing','downloading','paused'].includes(states[id]);
 const reason=states[id]==='running'?'请先关闭游戏':active?'请先完成当前任务':!installed[id]?'下载客户端后可用':'';
 const anyActive=Object.values(operations).some(Boolean)||Object.values(states).some(state=>['running','preparing','downloading','paused'].includes(state));
 const globalReason=anyActive?'请先结束游戏和下载任务':'';
 async function action(command:string){setWorking(command);setMessages(previous=>({...previous,[command]:''}));try{await act(async()=>{const value=await invoke(command,{instance:id});if(value?.settings)setSettings(value.settings);if(value?.message&&!value.cancelled&&!value.message.startsWith('已取消'))setMessages(previous=>({...previous,[command]:value.message}));});}finally{setWorking('');}}
 function operation(title:string,command:string,label:string,blocked:string,handler?:()=>void){return <div className="maintenance-row"><div><b>{title}</b>{messages[command]&&<p className="maintenance-result" role="status"><Check size={15}/>{messages[command]}</p>}</div><div className="maintenance-control">{blocked&&<span className="action-reason">{blocked}</span>}<button className="secondary" disabled={busy||!!blocked} onClick={handler||(()=>action(command))}>{working===command?<><RefreshCw size={15} className="spin"/>处理中</>:label}</button></div></div>;}
 return <div className="maintenance-panel">
  <div className="maintenance-row"><b>内容目录</b><div className="maintenance-control content-directory"><input value={settings.root} readOnly aria-label="内容目录"/><button className="secondary" disabled={busy||!!globalReason} onClick={()=>action('directory.migrate')}><FolderOpen size={15}/>{working==='directory.migrate'?'正在迁移':'迁移目录'}</button>{globalReason&&<span className="action-reason">{globalReason}</span>}</div>{messages['directory.migrate']&&<p className="maintenance-result" role="status">{messages['directory.migrate']}</p>}</div>
  {operation('模组与资源','content.manage','管理',reason)}
  <div className="maintenance-row repair-row"><div><b>检查与修复</b></div><div className="maintenance-control">{reason&&<span className="action-reason">{reason}</span>}<button className="secondary" disabled={busy||!!reason} onClick={()=>runInstance(id,'instance.repair')}>{operations[id]?<><RefreshCw size={15} className="spin"/>{progress?.phase==='检查本地文件'?'正在检查':'处理中'}</>:'检查并修复'}</button></div>
   {progress&&<div className="maintenance-progress" aria-label={`${name}当前任务`}><TaskProgress progress={progress} busy={!!operations[id]} onAction={command=>runInstance(id,command)}/></div>}
   {!progress&&result&&<div className="repair-result" role="status"><Check size={18}/><span>{result.message}</span></div>}
  </div>
  {operation('下载缓存','cache.clean','清理缓存',globalReason)}
 </div>;
}
