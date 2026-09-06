import {useLayoutEffect,useRef,useState,type ReactNode} from 'react';

/** Keep every login/register action on screen without introducing a scrollbar. */
export function LoginPanelFit({children}:{children:ReactNode}){
 const panel=useRef<HTMLDivElement>(null),[scale,setScale]=useState(1);
 useLayoutEffect(()=>{
  const element=panel.current!,layout=element.parentElement!;
  function fit(){
   const style=getComputedStyle(layout);
   const height=layout.clientHeight-parseFloat(style.paddingTop)-parseFloat(style.paddingBottom);
   const width=layout.clientWidth-parseFloat(style.paddingLeft)-parseFloat(style.paddingRight);
   const next=Math.min(1,Math.max(1,height)/Math.max(1,element.offsetHeight),Math.max(1,width)/Math.max(1,element.offsetWidth));
   setScale(current=>Math.abs(current-next)>.001?next:current);
  }
  const observer=new ResizeObserver(fit);observer.observe(layout);observer.observe(element);fit();
  return()=>observer.disconnect();
 },[]);
 return <div ref={panel} className="login-panel-fit" style={{transform:`scale(${scale})`}}>{children}</div>;
}
