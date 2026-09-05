import {useEffect,useRef} from 'react';
import type {SkinTexture} from './bridge';

export function SkinAvatar({skin,name,large=false}:{skin:SkinTexture|null;name:string;large?:boolean}) {
  const canvas=useRef<HTMLCanvasElement>(null);
  useEffect(()=>{
    const target=canvas.current!;
    const context=target.getContext('2d')!;
    let cancelled=false;
    context.setTransform(10,0,0,10,0,0);
    context.imageSmoothingEnabled=false;
    const fallback=()=>{
      context.clearRect(0,0,16,20);
      context.fillStyle='#777b80';context.fillRect(4,0,8,8);
      context.fillStyle='#35373c';context.fillRect(4,0,8,2);context.fillRect(4,2,1,2);context.fillRect(11,2,1,2);
      context.fillStyle='#161719';context.fillRect(5,4,2,1);context.fillRect(9,4,2,1);context.fillRect(7,6,2,1);
      context.fillStyle='#555a62';context.fillRect(4,8,8,12);context.fillRect(0,8,4,12);context.fillRect(12,8,4,12);
      context.fillStyle='#777b80';context.fillRect(0,17,4,3);context.fillRect(12,17,4,3);
      context.fillStyle='#c1c5cc';context.fillRect(7,9,2,1);context.fillRect(7,12,2,1);context.fillRect(7,15,2,1);
      target.dataset.state='default';
    };
    fallback();
    if(!skin)return;
    const image=new Image();
    image.onload=()=>{
      if(cancelled||image.width<64||image.width>2048||(image.width&(image.width-1))!==0||![image.width/2,image.width].includes(image.height))return;
      context.clearRect(0,0,16,20);
      const scale=image.width/64;
      const modern=image.height===image.width;
      const slim=skin.model==='slim'&&modern;
      const arm=slim?3:4;
      const draw=(sx:number,sy:number,w:number,h:number,x:number,y:number,dw=w,dh=h)=>context.drawImage(image,sx*scale,sy*scale,w*scale,h*scale,x,y,dw,dh);
      draw(20,20,8,12,4,8);
      draw(44,20,arm,12,4-arm,8);
      if(modern) {
        draw(36,52,arm,12,12,8);
        draw(20,36,8,12,4,8);
        draw(44,36,arm,12,4-arm,8);
        draw(52,52,arm,12,12,8);
      } else {
        context.save();context.translate(16,0);context.scale(-1,1);draw(44,20,4,12,0,8);context.restore();
      }
      draw(8,8,8,8,4,0);
      draw(40,8,8,8,3.75,0,8.5,8.25);
      target.dataset.state='skin';
    };
    image.src=`data:image/png;base64,${skin.pngBase64}`;
    return ()=>{cancelled=true;image.onload=null;};
  },[skin]);
  return <canvas ref={canvas} className={`skin-avatar ${large?'large':''}`} width={160} height={200} role="img" aria-label={skin?`${name}的皮肤半身像`:'默认皮肤半身像'}/>;
}
