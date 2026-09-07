"""Compile reviewed activity policy against the installed, original quest books.
Does not edit a quest, completion, recipe, world or player file.
"""
from pathlib import Path
import json, re, uuid, os

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(os.environ.get('ACTIVITY_SOURCE_ROOT', str(ROOT)))
INSTANCES = SOURCE / '.local/complete-client-qa/全新整包安装/instances'
OUT = ROOT / 'activities'

def condition(all=(), any=(), none=()):
    return dict(all=list(all), any=list(any), none=list(none))

def qid(q):
    if 'questID:3' in q: return str(q['questID:3'])
    return str(uuid.UUID(int=((q['questIDHigh:4'] & ((1<<64)-1))<<64) | (q['questIDLow:4'] & ((1<<64)-1))))

def snbt(text):
    # FTB SNBT permits omitted commas; quoted values are JSON-compatible.
    tokens = re.findall(r'"(?:\\.|[^"\\])*"|[{}\[\]:,]|[^\s{}\[\]:,]+', text)
    pos = 0
    def value():
        nonlocal pos
        token=tokens[pos]; pos+=1
        if token=='{':
            result={}
            while tokens[pos]!='}':
                if tokens[pos]==',': pos+=1;continue
                key=tokens[pos];pos+=1
                if tokens[pos]!=':': raise ValueError('Expected colon: '+key)
                pos+=1;result[json.loads(key) if key.startswith('"') else key]=value()
            pos+=1;return result
        if token=='[':
            result=[]
            while tokens[pos]!=']':
                if tokens[pos]==',':pos+=1;continue
                result.append(value())
            pos+=1;return result
        if token.startswith('"'):return json.loads(token)
        if token in ('true','false'):return token=='true'
        try:return float(token.rstrip('bBsSlLfFdD')) if '.' in token else int(token.rstrip('bBsSlL'))
        except ValueError:return token
    return value()

def bq(world):
    root=INSTANCES/world/'config/betterquesting/DefaultQuests'
    if world=='vw':root=ROOT.parent/'_voidwayfarer4-deploy/release-r4-20260906/instance/config/betterquesting/DefaultQuests'
    result=[]
    for path in sorted(root.glob('Quests/*/*.json')):
        q=json.loads(path.read_text(encoding='utf-8-sig')); props=q['properties:10']['betterquesting:10']
        tasks=list(q.get('tasks:9',{}).values())
        # Checkboxes/optional retrieval are not sufficient evidence for a first completion.
        real=any(t.get('taskID:8') not in ('bq_standard:checkbox','bq_standard:optional_retrieval') and not t.get('optional:1',0) for t in tasks)
        if not real:continue
        name=re.sub('§.','',props['name:8'])
        prereq=[str(i) for i in q.get('preRequisites:11',[])] if world=='mb' else [qid(p) for p in q.get('preRequisites:9',{}).values()]
        result.append(dict(id=qid(q),name=name,chapter=path.parent.name,tasks=tasks,prereq=prereq))
    return result

def item(id,count,meta=0):return dict(id=id,meta=meta,count=count,nbt='{}')
def reward(id,name,tier,items,purpose='',all=(),any=(),none=(),goal=None,bp=0):
    proof=['craft:'+i['id']+'@'+str(i['meta']) for i in items]
    return dict(id=id,name=name,tier=tier,items=items,purpose=purpose,requires=condition([*all,*proof,*(['unlocked:'+goal] if goal else [])],any,none),goal=goal,basisPoints=bp)
def action(id,name,description,kind,keys,count=1,requires=None):
    return dict(id=id,name=name,description=description,kind=kind,keys=keys,count=count,requires=requires or condition())

