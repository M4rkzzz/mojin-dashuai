"""Reviewed reward revision. Structure counts come from the shipped quests/mods.

Old reward IDs stay immutable for delivery and history. Whole structures require
production evidence for parts and verified personal possession of custom-machine
controllers. They can be claimed once even after the retrieval quest completes.
"""
import collections, gzip, io, json, zipfile
import nbtlib


def revise(worlds, audit, instances, root, bq, snbt):
    def item(key, count):
        name, _, meta = key.partition('@')
        return dict(id=name, meta=int(meta or 0), count=count, nbt='{}')

    for w in worlds:
        world = w['id']; old = w['rewards']
        for r in old:
            r['retired'] = True
        rewards = []
        quests = bq(world) if world != 'dc2' else []
        byname = {q['name']: q for q in quests}
        def add(rid, name, tier, parts, *, goal=None, source='原版配方 / 对应模组实际物品', requires=()):
            items = [item(k, n) for k, n in parts]
            proof = ['craft:'+i['id']+'@'+str(i['meta']) for i in items]
            condition = dict(all=list(dict.fromkeys([*requires, *proof])), any=[], none=[])
            if goal:
                # Retrieval quests may finish on crafting the controller: completed
                # or unlocked are both valid, never require an unfinished controller.
                condition['any'] = ['unlocked:'+goal, 'quest:'+goal]
                if goal not in w['questIds']:
                    w['questIds'].append(goal)
            r = dict(id='v2-'+rid, name=name, tier=tier, items=items,
                     purpose='包含全部结构件；供能、加工耗材和实际搭建按原流程完成。' if goal else '补充已掌握工艺的日常用料。',
                     requires=condition, goal=goal, basisPoints=10000 if goal else 0, completeSet=bool(goal))
            rewards.append(r)
            audit.append(dict(world=world, reward=r['id'], source=source, items=items,
                              completeSet=bool(goal), qualification='本人实际制作各类部件；原任务已开放或完成；同结构一套' if goal else '本人实际制作过对应物品'))

        charcoal = 'minecraft:charcoal' if world == 'dc2' else 'minecraft:coal@1'
        # Useful replenishment, not new foods, tools, first-craft keys or boss drops.
        for rid, title, parts in [
            ('torches','火把',[('minecraft:torch',8)]),
            ('fuel','木炭',[(charcoal,8)]),
            ('glass','玻璃',[('minecraft:glass',8)]),
            ('ladder','梯子',[('minecraft:ladder',8)])]:
            add('daily-'+rid,title,'daily',parts)
        common = [
            ('torches','火把',[('minecraft:torch',32)]),
            ('fuel','木炭',[(charcoal,24)]),
            ('glass','玻璃',[('minecraft:glass',32)]),
            ('ladder','梯子',[('minecraft:ladder',24)]),
            ('storage','箱子',[('minecraft:chest',4)]),
            ('rail','铁轨',[('minecraft:rail',32)]),
            ('paper','纸',[('minecraft:paper',32)]),
            ('bricks','砖块',[('minecraft:bricks' if world=='dc2' else 'minecraft:brick_block',24)])]
        for rid,title,parts in common: add('ordinary-'+rid,title,'ordinary',parts)
        selected = [
            ('exploration','照明与燃料',[('minecraft:torch',64),(charcoal,64)]),
            ('building','建筑材料',[('minecraft:glass',64),('minecraft:stone_bricks' if world=='dc2' else 'minecraft:stonebrick',64)]),
            ('railway','铁路材料',[('minecraft:rail',64),('minecraft:powered_rail' if world=='dc2' else 'minecraft:golden_rail',8)]),
            ('warehouse','仓储材料',[('minecraft:chest',8),('minecraft:hopper',4)]),
            ('automation','红石配件',[('minecraft:repeater',8),('minecraft:piston',8)]),
            ('worksite','场地搭建',[('minecraft:ladder',64),('minecraft:glass',64)])]
        for rid,title,parts in selected: add('selected-'+rid,title,'selected',parts)

        if world == 'm3e':
            for rid,label,key,lo,hi in [('arrows','箭矢','minecraft:arrow',32,64),
                ('mana','魔法粉','manametalmod:dustMana',16,48),
                ('mana-fuel','灵炭','manametalmod:ManaCoal',8,24),
                ('metal-energy','中级金属能量','manametalmod:MetalEnergy02',4,12),
                ('mana-steel','魔法钢锭','manametalmod:ingotManaS',4,12)]:
                for tier,qty in [('ordinary',lo),('selected',hi)]:add(tier+'-'+rid,label,tier,[(key,qty)])
            mm=lambda name:'manametalmod:'+name
            sets=[('enchantment','法力附魔台', [('BlockTileEntityManaEnchantings',1),('magicStone1',9),('magicStone3',24),('ManaCrystal',8)]),
                ('infusion','魔力灌注',[('BLOCKManaMetalInjection',1),('BLOCKCrystalPillars',8),('magicStone3',12),('ManaCrystal',4),('ManaRuneE',16)]),
                ('wand','法杖融合',[('BlockMagicUpdates',1),('magicStone1',21),('magicStone2',24),('magicStone3',28),('magicStone4',8),('ManaRuneE',24),('ManaCrystal',8)]),
                ('reform','人体改造',[('BlockTileEntityHumanReforms',1),('magicStone1',9),('magicStone3',24),('ManaRuneE',24),('ManaCrystal',8)])]
            for rid,title,parts in sets:
                add('set-'+rid,title+'整套','rare',[(mm(k),n) for k,n in parts],goal=byname[title]['id'],source='M3E 原任务 '+title+' 的 requiredItems / 结构清单；含核心')
        elif world == 'vw':
            for rid,label,key,lo,hi in [('brick','耐火砖块','gregtech:gt.multitileentity@18000',4,12),
                ('plate','铁板','gregtech:gt.meta.plate@260',8,24),
                ('rod','铁杆','gregtech:gt.meta.stick@260',16,48),
                ('screw','铁螺丝','gregtech:gt.meta.screw@260',32,64),
                ('stainless','不锈钢板','gregtech:gt.meta.plate@8636',4,12)]:
                for tier,qty in [('ordinary',lo),('selected',hi)]:add(tier+'-'+rid,label,tier,[(key,qty)])
            sets=[('coke','焦炉','焦炉',[(17000,1),(18000,25)]),
                ('tower','蒸馏塔','蒸馏塔',[(17101,1),(18102,71),(18101,9)]),
                ('autoclave','大型高压釜','高压釜(fu三声)',[(17112,1),(18022,25)]),
                ('electrolyzer','大型电解器','Power over 9,000!',[(17103,1),(18105,17)])]
            for rid,title,qname,parts in sets:
                add('set-'+rid,title+'整套','rare',[('gregtech:gt.multitileentity@'+str(m),n) for m,n in parts],goal=byname[qname]['id'],source='GT6 6.17.06 原任务及 MultiTileEntity'+{'coke':'CokeOven','tower':'DistillationTower','autoclave':'Autoclave','electrolyzer':'Electrolyzer'}[rid]+' 结构校验/提示；主方块替代一块结构件')
        else:
            modern = world == 'dc2'
            ie = 'immersiveengineering:'
            ieparts = {'steel':('ingot_steel' if modern else 'metal@8',4,12,'钢锭'),
                       'plate':('plate_iron' if modern else 'metal@39',8,24,'铁板'),
                       'pipe':('fluid_pipe' if modern else 'metal_device1@6',8,24,'流体管道'),
                       'scaffold':('steel_scaffolding_standard' if modern else 'metal_decoration1@1',8,24,'钢脚手架')}
            for rid,(key,lo,hi,title) in ieparts.items():
                for tier,qty in [('ordinary',lo),('selected',hi)]:add(tier+'-'+rid,title,tier,[(ie+key,qty)])
            if modern:
                for rid,title,key,lo,hi in [('water','净水','purified_water_bottle',4,12),('bandage','绷带','bandage',4,12)]:
                    for tier,qty in [('ordinary',lo),('selected',hi)]:add(tier+'-'+rid,title,tier,[('legendarysurvivaloverhaul:'+key,qty)])
                parsed=[snbt(p.read_text(encoding='utf-8-sig')) for p in (instances/world/'config/ftbquests/quests/chapters').glob('*.snbt')]
                allq=[q for c in parsed for q in c.get('quests',[])]
                jar=next((instances/world/'mods').glob('ImmersiveEngineering-*.jar'))
                with zipfile.ZipFile(jar) as z:
                    for key,title in [('coke_oven','焦炉'),('blast_furnace','高炉'),('metal_press','金属冲压机'),('refinery','炼油厂'),('fermenter','工业发酵机'),('squeezer','榨油机')]:
                        q=next(q for q in sorted(allq,key=lambda q:q['id'] not in w['questIds']) if any(t.get('advancement','').endswith('/mb_'+key.replace('_','')) for t in q.get('tasks',[])))
                        template='data/immersiveengineering/structures/multiblocks/'+key+'.nbt'
                        nbt=nbtlib.File.parse(io.BytesIO(gzip.decompress(z.read(template))))
                        counts=collections.Counter(str(nbt['palette'][int(b['state'])]['Name']) for b in nbt['blocks'])
                        counts.pop('minecraft:air',None)
                        add('set-'+key,title+'整套','rare',list(counts.items()),goal=q['id'],source=jar.name+':'+template)
            else:
                # Shipped IE 0.12-98 getTotalMaterials lists, not a 1.20 mapping.
                parts={'stone0':'stone_decoration@0','stone1':'stone_decoration@1','light':'metal_decoration0@4',
                       'heavy':'metal_decoration0@5','redstone':'metal_decoration0@3','scaffold':'metal_decoration1@1',
                       'pipe':'metal_device1@6','sheet':'sheetmetal@9'}
                sets=[('coke','焦炉','20610',[('stone0',27)]),('blast','高炉','20573',[('stone1',27)]),
                      ('fermenter','工业发酵机','20618',[('scaffold',6),('pipe',2),('redstone',1),('light',2),('minecraft:cauldron',4),('sheet',4)]),
                      ('refinery','炼油厂','20619',[('scaffold',8),('pipe',5),('redstone',1),('light',2),('heavy',2),('sheet',16)])]
                for rid,title,goal,listed in sets:
                    add('set-'+rid,title+'整套','rare',[(ie+parts[k] if k in parts else k,n) for k,n in listed],goal=goal,
                        requires=['quest:482221128'] if rid!='coke' else (),source='ImmersiveEngineering 0.12-98 Multiblock'+rid+' getTotalMaterials；原主线工艺及本人制作证明')
        # Ritual cores are produced inside custom machines, not a Forge crafting
        # table. Verify that the player has already obtained the exact core in
        # their own inventory; never treat that observation as production or a
        # daily action. Every repeatable structure part still requires crafting.
        controllers=[]
        if world in ('m3e','vw'):
            for r in rewards:
                if not r['completeSet']:continue
                i=r['items'][0];key=i['id']+'@'+str(i['meta']);controllers.append(key)
                r['requires']['all']=[('owned:'+key if f=='craft:'+key else f) for f in r['requires']['all']]
                entry=next(a for a in reversed(audit) if a['reward']==r['id'] and a['world']==world)
                entry['qualification']='核心先按原流程取得并由服务端确认本人持有；其余结构件实际生产；原任务已开放或完成；同结构一套'
        w['trackedControllers']=controllers
        w['rewards'] = [*rewards, *old]
        w['trackedItems'] = sorted(set(w['trackedItems']) | {i['id']+'@'+str(i['meta']) for r in rewards for i in r['items']})
        w['questIds'] = sorted(set(w['questIds']))
    return worlds, audit
