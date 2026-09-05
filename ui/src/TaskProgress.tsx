import {useEffect,useState} from 'react';
import {Pause,Play,X} from 'lucide-react';
import type {Progress} from './bridge';

const bytes=(n:number)=>n>1024**3?`${(n/1024**3).toFixed(2)} GB`:`${(n/1024**2).toFixed(1)} MB`;
export function TaskProgress({progress:p,busy,onAction}:{progress:Progress&{observedAt:number};busy:boolean;onAction:(command:string)=>void}){
 const [now,setNow]=useState(Date.now());
 useEffect(()=>{const timer=setInterval(()=>setNow(Date.now()),1000);return()=>clearInterval(timer);},[]);
 const files=['检查本地文件','校验并准备更新','应用更新'].includes(p.phase);
 const ratio=p.total>0?Math.max(0,Math.min(100,p.completed/p.total*100)):0;
 const speed=!p.paused&&now-p.observedAt<=3000?p.bytesPerSecond:0;
 const downloading=p.phase.includes('下载'),committing=p.phase==='应用更新'&&!p.paused;
 return <div className="download-progress" role="status"><div><b>{p.phase}</b>{p.total>0&&<span>{ratio.toFixed(0)}%</span>}</div><progress max="100" value={p.total>0?ratio:undefined}/>{p.total>0&&<p>{files?`${p.completed.toLocaleString()} / ${p.total.toLocaleString()} 个文件`:`${bytes(p.completed)} / ${bytes(p.total)}`}</p>}{p.paused?<small>已暂停</small>:downloading&&<small>{bytes(speed)} / s</small>}{!committing&&<div className="download-actions"><button className="secondary" disabled={p.paused&&busy} onClick={()=>onAction(p.paused?'download.resume':'download.pause')}>{p.paused?<Play size={15}/>:<Pause size={15}/>} {p.paused?(downloading?'继续下载':'继续'):(downloading?'暂停下载':'暂停')}</button><button className="secondary" onClick={()=>onAction('download.cancel')}><X size={15}/>取消</button></div>}</div>;
}