worlds=[];audit=[]
names={'m3e':('魔法金属','冒险准备','魔王再战','冒险手册'),'dc2':('亡者世界','外出准备','据点值守','生存据点开放日'),'mb':('肉丸工艺','工坊记录','本章研究','产线说明书'),'vw':('虚空行者','工段日志','工艺接力','空岛工厂开放日')}
for world in names:
    name,daily,weekly,monthly=names[world]
    rewards=[reward('daily-torches','照明补给','daily',[item('minecraft:torch',4)],'补充已经掌握制作方法的火把。'),
             reward('ordinary-torches','照明补给','ordinary',[item('minecraft:torch',8)],'探索和生产场地照明。'),
             reward('selected-charcoal','燃料补给','selected',[item('minecraft:charcoal' if world=='dc2' else 'minecraft:coal',8,0 if world=='dc2' else 1)],'补充已自行烧制过的木炭。')]
    tracked={'minecraft:torch@0','minecraft:charcoal@0' if world=='dc2' else 'minecraft:coal@1'}
    if world in ('m3e','mb','vw'):
        support=item('minecraft:arrow',8) if world=='m3e' else item('minecraft:ladder',6)
        for tier in ('daily','ordinary'):
            rewards.append(reward(tier+'-field-supplies','冒险箭矢' if world=='m3e' else '作业梯补给',tier,[support],'仅在自己制作过后补充，供日常探索与场地维护使用。'))
    kills=[];stages=[dict(id='start',name={'m3e':'冒险起步','dc2':'生存起步','mb':'初到异世','vw':'开荒草莽'}[world],requires=condition())]
    if world!='dc2':
        quests=bq(world); byname={q['name']:q for q in quests}
        if world=='m3e':
            permitted=['-oOQYVIV0R6S71nZrPPMHgQ==','-bfZZGR-7Qi2UctGmDiVD_g==','-ESLElyKfSjazsrwfNvBSvg==']
            chosen=[q for q in quests if q['chapter'] in permitted]
            for title in ['魔法钢锭','玛夏达巴']:
                q=byname[title];stages.append(dict(id=q['id'],name='魔法工艺' if title=='魔法钢锭' else '魔王征战与每日任务',requires=condition(['quest:'+q['id']])))
            q=byname['法力附魔台'];part=item('manametalmod:magicStone3',3)
            rewards.append(reward('mana-enchantment-foundation','法力附魔台材料','rare',[part],'原任务需要 24 个祭坛符文石柱，本包补充 3 个（12.5%）；需已自行制作。',none=['quest:'+q['id']],goal=q['id'],bp=1250))
            audit.append(dict(world=world,reward='mana-enchantment-foundation',quest=q['id'],required=24,awarded=3,source='原任务 requiredItems magicStone3'))
            for rid,id,total,label in [('mana-pillars','manametalmod:magicStone1',9,'祭坛石'),('mana-crystals','manametalmod:ManaCrystal',8,'魔力水晶')]:
                rewards.append(reward(rid,'附魔台'+label+'补给','rare',[item(id,1)],f'原结构需要 {total} 个{label}，本包补 1 个；已经自行制作后可领取。',none=['quest:'+q['id']],goal=q['id'],bp=round(10000/total)))
                audit.append(dict(world=world,reward=rid,quest=q['id'],required=total,awarded=1,source='法力附魔台原任务 requiredItems'))
        elif world=='mb':
            chosen=[q for q in quests if q['chapter']=='0']
            # Chapter headers are checkboxes. Use their concrete prerequisite quests instead.
            for n in [68,74,147,274,283,291,308,319,1337626444]:
                path=INSTANCES/world/f'config/betterquesting/DefaultQuests/Quests/0/{n}.json';raw=json.loads(path.read_text(encoding='utf-8-sig'))
                concrete=[str(p) for p in raw.get('preRequisites:11',[]) if str(p) in {q['id'] for q in chosen}]
                if concrete:stages.append(dict(id=str(n),name=re.sub('§.','',raw['properties:10']['betterquesting:10']['name:8']),requires=condition(['quest:'+p for p in concrete])))
            q=next(q for q in chosen if q['id']=='179')
            rewards.append(reward('petrified-foundation','化石木建设材料','rare',[item('erebus:planks_petrified_wood',3)],'主线“生命真的自会找到出路！”需要 26 块化石木板，本包补充 3 块（11.54%）。',all=['quest:'+p for p in q['prereq'] if p in {v['id'] for v in chosen}],none=['quest:'+q['id']],goal=q['id'],bp=1154))
            audit.append(dict(world=world,reward='petrified-foundation',quest='179',required=26,awarded=3,source='维度飞升 requiredItems'))
            for target,rid,id,count,total,label in [('163','emberstone-building','aoa3:emberstone_block',1,8,'余烬石块'),('164','purple-steel-building','divinerpg:purple_steel',1,8,'紫色钢铁')]:
                q=next(v for v in chosen if v['id']==target)
                rewards.append(reward(rid,label+'补给','rare',[item(id,count)],f'当前主线“{q["name"]}”需要 {total} 个，本包补 {count} 个。维度与原任务条件仍需正常完成。',none=['quest:'+target],goal=target,bp=round(count/total*10000)))
                audit.append(dict(world=world,reward=rid,quest=target,required=total,awarded=count,source='维度飞升原任务 requiredItems'))
        else:
            chosen=[q for q in quests if re.match('Ch[1-6]-',q['chapter'])]
            for chapter,title in [(2,'进军工业'),(3,'蒸汽岁月'),(4,'化学工程'),(5,'电力时代'),(6,'终极之路')]:
                ids=['quest:'+q['id'] for q in chosen if q['chapter'].startswith(f'Ch{chapter}-')]
                stages.append(dict(id='ch'+str(chapter),name=title,requires=condition(any=ids)))
            q=byname['蒸馏塔'];steel=byname['不锈钢']
            for rid,meta,qty,total,label in [('tower-wall',18102,10,71,'塔体结构'),('tower-base',18101,1,9,'热能传输器')]:
                rewards.append(reward(rid,'蒸馏塔'+label+'补给','rare',[item('gregtech:gt.multitileentity',qty,meta)],f'原任务参考结构需要 {total} 个，本包补充 {qty} 个；需已自行制作相同结构件。',all=['quest:'+steel['id']],none=['quest:'+q['id']],goal=q['id'],bp=round(qty/total*10000)))
                audit.append(dict(world=world,reward=rid,quest=q['id'],required=total,awarded=qty,source='蒸馏塔原任务结构清单'))
            q=byname['焦炉'];rewards.append(reward('coke-wall','焦炉结构补给','rare',[item('gregtech:gt.multitileentity',3,18000)],'原焦炉任务需要 25 个结构件，本包补 3 个（12%）；必须已自行制作结构件，焦炉搭建与首次运行仍需正常完成。',none=['quest:'+q['id']],goal=q['id'],bp=1200))
            audit.append(dict(world=world,reward='coke-wall',quest=q['id'],required=25,awarded=3,source='焦炉原任务 requiredItems'))
        questids=[q['id'] for q in chosen]
        for q in chosen:
            for t in q['tasks']:
                if t.get('taskID:8')=='bq_standard:hunt':
                    target=t.get('target:8') or t.get('entity:8')
                    if target:kills.append(target)
                for i in t.get('requiredItems:9',{}).values():
                    id=i.get('id:8','');meta=i.get('Damage:2',0)
                    if id.startswith('gregtech:gt.meta.') and 'tag:10' not in i:tracked.add(id+'@'+str(meta))
        if world=='m3e':kills=['manametalmod.EntityBossMasadabah']
        if world=='mb':kills=['minecraft:zombie','minecraft:skeleton','minecraft:spider']
        actions=[action('quest','推进原有任务','完成当前已开放的原生主线任务；教程勾选不计入。','quest',questids),
                 action('craft','补充工坊材料','正常合成 4 个火把、箭矢或当前主线的材料。' if world=='m3e' else '正常合成 4 个火把、梯子或当前主线的材料。','craft',[],4),
                 action('challenge','完成冒险行动','参与实际战斗；不要求开新维度。','kill',kills,8)] if world!='vw' else [
                 action('quest','推进工艺任务','完成当前章节的原生任务。','quest',questids),
                 action('craft','工段生产','制作或加工 4 份工艺材料，自己的 GT6 机器实际产出也会计入。','craft',[],4),
                 action('supply','准备生产补给','制作 8 份火把或烧制木炭。','craft',['minecraft:torch@0','minecraft:coal@1'],8)]
        if world=='m3e':
            actions[2]['count']=1;actions[2]['name']='魔王再战';actions[2]['description']='再次击败已经通关的玛夏达巴。新人的首次主线挑战可计入推进任务。';actions[2]['requires']=condition(['quest:'+byname['玛夏达巴']['id']])
    else:
        chapters=INSTANCES/world/'config/ftbquests/quests/chapters'
        parsed={p.stem:snbt(p.read_text(encoding='utf-8-sig')) for p in chapters.glob('*.snbt')}
        chosen=[]
        for chapter in ['questmainbuildings','questmainabyss','basic_needs','machinery','bullet_automation','steels']:
            for q in parsed[chapter].get('quests',[]):
                if any(t.get('type') not in ('checkmark','check') for t in q.get('tasks',[])):chosen.append(q)
        questids=[q['id'] for q in chosen]
        water='legendarysurvivaloverhaul:purified_water_bottle';bandage='legendarysurvivaloverhaul:bandage'
        tracked.update([water+'@0',bandage+'@0']);kills=['minecraft:zombie','minecraft:husk','minecraft:drowned']
        for label,key,qty in [('净水',water,2),('绷带',bandage,1)]:
            for tier,mult in [('daily',1),('ordinary',1),('selected',2)]:rewards.append(reward(tier+'-'+label,label+'补给',tier,[item(key,qty*mult)],'已自行制作后开放。'))
        for key,title in [('2C6177EDC934F9DC','生存补给'),('4A1A36DD335F7F72','金属加工')]:
            assert key in questids;stages.append(dict(id=key,name=title,requires=condition(['quest:'+key])))
        for fragment,material,qty,total,rid in [('refinery','immersiveengineering:sheetmetal_iron',2,16,'refinery-plates'),('refinery','immersiveengineering:steel_scaffolding_standard',1,8,'refinery-scaffold'),('refinery','immersiveengineering:fluid_pipe',1,5,'refinery-pipe')]:
            q=next(q for q in chosen if any(fragment in str(t.get('advancement','')) for t in q.get('tasks',[])))
            rewards.append(reward(rid,{'refinery-plates':'炼油厂铁板补给','refinery-scaffold':'炼油厂脚手架补给','refinery-pipe':'炼油厂管道补给'}[rid],'rare',[item(material,qty)],f'原多方块结构需要 {total} 个，本包补充 {qty} 个；已制作对应材料且完成金属冲压机任务后开放。',all=['quest:4A1A36DD335F7F72'],none=['quest:'+q['id']],goal=q['id'],bp=round(qty/total*10000)))
            audit.append(dict(world=world,reward=rid,quest=q['id'],required=total,awarded=qty,source='ImmersiveEngineering 10.2.0-183 multiblock template'))
        actions=[action('quest','推进生存任务','完成已开放的生存、探索或加工任务。','quest',questids),action('craft','准备外出补给','自行制作一份净水、绷带或已掌握工艺的建材。','craft',[],1),action('challenge','清理当前区域','参与击败 8 只僵尸类敌人，不额外生成尸潮。','kill',kills,8)]
        goal='741526AA7D74D11C';assert goal in questids
        rewards.append(reward('blast-furnace-bricks','高炉砖补给','rare',[item('immersiveengineering:blastbrick',3)],'高炉原结构需要 27 块高炉砖，本包补 3 块（11.11%）；必须已自行制作，保留搭建与首次炼钢流程。',none=['quest:'+goal],goal=goal,bp=1111))
        audit.append(dict(world=world,reward='blast-furnace-bricks',quest=goal,required=27,awarded=3,source='ImmersiveEngineering 10.2.0-183 blast_furnace.nbt'))
    for r in rewards:
        tracked.update(i['id']+'@'+str(i['meta']) for i in r['items'])
    definition=dict(id=world,name=name,dailyName=daily,weeklyName=weekly,monthlyName=monthly,stages=stages,actions=actions,
                    weeklySteps=[['craft','supply'],['challenge','quest'],['craft','supply']],weeklyLabels=['准备补给或材料','参与挑战或推进任务','完成后补充物资'],questIds=sorted(set(questids)),trackedItems=sorted(tracked),trackedKills=sorted(set(kills)),rewards=rewards)
    worlds.append(definition)
from activity_reward_revision import revise
worlds, audit = revise(worlds, audit, INSTANCES, ROOT, bq, snbt)
OUT.mkdir(exist_ok=True)
(OUT/'catalog.json').write_text(json.dumps(dict(version=2,worlds=worlds),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
(OUT/'reward-audit.json').write_text(json.dumps(audit,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Activity catalogue:',', '.join(f"{w['id']} {len(w['questIds'])} quests / {len(w['rewards'])} rewards" for w in worlds))
