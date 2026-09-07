"""Actual sprite mappings for the expanded pools, from each shipped mod version."""
import json, zipfile


def extract_revision(root, instances, extract, icons, gt):
    catalog = json.loads((root/'activities/catalog.json').read_text(encoding='utf-8'))
    vanilla_names={'torch':'火把','coal':'木炭','charcoal':'木炭','arrow':'箭矢','ladder':'梯子','glass':'玻璃','chest':'箱子','rail':'铁轨','paper':'纸','bricks':'砖块','brick_block':'砖块',
                   'stone_bricks':'石砖','stonebrick':'石砖','powered_rail':'动力铁轨','golden_rail':'动力铁轨','hopper':'漏斗','repeater':'红石中继器','piston':'活塞','cauldron':'炼药锅'}
    vanilla_old={'glass':'blocks/glass','chest':'entity/chest/normal','rail':'blocks/rail_normal','paper':'items/paper','brick_block':'blocks/brick','stonebrick':'blocks/stonebrick',
                 'golden_rail':'blocks/rail_golden','hopper':'items/hopper','repeater':'items/repeater','piston':'blocks/piston_side','cauldron':'items/cauldron','ladder':'blocks/ladder'}
    vanilla_new={'glass':'block/glass','chest':'entity/chest/normal','rail':'block/rail','paper':'item/paper','bricks':'block/bricks','stone_bricks':'block/stone_bricks',
                 'powered_rail':'block/powered_rail','hopper':'item/hopper','repeater':'item/repeater','piston':'block/piston_side','cauldron':'item/cauldron','ladder':'block/ladder'}
    mm_names={'dustMana':'魔法粉','ManaCoal':'灵炭','MetalEnergy02':'中级金属能量','ingotManaS':'魔法钢锭',
              'BlockTileEntityManaEnchantings':'法力附魔台','BLOCKManaMetalInjection':'魔力灌注核心','BLOCKCrystalPillars':'魔力石柱',
              'ManaRuneE':'魔导符文','BlockMagicUpdates':'法杖融合台','magicStone2':'祭坛符文石','magicStone4':'祭坛符文石核心','BlockTileEntityHumanReforms':'人体改造仪'}
    mm_paths={'BlockTileEntityManaEnchantings':'blocks/TileEntityManaEnchanting','ManaRuneE':'blocks/ManaRuneE_0','BlockMagicUpdates':'blocks/BlockMagicUpdate'}
    ie_old={'metal@8':('items/metal_ingot_steel','钢锭'), 'metal@39':('items/metal_plate_iron','铁板'),
            'metal_decoration0@3':('blocks/metal_decoration0_redstone_engineering','红石工程块'),
            'metal_decoration0@4':('blocks/metal_decoration0_light_engineering','轻型工程块'),
            'metal_decoration0@5':('blocks/metal_decoration0_heavy_engineering','重型工程块'),
            'metal_decoration1@1':('blocks/metal_decoration1_steel_scaffolding','钢脚手架'),
            'metal_device1@6':('blocks/metal_device1_fluid_pipe','流体管道'),
            'sheetmetal@9':('blocks/sheetmetal_iron','铁钣金块'),
            'stone_decoration@0':('blocks/stone_decoration_cokebrick','焦炉砖'),
            'stone_decoration@1':('blocks/stone_decoration_blastbrick','高炉砖')}
    ie_new_names={'ingot_steel':'钢锭','plate_iron':'铁板','light_engineering':'轻型工程块','heavy_engineering':'重型工程块','rs_engineering':'红石工程块',
                  'cokebrick':'焦炉砖','conveyor_basic':'传送带','steel_fence':'钢栅栏','wooden_barrel':'木桶'}

    def model_texture(z, ns, path, seen=None):
        seen=set() if seen is None else seen
        entry=f'assets/{ns}/models/{path}.json'
        if entry in seen or entry not in z.namelist():return {}
        seen.add(entry);data=json.loads(z.read(entry));textures={}
        if 'parent' in data:
            parent=data['parent'];n,_,p=parent.partition(':');textures.update(model_texture(z,n,p,seen) if p else model_texture(z,'minecraft',n,seen))
        textures.update(data.get('textures',{}));return textures

    for world in catalog['worlds']:
        w=world['id'];modern=w=='dc2';base=instances/('m3e' if w=='vw' else w)
        vanilla=next(p for p in (base/'versions').glob('*/*.jar') if zipfile.is_zipfile(p) and ('assets/minecraft/textures/item/charcoal.png' if modern else 'assets/minecraft/textures/items/charcoal.png') in zipfile.ZipFile(p).namelist())
        jars={p.name:p for p in (base/'mods').glob('*.jar')}
        needed={i['id']+'@'+str(i['meta']) for r in world['rewards'] for i in r['items']}
        for key in sorted(needed):
            if w+':'+key in icons:continue
            ns,tail=key.split(':');id,meta=tail.split('@')
            if ns=='minecraft':
                texture=(vanilla_new if modern else vanilla_old)[id]
                extract(w,key,vanilla,'assets/minecraft/textures/'+texture+'.png',vanilla_names[id],texture.startswith(('block/','blocks/')))
                # Chest sprite contains a UV atlas. Display its front face with CSS,
                # retaining the game-authored source PNG without inventing an icon.
                if id=='chest':icons[w+':'+key]['atlas']={'x':14,'y':33,'width':14,'height':10,'size':64}
            elif ns=='manametalmod':
                jar=next(p for n,p in jars.items() if n.startswith('manametalmod-'))
                path=mm_paths.get(id,('items/' if id in ['dustMana','ManaCoal','MetalEnergy02','ingotManaS'] else 'blocks/')+id)
                extract(w,key,jar,'assets/manametalmod/textures/'+path+'.png',mm_names[id],path.startswith('blocks/') and id!='ManaRuneE')
            elif ns=='immersiveengineering':
                jar=next(p for n,p in jars.items() if n.startswith('ImmersiveEngineering-'))
                if not modern:
                    path,label=ie_old[tail];extract(w,key,jar,'assets/immersiveengineering/textures/'+path+'.png',label,path.startswith('blocks/'))
                else:
                    with zipfile.ZipFile(jar) as z:
                        textures=model_texture(z,ns,'item/'+id)
                        value=next((textures[k] for k in ['layer0','front','side','all','particle','texture','0'] if k in textures),None)
                        if id=='conveyor_basic':value='immersiveengineering:block/conveyor/off'
                        if id=='wooden_barrel':value='immersiveengineering:block/wooden_device/barrel_side'
                        if value is None:raise ValueError((id,textures))
                        while value.startswith('#'):value=textures[value[1:]]
                        domain,tex=value.split(':')
                        extract(w,key,jar,f'assets/{domain}/textures/{tex}.png',ie_new_names[id],not tex.startswith('item/'))
            elif ns=='gregtech':
                if id=='gt.multitileentity':
                    controller={17000:('cokeoven','焦炉核心'),17101:('distillationtower','蒸馏塔核心'),17112:('autoclave','大型高压釜核心'),17103:('electrolyzer','大型电解器核心')}
                    if int(meta) in controller:
                        folder,label=controller[int(meta)];prefix='assets/gregtech/textures/blocks/machines/basicmachines/'+folder+'/'
                        extract(w,key,gt,prefix+'colored/front.png',label,True,prefix+'overlay/front.png')
                    else:
                        folder,label={18022:('metalwalldense','高强度不锈钢墙壁'),18105:('electrolyzerparts','电解器部件')}[int(meta)]
                        prefix='assets/gregtech/textures/blocks/machines/multiblockparts/'+folder+'/0/'
                        extract(w,key,gt,prefix+'colored/side.png',label,True,prefix+'overlay/side.png')
                else:
                    form=id.split('.')[-1];label=('不锈钢' if meta=='8636' else '铁')+{'plate':'板','stick':'杆','screw':'螺丝'}[form]
                    prefix='assets/gregtech/textures/items/materialicons/'+('SHINY' if meta=='8636' else 'METALLIC')+'/'
                    extract(w,key,gt,prefix+form+'.png',label,False,prefix+form+'_OVERLAY.png')
            else:raise ValueError('Missing exact sprite mapping: '+key)
