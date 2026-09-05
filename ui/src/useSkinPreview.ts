import {useCallback,useEffect,useRef,useState} from 'react';
import {invoke,isNative,type Profile,type Settings,type SkinLoadResult,type SkinTexture} from './bridge';

export function useSkinPreview(profile:Profile|null,source:Settings['skinSource']) {
  const [result,setResult]=useState<SkinLoadResult|null>(null);
  const [loading,setLoading]=useState(false);
  const request=useRef(0);
  const profileKey=profile?`${profile.kind}:${profile.id}:${profile.gameName}`:'';
  const load=useCallback(async(refresh=false)=>{
    const id=++request.current;
    if(!profileKey||!isNative){setResult(null);setLoading(false);return;}
    setLoading(true);
    try {
      const value=await invoke<SkinLoadResult|null>('account.skin.preview',{refresh});
      if(request.current===id)setResult(value);
    } catch {
      if(request.current===id)setResult(previous=>({texture:previous?.texture||null,status:'error',message:'皮肤获取失败，请稍后刷新。'}));
    } finally {if(request.current===id)setLoading(false);}
  },[profileKey,source]);
  useEffect(()=>{
    setResult(null);void load();
    return()=>{++request.current;};
  },[load]);
  const setTexture=(texture:SkinTexture)=>{
    ++request.current;setLoading(false);setResult({texture,status:'ready'});
  };
  return {skin:result?.texture||null,message:result?.message||'',loading,refresh:()=>load(true),setTexture};
}
