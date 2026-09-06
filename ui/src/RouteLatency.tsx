import {useCallback,useEffect,useRef,useState,type ReactNode} from 'react';
import {ArrowRight} from 'lucide-react';
import {invoke,isNative} from './bridge';

export const routeNames=['河北阿里云','宿迁三网'];
type Latencies=[number|null,number|null];
export function useRouteLatencies(instance:string,onError?:(message:string)=>void){
 const [latencies,setLatencies]=useState<Latencies>([null,null]);
 const [onlinePlayers,setOnlinePlayers]=useState<number|null>(null);
 const [checking,setChecking]=useState(false);
 const generation=useRef(0);
 const inFlight=useRef<number|null>(null);
 const errorHandler=useRef(onError);
 useEffect(()=>{errorHandler.current=onError;},[onError]);
 const probe=useCallback(async()=>{
  if(inFlight.current!==null)return;
  const request=++generation.current;inFlight.current=request;setChecking(true);
  try{
   const result=await invoke<unknown>('routes.probe',{instance});
   if(request!==generation.current)return;
   const routes=[0,1].map(index=>{
    const value=Array.isArray(result)?result[index]:null;
    const latency=typeof value==='number'?value:value?.latency;
    return {latency:typeof latency==='number'&&Number.isFinite(latency)?Math.max(-1,Math.round(latency)):-1,
     onlinePlayers:typeof value?.onlinePlayers==='number'&&Number.isSafeInteger(value.onlinePlayers)&&value.onlinePlayers>=0?value.onlinePlayers:null};
   });
   setLatencies(routes.map(route=>route.latency) as Latencies);
   // Both routes lead to the same server. Use one successful response, never add their counts.
   setOnlinePlayers(routes.filter(route=>route.latency>=0&&route.onlinePlayers!==null).sort((a,b)=>a.latency-b.latency)[0]?.onlinePlayers??null);
  }catch(error){if(request===generation.current){setLatencies([-1,-1]);setOnlinePlayers(null);errorHandler.current?.((error as Error).message);}}
  finally{if(request===generation.current){inFlight.current=null;setChecking(false);}}
 },[instance]);
 useEffect(()=>{
  setLatencies([null,null]);
  setOnlinePlayers(null);
  if(!isNative)return;
  void probe();
  return()=>{generation.current++;inFlight.current=null;};
 },[instance,probe]);
 return {latencies,onlinePlayers,checking,probe};
}

export function LatencySignal({latency,checking=false}:{latency:number|null;checking?:boolean}){
 const level=latency===null?'pending':latency<0?'offline':latency<100?'good':latency<200?'fair':'poor';
 const bars=level==='good'?3:level==='fair'?2:level==='poor'?1:0;
 return <span className={`route-latency latency-${level}`}>
  <svg className="latency-signal" viewBox="0 0 18 16" width="18" height="16" fill="currentColor" aria-hidden="true">
   {[0,1,2].map((bar)=><rect key={bar} x={bar*6+1} y={11-bar*4} width="4" height={4+bar*4} opacity={bar<bars?1:.24}/>)}
   {level==='offline'&&<path d="m2 2 14 12" fill="none" stroke="currentColor" strokeWidth="1.6"/>}
  </svg>
  <span>{latency===null?(checking?'测速中':'未测速'):latency<0?'不可达':`${latency} ms`}</span>
 </span>;
}

export function ServerRouteLatencies({instance,children}:{instance:string;children:ReactNode}){
 const {latencies,onlinePlayers,checking}=useRouteLatencies(instance);
 return <><div className="card-routes" aria-label="线路延迟">{routeNames.map((name,index)=><div className="card-route" key={name}><span>{name}</span><LatencySignal latency={latencies[index]} checking={checking}/></div>)}</div>
  <div className="card-bottom">{children}<span className="card-online" aria-label="在线玩家数量">{onlinePlayers!==null?`在线 ${onlinePlayers} 人`:checking?'人数查询中':'人数暂不可用'}</span><ArrowRight size={19}/></div></>;
}
