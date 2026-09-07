import {useEffect,useRef,useState} from 'react';
import {Check,Gift,RefreshCw,Ticket,Medal,ArrowRight} from 'lucide-react';
import {invoke,type Profile,type SkinTexture} from './bridge';
import {ServerIcon} from './Brand';
import {SkinAvatar} from './SkinAvatar';
import itemIcons from './activity-icons.json';
import './activities.css';

type World={id:string;name:string;image:string;color:string};
type Item={id:string;meta:number;count:number;nbt:string};
type Reward={id:string;name:string;tier:string;purpose:string;items:Item[];eligible:boolean;eligibility?:string;basisPoints:number};
type Award={id:string;tier:string;source:string;createdAt:string;name?:string;status:string;items:Item[];choices:Reward[]};
type ActivityView={instance:string;name:string;dailyName:string;weeklyName:string;monthlyName:string;today:string;week:string;stage:string;lastSeen?:string;
 tickets:number;medals:number;misses:number;guaranteeWithin:number;actions:{id:string;name:string;description:string;current:number;count:number;eligible:boolean}[];
 dailyReady:boolean;claimedIn?:string;pendingDays:string[];weeklySteps:{label:string;done:boolean}[];weeklyDays:number;weeklyReady:boolean;weeklyClaimed:boolean;pendingWeeks:string[];
 awards:Award[];pool:Reward[];cosmetics:string[];equippedTitle?:string;equippedFrame?:string;equippedBackground?:string;
 showcases:{id:string;text:string;stage:string;month:string;gameName:string}[];myShowcase?:string;resultAwardId?:string};
const rarity:Record<string,string>={daily:'签到补给',ordinary:'普通补给',selected:'精选补给',rare:'稀有推进奖励'};
const status:Record<string,string>={pending:'待选择／解锁',queued:'待入服领取',delivered:'已送入背包'};
const cosmetic=[{id:'title',name:'主题称号',price:8},{id:'frame',name:'纪念头像框',price:20},{id:'background',name:'个人展示背景',price:40}];
const icons=itemIcons as Record<string,{src:string;name:string;block:boolean;overlay:string|null;atlas?:{x:number;y:number;width:number;height:number;size:number}}>;
function RewardItems({world,items}:{world:string;items:Item[]}){
 return <div className="activity-items">{items.map((item,index)=>{const icon=icons[`${world}:${item.id}@${item.meta}`];return <div className="activity-item" key={`${item.id}-${item.meta}-${index}`}>
  {icon&&<span className={`item-texture ${icon.block?'item-block':''}`} aria-hidden="true">{icon.atlas?<span style={{display:'block',width:24,height:24*icon.atlas.height/icon.atlas.width,backgroundImage:`url('${icon.src}')`,backgroundSize:24*icon.atlas.size/icon.atlas.width,backgroundPosition:`${-24*icon.atlas.x/icon.atlas.width}px ${-24*icon.atlas.y/icon.atlas.width}px`,imageRendering:'pixelated'}}/>:icon.block?<span className="item-cube">{['top','left','right'].map(face=><span key={face} className={`cube-${face}`} style={{backgroundImage:`url('${icon.src}')`}}>{icon.overlay&&<img src={icon.overlay} alt=""/>}</span>)}</span>:<span className="item-flat"><img src={icon.src} alt=""/>{icon.overlay&&<img className="flat-overlay" src={icon.overlay} alt=""/>}</span>}</span>}
  <span>{icon?.name||'材料'} <b>×{item.count}</b></span></div>;})}</div>;
}

