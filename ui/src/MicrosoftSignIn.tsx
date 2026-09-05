import {useEffect, useRef, useState} from 'react';
import {ArrowUpRight, Check, Copy, RefreshCw} from 'lucide-react';
import type {MicrosoftCode} from './bridge';

export function MicrosoftSignIn({code,mode,copy,open,cancel,expired}:{code:MicrosoftCode|null;mode:'window'|'device-code'|null;copy:()=>Promise<void>;open:()=>Promise<void>;cancel:()=>Promise<void>;expired:()=>void}){
 const [now,setNow]=useState(Date.now()),[copied,setCopied]=useState(false),[cancelling,setCancelling]=useState(false),[error,setError]=useState('');
 const expiration=useRef(expired);expiration.current=expired;
 const seconds=code?Math.max(0,Math.ceil((Date.parse(code.expiresAt)-now)/1000)):null;
 const expiredOnce=useRef(false);
 useEffect(()=>{const timer=setInterval(()=>setNow(Date.now()),1000);return()=>clearInterval(timer);},[]);
 useEffect(()=>{if(code&&seconds===0&&!expiredOnce.current){expiredOnce.current=true;expiration.current();}},[code,seconds]);
 async function stop(){if(cancelling)return;setCancelling(true);try{await cancel();}catch(e){setError((e as Error).message);setCancelling(false);}}
 const stopRef=useRef(stop);stopRef.current=stop;
 useEffect(()=>{const onKey=(e:KeyboardEvent)=>{if(e.key==='Escape'){e.preventDefault();void stopRef.current();}};window.addEventListener('keydown',onKey);return()=>window.removeEventListener('keydown',onKey);},[]);
 async function action(fn:()=>Promise<void>){setError('');try{await fn();}catch(e){setError((e as Error).message);}}
 return <section className="login-panel microsoft-login" aria-labelledby="microsoft-heading">
  <h1 id="microsoft-heading">微软登录</h1>
  {code?<>
   <div className="device-code-label">登录码</div>
   <div className="device-code-row"><output aria-label="微软登录码">{code.userCode}</output><button aria-label={copied?'已复制登录码':'复制登录码'} disabled={cancelling||seconds===0} onClick={()=>action(async()=>{await copy();setCopied(true);})}>{copied?<Check size={19}/>:<Copy size={19}/>}</button></div>
   <button className="primary wide device-open" disabled={cancelling||seconds===0} onClick={()=>action(open)}>打开微软登录页<ArrowUpRight size={18}/></button>
   <div className="device-status" role="status"><RefreshCw size={17} className="spin"/><span>{cancelling?'正在取消':'等待授权'}</span><time aria-label="登录码剩余时间">{Math.floor((seconds??0)/60)}:{String((seconds??0)%60).padStart(2,'0')}</time></div>
  </>:<div className="device-status" role="status"><RefreshCw size={18} className="spin"/>{cancelling?'正在取消':mode==='window'?'请在微软窗口中完成登录':mode==='device-code'?'正在获取登录码':'正在连接微软'}</div>}
  {error&&<p className="device-error" role="alert">{error}</p>}
  <button className="secondary wide" autoFocus disabled={cancelling} onClick={()=>void stop()}>取消登录</button>
 </section>;
}
