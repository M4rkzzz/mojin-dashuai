"""Copy game-authored reward textures; never synthesize item artwork."""
import json,zipfile,os,struct
from pathlib import Path

root=Path(__file__).resolve().parents[1];source=Path(os.environ.get('ACTIVITY_SOURCE_ROOT',str(root)));instances=source/'.local/complete-client-qa/全新整包安装/instances';out=root/'ui/public/activities/items';out.mkdir(parents=True,exist_ok=True)
sources={};icons={}
def extract(world,key,jar,texture,name,block=False,overlay=None):
    target=world+'-'+key.replace(':','_').replace('@','_')+'.png'
    frame=None
    with zipfile.ZipFile(jar) as z:
        sprite=z.read(texture);(out/target).write_bytes(sprite)
        if texture+'.mcmeta' in z.namelist():
            animation=json.loads(z.read(texture+'.mcmeta')).get('animation')
            if animation is not None:
                width,height=struct.unpack('>II',sprite[16:24]);fw=animation.get('width',width);fh=animation.get('height',fw)
                first=animation.get('frames',[0])[0];first=first.get('index',0) if isinstance(first,dict) else first
                if width!=fw or height!=fh:frame=dict(x=(first%(width//fw))*fw,y=(first//(width//fw))*fh,width=fw,height=fh,size=width)
        if overlay:(out/('overlay-'+target)).write_bytes(z.read(overlay))
    icons[world+':'+key]=dict(src='./activities/items/'+target,name=name,block=block,overlay='./activities/items/overlay-'+target if overlay else None)
    sources[world+':'+key]=dict(archive=Path(jar).name,texture=texture,overlay=overlay)
    if frame and not block:
        icons[world+':'+key]['atlas']=frame
        sources[world+':'+key]['frame']=frame

for w,version in [('m3e','1.7.10'),('mb','1.12.2'),('dc2','1.20.1'),('vw','1.7.10')]:
    base=instances/('m3e' if w=='vw' else w)
    candidates=list((base/'versions').glob('*/*.jar'))
    vanilla=next(p for p in candidates if zipfile.is_zipfile(p) and ('assets/minecraft/textures/item/charcoal.png' if w=='dc2' else 'assets/minecraft/textures/items/charcoal.png') in zipfile.ZipFile(p).namelist())
    extract(w,'minecraft:torch@0',vanilla,'assets/minecraft/textures/block/torch.png' if w=='dc2' else 'assets/minecraft/textures/blocks/torch_on.png','火把')
    extract(w,'minecraft:charcoal@0' if w=='dc2' else 'minecraft:coal@1',vanilla,'assets/minecraft/textures/item/charcoal.png' if w=='dc2' else 'assets/minecraft/textures/items/charcoal.png','木炭')
    if w=='m3e':extract(w,'minecraft:arrow@0',vanilla,'assets/minecraft/textures/items/arrow.png','箭')
    if w in ('mb','vw'):extract(w,'minecraft:ladder@0',vanilla,'assets/minecraft/textures/blocks/ladder.png','梯子')
extract('m3e','manametalmod:magicStone3@0',next((instances/'m3e/mods').glob('manametalmod-*.jar')),'assets/manametalmod/textures/blocks/magicStone3.png','祭坛符文石柱',True)
for id,label in [('magicStone1','祭坛石'),('ManaCrystal','魔力水晶')]:extract('m3e','manametalmod:'+id+'@0',next((instances/'m3e/mods').glob('manametalmod-*.jar')),'assets/manametalmod/textures/blocks/'+id+'.png',label,True)
extract('mb','erebus:planks_petrified_wood@0',next((instances/'mb/mods').glob('Erebus-*.jar')),'assets/erebus/textures/blocks/planks_petrified_wood.png','化石木板',True)
extract('mb','aoa3:emberstone_block@0',next((instances/'mb/mods').glob('AoA3-*.jar')),'assets/aoa3/textures/blocks/decoration/compressedblock/emberstone_block.png','余烬石块',True)
extract('mb','divinerpg:purple_steel@0',next((instances/'mb/mods').glob('DivineRPG-*.jar')),'assets/divinerpg/textures/blocks/purple_steel.png','紫色钢铁',True)
lso=next((instances/'dc2/mods').glob('legendarysurvivaloverhaul-*.jar'));ie=next((instances/'dc2/mods').glob('ImmersiveEngineering-*.jar'))
extract('dc2','legendarysurvivaloverhaul:purified_water_bottle@0',lso,'assets/legendarysurvivaloverhaul/textures/item/purified_water_bottle.png','净水瓶')
extract('dc2','legendarysurvivaloverhaul:bandage@0',lso,'assets/legendarysurvivaloverhaul/textures/item/healing/bandage.png','绷带')
for id,path,label in [('sheetmetal_iron','metal/sheetmetal_iron','铁钣金块'),('steel_scaffolding_standard','metal_decoration/steel_scaffolding','钢脚手架'),('fluid_pipe','metal_device/fluid_pipe','流体管道')]:extract('dc2','immersiveengineering:'+id+'@0',ie,'assets/immersiveengineering/textures/block/'+path+'.png',label,True)
extract('dc2','immersiveengineering:blastbrick@0',ie,'assets/immersiveengineering/textures/block/stone_decoration/blastbrick.png','高炉砖',True)
gt=next((root.parent/'_voidwayfarer4-deploy/release-r4-20260906/instance/mods').glob('gregtech_*.jar'))
for meta,folder,label in [(18102,'distillationtowerparts','蒸馏塔部件'),(18101,'heatacceptor','热能传输器'),(18000,'firebricks','耐火砖块')]:
    prefix='assets/gregtech/textures/blocks/machines/multiblockparts/'+folder+'/0/'
    extract('vw','gregtech:gt.multitileentity@'+str(meta),gt,prefix+'colored/side.png',label,True,prefix+'overlay/side.png')

from activity_reward_textures import extract_revision
extract_revision(root, instances, extract, icons, gt)
catalog=json.loads((root/'activities/catalog.json').read_text(encoding='utf-8'))
for world in catalog['worlds']:
    for reward in world['rewards']:
        for item in reward['items']:
            key=world['id']+':'+item['id']+'@'+str(item['meta'])
            assert key in icons,'Missing actual item texture: '+key
(root/'ui/src/activity-icons.json').write_text(json.dumps(icons,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
(root/'activities/texture-sources.json').write_text(json.dumps(sources,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Extracted',len(icons),'real item textures')
