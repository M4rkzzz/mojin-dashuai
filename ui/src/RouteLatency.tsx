import {useCallback,useEffect,useRef,useState} from 'react';
import {invoke,isNative} from './bridge';

export const routeNames=['河北阿里云','宿迁三网'];
type Latencies=[number|null,number|null];
export function useRouteLatencies(instance:string,autoRefresh=false,onError?:(message:string)=>void){
 const [latencies,setLatencies]=useState<Latencies>([null,null]);
 const [checking,setChecking]=useState(false);
 const generation=useRef(0);
 const probe=useCallback(async()=>{
  const request=++generation.current;setChecking(true);
  try{
   const result=await invoke<unknown>('routes.probe',{instance});
   if(request!==generation.current)return;
   setLatencies([0,1].map(index=>Array.isArray(result)&&typeof result[index]==='number'&&Number.isFinite(result[index])?Math.max(-1,Math.round(result[index])):-1) as Latencies);
  }catch(error){if(request===generation.current){setLatencies([-1,-1]);onError?.((error as Error).message);}}
  finally{if(request===generation.current)setChecking(false);}
 },[instance,onError]);
 useEffect(()=>{
  setLatencies([null,null]);
  if(!isNative)return;
  void probe();
  const timer=autoRefresh?window.setInterval(()=>{if(document.visibilityState==='visible')void probe();},30000):undefined;
  return()=>{generation.current++;if(timer)window.clearInterval(timer);};
 },[instance,autoRefresh,probe]);
 return {latencies,checking,probe};
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

export function ServerRouteLatencies({instance}:{instance:string}){
 const {latencies,checking}=useRouteLatencies(instance,true);
 return <div className="card-routes" aria-label="线路延迟">{routeNames.map((name,index)=><div className="card-route" key={name}><span>{name}</span><LatencySignal latency={latencies[index]} checking={checking}/></div>)}</div>;
}