export function ActivitiesPage({worlds,initialWorld,profile,skin=null}:{worlds:World[];initialWorld:string;profile:Profile|null;skin?:SkinTexture|null}){
 const [worldId,setWorldId]=useState(initialWorld),[tab,setTab]=useState<'daily'|'draw'|'shop'|'showcase'>('daily');
 const [data,setData]=useState<ActivityView|null>(null),[busy,setBusy]=useState(false),[error,setError]=useState(''),[message,setMessage]=useState(''),[text,setText]=useState('');
 const [reveal,setReveal]=useState<Award|null>(null),[drawing,setDrawing]=useState(false);
 const loaded=useRef(false);
 const generation=useRef(0),mutation=useRef(false),reading=useRef<number|null>(null),revision=useRef(0),knownReady=useRef(false),world=worlds.find(w=>w.id===worldId)!;
 async function load(){const stamp=generation.current,rev=revision.current;if(mutation.current||reading.current===stamp)return;reading.current=stamp;try{const next=await invoke<ActivityView>('activities',{instance:worldId});if(stamp===generation.current&&rev===revision.current){if(next.dailyReady&&!next.claimedIn&&!knownReady.current&&loaded.current)setMessage('今日行动已完成，可以领取签到。');knownReady.current=next.dailyReady;loaded.current=true;setData(next);setError('');}}catch(e){if(stamp===generation.current&&rev===revision.current)setError((e as Error).message);}finally{if(reading.current===stamp)reading.current=null;}}
 useEffect(()=>{generation.current++;revision.current++;setData(null);setError('');setMessage('');setReveal(null);knownReady.current=false;loaded.current=false;load();const timer=setInterval(()=>{if(!document.hidden)load();},15000);return()=>{generation.current++;clearInterval(timer);};},[worldId,profile?.id]);
 async function run(action:string,args:Record<string,string>={}){
  if(mutation.current)return;mutation.current=true;revision.current++;setBusy(true);setDrawing(action==='draw');setReveal(null);setError('');setMessage('');const stamp=generation.current;
  const key='mojin.activity.operation:'+profile?.kind+':'+profile?.id+':'+worldId+':'+action+':'+JSON.stringify(args);
  // A lost response must retry the same draw/purchase instead of spending again.
  const operationId=localStorage.getItem(key)||crypto.randomUUID();localStorage.setItem(key,operationId);
  try{const next=await invoke<ActivityView>('activities',{instance:worldId,action,operationId,...args});localStorage.removeItem(key);if(stamp===generation.current){setData(next);if(action==='draw')setReveal(next.awards.find(a=>a.id===next.resultAwardId)||null);setMessage(action==='draw'?'':action==='daily'?'签到已领取：抽奖券 +1，纪念章 +1。':action==='weekly'?'周奖励已领取：抽奖券 +2，纪念章 +3。':action==='showcase'?'已提交，审核通过后展示。':action==='select'?'奖励已保留，入服后自动放入背包。':action==='buy'?'兑换成功，已为你使用。':'已使用');}}
  catch(e){if(stamp===generation.current)setError((e as Error).message);}finally{mutation.current=false;setBusy(false);setDrawing(false);}
 }
 return <section className="activities" style={{'--activity-accent':world.color} as React.CSSProperties}>
  <header className="activities-hero" style={{backgroundImage:`linear-gradient(90deg,#0b100ef5,#0b100e66),url('./scenes/${world.image}')`}}>
   <div><span className="eyebrow">{world.name} · 日常与成长</span><h1>活动</h1><p>{data?.stage||world.name} · 游戏内行动自动记录</p></div>
   <div className="activity-balances"><span><Ticket size={18}/>{data?.tickets??'—'} 张抽奖券</span><span><Medal size={18}/>{data?.medals??'—'} 枚纪念章</span></div>
  </header>
  <div className="activity-worlds">{worlds.map(w=><button disabled={busy} key={w.id} className={w.id===worldId?'selected':''} onClick={()=>setWorldId(w.id)}><ServerIcon id={w.id}/>{w.name}</button>)}</div>
  <nav className="activity-tabs">{([['daily','签到与周活动'],['draw','抽奖与奖励'],['shop','纪念兑换'],['showcase','玩家展示']] as const).map(([id,name])=><button key={id} className={tab===id?'selected':''} onClick={()=>setTab(id)}>{name}</button>)}<button className="activity-refresh" aria-label="刷新活动进度" disabled={busy} onClick={load}><RefreshCw size={15}/></button></nav>
  {error&&<p className="activity-error" role="alert">{error} <button onClick={load}>重试</button></p>}
  {message&&<p className="activity-notice" role="status"><Check size={16}/>{message}</p>}
  {!data?<p role="status">{error?'暂时无法读取活动，已获得的进度和奖励会保留。':'正在读取活动进度…'}</p>:<>
   {tab==='daily'&&<div className="activity-columns"><article className="activity-panel"><h2>{data.dailyName}</h2><p>以下行动完成一项即可签到。每天选择一个服领取，无需连续打卡。</p>
    {!data.lastSeen&&<p className="activity-muted">首次进入对应服务器后同步任务进度。</p>}
    <div className="daily-actions">{data.actions.map(a=><div key={a.id} className={!a.eligible?'locked':''}><div><strong>{a.name}</strong><span>{a.current} / {a.count}</span></div><p>{a.eligible?a.description:'达到对应游戏进度后开放。'}</p><progress aria-label={a.name} value={a.current} max={a.count}/></div>)}</div>
    <button className="primary" disabled={busy||!data.dailyReady||!!data.claimedIn} onClick={()=>run('daily',{period:data.today})}>{data.claimedIn?`今天已在${data.claimedIn}领取`:'领取今日签到'}<Gift size={17}/></button><small>小份补给 · 抽奖券 ×1 · 纪念章 ×1</small>
    {data.pendingDays.filter(d=>d!==data.today).length>0&&<details><summary>以前完成的签到尚未领取</summary>{data.pendingDays.filter(d=>d!==data.today).map(d=><button disabled={busy} key={d} onClick={()=>run('daily',{period:d})}>{d} 领取</button>)}</details>}
   </article><article className="activity-panel"><h2>{data.weeklyName}</h2><p>依次完成三步，或在本服完成三天日常行动。</p><ol className="weekly-steps">{data.weeklySteps.map((s,i)=><li key={s.label}><span className={s.done?'done':''}>{s.done?<Check size={15}/>:i+1}</span>{s.label}</li>)}</ol><p>本周日常：{Math.min(3,data.weeklyDays)} / 3 天</p><button className="primary" disabled={busy||!data.weeklyReady||data.weeklyClaimed} onClick={()=>run('weekly',{period:data.week})}>{data.weeklyClaimed?'本周已领取':'领取周活动奖励'}<ArrowRight size={17}/></button><small>抽奖券 ×2 · 纪念章 ×3</small>
    {data.pendingWeeks.filter(w=>w!==data.week).map(w=><button className="secondary" disabled={busy} key={w} onClick={()=>run('weekly',{period:w})}>{w} 周奖励待领取</button>)}
   </article></div>}
   {tab==='draw'&&<><div className={`activity-draw ${drawing?'is-drawing':''}`}><img className="supply-chest" src="./activities/supply-chest.png" alt="" aria-hidden="true"/><div className="draw-copy"><h2>本服补给抽奖</h2><p>普通 80% · 精选 18% · 稀有 2%（基础概率）</p><p>最多再抽 <strong>{data.guaranteeWithin}</strong> 次获得稀有奖励。券与保底不会过期。</p></div><button className="primary" disabled={busy||data.tickets<1} onClick={()=>run('draw')}><Ticket size={18}/>{drawing?'正在开奖…':'使用 1 张抽奖券'}</button></div>
    {reveal&&<div key={reveal.id} className={`draw-result result-${reveal.tier}`} role="status"><span className="result-spark" aria-hidden="true"/><div><span className="eyebrow">本次获得</span><h2>{rarity[reveal.tier]}</h2><strong>{reveal.name||'选择符合当前进度的材料'}</strong><RewardItems world={worldId} items={reveal.items}/><p>{reveal.status==='pending'?'奖励已保留，可在下方选择材料。':'入服后送入背包，背包满时继续保留。'}</p></div><button aria-label="关闭开奖结果" onClick={()=>setReveal(null)}>×</button></div>}
    <section className="activity-panel reward-pool" aria-label="奖池"><h2>奖池</h2><p>多方块奖励包含整套结构，每种限领一次。先掌握原有工艺，再领取奖励。</p>{(['ordinary','selected','rare'] as const).map(tier=><section className={`pool-tier pool-${tier}`} key={tier}><h3>{rarity[tier]}<span>{data.pool.filter(r=>r.tier===tier).length} 种</span></h3><ul className="pool-grid">{data.pool.filter(r=>r.tier===tier).map(r=><li className={`pool-entry ${r.items.length===1?'single-item':''}`} key={r.id}><div className="pool-entry-heading"><strong>{r.name}</strong><details className="pool-condition"><summary>{r.eligible?'已解锁':'查看条件'}</summary><p>{r.eligibility||(r.eligible?'已满足领取条件。':'完成原有前置并自行制作对应物品后开放。')}</p></details></div><RewardItems world={worldId} items={r.items}/></li>)}</ul></section>)}</section>
    <h2 className="activity-subtitle">奖励记录</h2><div className="award-list">{data.awards.length===0?<p>完成行动并领取签到，即可获得第一张抽奖券。</p>:data.awards.map(a=><article key={a.id} className={`activity-panel award-${a.tier}`}><div className="award-heading"><strong>{a.name||rarity[a.tier]}</strong><span>{status[a.status]}</span></div><small>{a.source} · {new Date(a.createdAt).toLocaleDateString('zh-CN')}</small><RewardItems world={worldId} items={a.items}/>{a.status==='queued'&&<p>入服后自动送入背包；背包满时整份保留。</p>}{a.status==='pending'&&(a.choices.length?<div className="rare-choices">{a.choices.map(r=><div key={r.id}><strong>{r.name}</strong><RewardItems world={worldId} items={r.items}/><button className="secondary" disabled={busy} onClick={()=>run('select',{awardId:a.id,rewardId:r.id})}>选择此奖励</button></div>)}</div>:<p>暂无符合条件的奖励，这次中奖资格会继续保留。</p>)}</article>)}</div>
   </>}
   {tab==='shop'&&<><div className={`activity-profile ${data.equippedFrame?'with-frame':''}`} style={data.equippedBackground?{backgroundImage:`linear-gradient(90deg,#111b,#111b),url('./scenes/${world.image}')`}:undefined}><span className="activity-avatar"><SkinAvatar skin={skin} name={profile?.gameName||''}/></span><div><strong>{profile?.gameName}</strong><p>{data.equippedTitle?`${world.name} · 同行者`:data.stage}</p></div></div><div className="cosmetic-grid">{cosmetic.map(c=><article key={c.id} className="activity-panel"><div className={`cosmetic-art cosmetic-${c.id}`} style={{backgroundImage:`linear-gradient(0deg,#101710dd,transparent),url('./scenes/${world.image}')`}}>{c.id==='frame'?<SkinAvatar skin={skin} name={profile?.gameName||''}/>:<ServerIcon id={worldId}/>}<span>{c.id==='title'?'同行者':c.id==='frame'?'':world.name}</span></div><h2>{c.name}</h2><p>{world.name}主题 · 仅用于统一端展示</p><strong>{c.price} 枚纪念章</strong><button className="primary" disabled={busy||(!data.cosmetics.includes(c.id)&&data.medals<c.price)} onClick={()=>run(data.cosmetics.includes(c.id)?'equip':'buy',{cosmetic:c.id})}>{data.cosmetics.includes(c.id)?'使用':'兑换'}</button></article>)}</div></>}
   {tab==='showcase'&&<div className="activity-columns"><article className="activity-panel"><h2>{data.monthlyName}</h2><p>分享一段本服的游玩经验、据点或工艺说明。每月提交一次，审核通过后展示。</p><textarea aria-label="分享内容" value={text} onChange={e=>setText(e.target.value)} maxLength={700} placeholder="写清楚你完成了什么，以及对其他玩家有什么帮助。"/><small>{text.length} / 700</small><button className="primary" disabled={busy||!!data.myShowcase||text.trim().length<10} onClick={()=>run('showcase',{text})}>{data.myShowcase?'本月已提交':'提交分享'}</button></article><article className="activity-panel"><h2>本服玩家分享</h2>{data.showcases.length?data.showcases.map(s=><div className="showcase-entry" key={s.id}><strong>{s.gameName} · {s.stage}</strong><p>{s.text}</p></div>):<p>本月分享正在征集中。</p>}</article></div>}
  </>}
 </section>;
}
