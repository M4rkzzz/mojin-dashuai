from pathlib import Path
import os,subprocess
root=Path(__file__).resolve().parents[3];java=next((root.parent/'.tools/temurin25').glob('*/bin'))
asm=list((root/'.local/engines/mb/libraries/org/ow2/asm').glob('**/9.10.1/*.jar'))
out=root/'.local/activities/test-classes';out.mkdir(parents=True,exist_ok=True)
cp=os.pathsep.join(map(str,[out,root/'.local/activities/classes',*asm]))
def run(args):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,encoding='utf-8',errors='replace',creationflags=0x08000000)
 if p.returncode:raise RuntimeError(p.stdout+p.stderr)
 print(p.stdout.strip())
run([java/'javac.exe','--release','8','-cp',cp,'-d',out,Path(__file__).with_name('ActivityClassCheck.java')])
base=root/'.local/complete-client-qa/全新整包安装/instances';targets=[]
for w in ['m3e','mb','dc2']:
 if w=='m3e':forge=next((root/'.local/engines/m3e/libraries/net/minecraftforge/forge').glob('*/*.jar'));common='cpw/mods/fml/common/FMLCommonHandler'
 elif w=='mb':forge=next((root/'.local/engines/mb/libraries/com/cleanroommc/cleanroom').glob('*/*.jar'));common='net/minecraftforge/fml/common/FMLCommonHandler'
 else:forge=next((root/'.local/engines/dc2/libraries/net/minecraftforge/forge').glob('*/*universal*.jar'));common='net/minecraftforge/event/ForgeEventFactory'
 targets.extend([forge,common,forge,'net/minecraftforge/common/ForgeHooks'])
 if w=='m3e':targets.extend([forge,'net/minecraftforge/event/entity/player/PlayerEvent$Clone'])
 quest=next((base/w/'mods').glob('ftb-quests-*.jar' if w=='dc2' else 'BetterQuesting*.jar'))
 targets.extend([quest,'dev/ftb/mods/ftbquests/quest/TeamData' if w=='dc2' else 'betterquesting/questing/QuestInstance'])
vw=next((root.parent/'_voidwayfarer4-deploy/release-r4-20260906/instance/mods').glob('BetterQuesting*.jar'));targets.extend([vw,'betterquesting/questing/QuestInstance'])
gt=next((root.parent/'_voidwayfarer4-deploy/release-r4-20260906/instance/mods').glob('gregtech_*.jar'));targets.extend([gt,'gregapi/tileentity/machines/MultiTileEntityBasicMachine'])
run([java/'java.exe','-cp',cp,'ActivityClassCheck',*targets])
