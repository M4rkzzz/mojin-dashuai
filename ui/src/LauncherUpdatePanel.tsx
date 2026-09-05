import {useEffect,useState} from 'react';
import {Download,RefreshCw} from 'lucide-react';
import {invoke,isNative,subscribe} from './bridge';

type Status={phase:string;version?:string;downloaded?:number;total?:number;error?:string};
export function LauncherUpdatePanel(){
 const [state,setState]=useState<Status>({phase:'idle'}),[error,setError]=useState(''),[restarting,setRestarting]=useState(false);
 useEffect(()=>{
  if(!isNative)return;
  let disposed=false;
  const off=subscribe(d=>{if(d.event==='launcher-update'&&d.data?.phase)setState(d.data);});
  invoke<Status>('launcher.update.status').then(s=>{if(!disposed&&s?.phase)setState(s);}).catch(()=>{});
  return()=>{disposed=true;off();};
 },[]);
 async function check(){setError('');try{const s=await invoke<Status>('launcher.update.check');if(s?.phase)setState(s);}catch(e){setError((e as Error).message);}}
 async function restart(){setRestarting(true);setError('');try{await invoke('launcher.update.restart');}catch(e){setError((e as Error).message);}finally{setRestarting(false);}}
 const busy=state.phase==='checking'||state.phase==='downloading'||restarting;
 const label=state.phase==='checking'?'正在检查':state.phase==='downloading'?`正在下载 ${Math.min(100,Math.floor((state.downloaded||0)/Math.max(1,state.total||0)*100))}%`:state.phase==='ready'?`${state.version} 已就绪`:state.phase==='current'?'暂无更新':state.phase==='failed'?'更新暂不可用':'';
 return <div className="launcher-update"><div className="field-row"><div><b>启动器更新</b><span className="update-state" role="status">{label}</span></div><div className="field-control">{state.phase==='ready'&&<button className="primary" disabled={busy} onClick={restart}><Download size={15}/>{restarting?'正在重启':'重启更新'}</button>}<button className="secondary" disabled={!isNative||busy} onClick={check}><RefreshCw size={15} className={busy?'spin':''}/>检查更新</button></div></div>{(error||state.error)&&<p className="update-error" role="alert">{error||state.error}</p>}</div>;
}
