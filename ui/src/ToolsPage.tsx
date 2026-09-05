import {useState} from 'react';
import {FolderOpen,Import,RefreshCw} from 'lucide-react';
import {ServerIcon} from './Brand';

type Result={source:string;files:number;backupDirectory:string};
export function ToolsPage({servers,initialServer,installed,states={},operations={},busy,run}:{servers:{id:string;name:string}[];initialServer:string;installed:Record<string,unknown>;states?:Record<string,string>;operations?:Record<string,boolean>;busy:boolean;run:<T>(command:string,args:unknown)=>Promise<T|undefined>}){
 function unavailable(id:string){return states[id]==='running'?'请先关闭游戏':operations[id]||['preparing','downloading','paused'].includes(states[id])?'请先完成当前任务':!installed[id]?'下载后才可以导入':'';}
 const [server,setServer]=useState(!unavailable(initialServer)?initialServer:servers.find(item=>!unavailable(item.id))?.id||initialServer),[categories,setCategories]=useState(['settings','maps']),[result,setResult]=useState<Result|null>(null);
 function toggle(value:string){setCategories(previous=>previous.includes(value)?previous.filter(item=>item!==value):[...previous,value]);}
 async function start(){setResult(null);const imported=await run<Result|null>('instance.import',{instance:server,categories});if(imported)setResult(imported);}
 return <section className="tools-page">
  <h1>工具</h1>
  <div className="tools-section"><h2>导入旧客户端配置</h2>
   <div className="tools-servers" aria-label="导入到服务器">{servers.map(item=><button key={item.id} type="button" className={`${item.id===server?'selected':''} ${unavailable(item.id)?'not-installed':''}`} aria-pressed={item.id===server} disabled={busy||!!unavailable(item.id)} onClick={()=>{setServer(item.id);setResult(null);}}><ServerIcon id={item.id}/><span>{item.name}{unavailable(item.id)&&<span className="import-unavailable">{unavailable(item.id)}</span>}</span></button>)}</div>
   <div className="import-categories">{[['settings','游戏设置'],['maps','地图与标点'],['packs','资源包与光影'],['worlds','存档与截图']].map(([value,label])=><label key={value}><input type="checkbox" checked={categories.includes(value)} disabled={busy} onChange={()=>toggle(value)}/>{label}</label>)}</div>
   <button className="primary" disabled={busy||!!unavailable(server)||categories.length===0} onClick={start}>{busy?<RefreshCw className="spin" size={18}/>:<Import size={18}/>} {busy?'正在导入':unavailable(server)||'选择旧客户端'}</button>
   {result&&<div className="import-result" role="status"><b>已导入 {result.files} 个文件</b><span>{result.source}</span><button className="secondary" onClick={()=>run('instance.import.backups',{instance:server})}><FolderOpen size={17}/>打开备份</button></div>}
  </div>
 </section>;
}
