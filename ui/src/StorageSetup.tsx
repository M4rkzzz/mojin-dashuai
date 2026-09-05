import {useState, type FormEvent} from 'react';
import {ArrowRight, FolderOpen, RefreshCw} from 'lucide-react';

type Props = {
 initialRoot:string;
 busy:boolean;
 choose:()=>Promise<string|null|undefined>;
 confirm:(root:string)=>void;
 logout:()=>void;
};

export function StorageSetup({initialRoot,busy,choose,confirm,logout}:Props){
 const [root,setRoot]=useState(initialRoot);
 async function browse(){const selected=await choose();if(selected)setRoot(selected);}
 function submit(event:FormEvent){event.preventDefault();confirm(root.trim());}
 return <div className="storage-page">
  <header><div className="brand">魔金大帅</div><button className="text-link" disabled={busy} onClick={logout}>切换账号</button></header>
  <main className="storage-layout">
   <form className="storage-panel" onSubmit={submit} aria-labelledby="storage-title">
    <FolderOpen className="storage-mark" size={36} strokeWidth={1.4} aria-hidden="true"/>
    <h1 id="storage-title">游戏文件保存位置</h1>
    <label htmlFor="storage-root">保存到</label>
    <div className="storage-path">
     <input id="storage-root" value={root} onChange={event=>setRoot(event.target.value)} disabled={busy} required spellCheck={false} autoComplete="off"/>
     <button type="button" className="secondary" disabled={busy} onClick={browse}><FolderOpen size={17}/>选择文件夹</button>
    </div>
    <button className="primary storage-confirm" disabled={busy||!root.trim()}>{busy?<><RefreshCw size={17} className="spin"/>正在处理</>:<>确定并进入大厅<ArrowRight size={18}/></>}</button>
   </form>
  </main>
 </div>;
}
