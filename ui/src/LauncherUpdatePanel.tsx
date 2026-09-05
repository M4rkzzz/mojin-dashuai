import {createContext,useContext,useEffect,useRef,useState,type ReactNode} from 'react';
import {createPortal} from 'react-dom';
import {Download,RefreshCw,X} from 'lucide-react';
import {invoke,isNative,subscribe} from './bridge';

type Status={phase:string;version?:string;downloaded?:number;total?:number;error?:string};
function useUpdateController(){
 const [state,setState]=useState<Status>({phase:'idle'}),[error,setError]=useState(''),[restarting,setRestarting]=useState(false);
 const revision=useRef(0);
 const checking=useRef(false);
 const receive=(next:Status)=>setState(previous=>next.phase==='checking'?{...next,version:next.version||previous.version}:next);
 useEffect(()=>{
  if(!isNative)return;
  let disposed=false;
  const initialRevision=revision.current;
  const off=subscribe(d=>{if(d.event==='launcher-update'&&d.data?.phase){revision.current++;receive(d.data);setError('');}});
  invoke<Status>('launcher.update.status').then(s=>{if(!disposed&&revision.current===initialRevision&&s?.phase)receive(s);}).catch(()=>{});
  return()=>{disposed=true;off();};
 },[]);
 async function check(){if(checking.current)return;checking.current=true;setError('');setState(previous=>({...previous,phase:'checking'}));const before=revision.current;try{const s=await invoke<Status>('launcher.update.check');if(revision.current===before&&s?.phase)receive(s);}catch(e){setState(previous=>({...previous,phase:'failed'}));setError((e as Error).message);}finally{checking.current=false;}}
 async function restart(){setRestarting(true);setError('');try{await invoke('launcher.update.restart');}catch(e){setError((e as Error).message);}finally{setRestarting(false);}}
 const busy=state.phase==='checking'||state.phase==='downloading'||restarting;
 const label=state.phase==='checking'?'正在检查':state.phase==='downloading'?`正在下载 ${Math.min(100,Math.floor((state.downloaded||0)/Math.max(1,state.total||0)*100))}%`:state.phase==='ready'?`${state.version} 已就绪`:state.phase==='current'?'暂无更新':state.phase==='failed'?'更新暂不可用':'';
 return {state,error,restarting,busy,label,check,restart};
}
const UpdateContext=createContext<ReturnType<typeof useUpdateController>|null>(null);
export function LauncherUpdateProvider({children}:{children:ReactNode}){
 const controller=useUpdateController();
 return <UpdateContext.Provider value={controller}>{children}</UpdateContext.Provider>;
}
function useUpdate(){const controller=useContext(UpdateContext);if(!controller)throw new Error('Missing launcher update provider');return controller;}
export function LauncherUpdateNotice(){
 const {state,busy,check}=useUpdate();
 const [open,setOpen]=useState(false);
 const trigger=useRef<HTMLButtonElement>(null),panel=useRef<HTMLDivElement>(null);
 const available=!!state.version&&['checking','downloading','ready','failed'].includes(state.phase);
 useEffect(()=>{
  if(!open)return;
  panel.current?.focus();
  function key(e:KeyboardEvent){if(e.key==='Escape'){setOpen(false);trigger.current?.focus();}}
  function outside(e:PointerEvent){if(!panel.current?.contains(e.target as Node)&&!trigger.current?.contains(e.target as Node))setOpen(false);}
  document.addEventListener('keydown',key);document.addEventListener('pointerdown',outside);
  return()=>{document.removeEventListener('keydown',key);document.removeEventListener('pointerdown',outside);};
 },[open]);
 if(!isNative)return null;
 return <><button ref={trigger} className={`window-update${available?' available':''}`} aria-expanded={open} aria-busy={busy} aria-controls="window-update-panel" onClick={()=>{setOpen(value=>!value);if(!open&&!busy&&!available)void check();}}>检查更新</button>{open&&createPortal(<div ref={panel} id="window-update-panel" className="window-update-panel" role="dialog" aria-label="启动器更新" tabIndex={-1}><button className="update-dismiss" aria-label="关闭更新面板" onClick={()=>{setOpen(false);trigger.current?.focus();}}><X size={16}/></button><LauncherUpdatePanel/></div>,document.body)}</>;
}
export function LauncherUpdatePanel(){
 const {state,error,restarting,busy,label,check,restart}=useUpdate();
 return <div className="launcher-update"><div className="field-row"><div><b>启动器更新</b><span className="update-state" role="status">{label}</span></div><div className="field-control">{state.phase==='ready'&&<button className="primary" disabled={busy} onClick={restart}><Download size={15}/>{restarting?'正在重启':'重启更新'}</button>}<button className="secondary" disabled={!isNative||busy} onClick={check}><RefreshCw size={15} className={busy?'spin':''}/>检查更新</button></div></div>{(error||state.error)&&<p className="update-error" role="alert">{error||state.error}</p>}</div>;
}
