"""Patch pinned TipTheScales bounds and execute the actual patched class without a display."""
import argparse,hashlib,json,os,subprocess,zipfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source',type=Path,required=True,help='Original TipTheScales 1.12.2-1.0.4 JAR, before applying the patch')
source=parser.parse_args().source
record={'path':'mods/TipTheScales-1.12.2-1.0.4.jar','sha256':'91eab825f0730d412a05a40136e9f0755b9a2b9483fc92255cba08e282a1e146'}
if hashlib.sha256(source.read_bytes()).hexdigest()!=record['sha256']:
    raise ValueError('Source JAR differs from the pinned TipTheScales release')
javac=next((ROOT.parent/'.tools/temurin25').glob('*/bin/javac.exe'));java=javac.with_name('java.exe')
stage=ROOT/'.local/gui-scale-fix';stage.mkdir(exist_ok=True)
asm=ROOT/'.local/engines/mb/libraries/org/ow2/asm'
cp=os.pathsep.join(str(next((asm/name).rglob('*.jar'))) for name in ['asm','asm-tree'])
def run(args):
    p=subprocess.run([str(x) for x in args],capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
    if p.returncode:raise RuntimeError((p.stdout+p.stderr).decode('utf-8',errors='replace'))
    return p.stdout.decode('utf-8',errors='replace')
run([javac,'-encoding','UTF-8','-cp',cp,'-d',stage,ROOT/'tools/GuiScalePatch.java'])
name='com/blamejared/tipthescales/client/GuiNewOptionsRowList.class'
with zipfile.ZipFile(source) as z:
    original=z.read(name);(stage/'original.class').write_bytes(original)
    run([java,'-cp',str(stage)+os.pathsep+cp,'GuiScalePatch',stage/'original.class',stage/'patched.class'])
    entries={i.filename:z.read(i) for i in z.infolist() if not i.is_dir()}
entries[name]=(stage/'patched.class').read_bytes()
# Stubs replace Minecraft/GUI constructors only; the class under test is the real patched JAR entry.
stubs={
'net/minecraft/client/Minecraft.java':'''package net.minecraft.client; public class Minecraft { public int field_71443_c,field_71440_d; public boolean unicode; public boolean func_152349_b(){return unicode;} }''',
'net/minecraft/client/settings/GameSettings.java':'''package net.minecraft.client.settings; public class GameSettings { public enum Options {GUI_SCALE; public int func_74381_c(){return ordinal();}}}''',
'net/minecraft/client/gui/GuiButton.java':'''package net.minecraft.client.gui; public class GuiButton {}''',
'net/minecraft/client/gui/GuiOptionsRowList.java':'''package net.minecraft.client.gui; import net.minecraft.client.Minecraft; import net.minecraft.client.settings.GameSettings; public class GuiOptionsRowList { public Minecraft field_148161_k; public GuiOptionsRowList(Minecraft mc,int a,int b,int c,int d,int e,GameSettings.Options...o){field_148161_k=mc;} public GuiButton func_148182_a(Minecraft mc,int a,int b,GameSettings.Options o){return null;} }''',
'com/blamejared/tipthescales/client/GuiNewOptionSlider.java':'''package com.blamejared.tipthescales.client; import net.minecraft.client.gui.GuiButton; import net.minecraft.client.settings.GameSettings; public class GuiNewOptionSlider extends GuiButton { public int maximum; public GuiNewOptionSlider(int id,int x,int y,GameSettings.Options o,int min,int max){maximum=max;} }''',
'BoundsCheck.java':'''import net.minecraft.client.Minecraft; import net.minecraft.client.settings.GameSettings; import com.blamejared.tipthescales.client.*; public class BoundsCheck { public static void main(String[] args){int[][] cases={{1280,720,0,3},{1920,1080,0,4},{2560,1440,0,6},{3840,2160,0,9},{3840,2160,1,8},{640,480,0,2}}; for(int[] c:cases){Minecraft mc=new Minecraft();mc.field_71443_c=c[0];mc.field_71440_d=c[1];mc.unicode=c[2]>0;GuiNewOptionsRowList list=new GuiNewOptionsRowList(mc,0,0,0,0,0,GameSettings.Options.GUI_SCALE);int actual=((GuiNewOptionSlider)list.func_148182_a(mc,0,0,GameSettings.Options.GUI_SCALE)).maximum;if(actual!=c[3])throw new AssertionError(actual+" != "+c[3]);} System.out.println("6 actual patched-class scale bounds passed in headless Java 25");}}'''
}
check=stage/'checks';check.mkdir(exist_ok=True)
for n,text in stubs.items():p=check/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(text,encoding='utf-8')
patched=check/name;patched.parent.mkdir(parents=True,exist_ok=True);patched.write_bytes(entries[name])
run([javac,'-cp',check,'-d',check,*[check/n for n in stubs]])
print(run([java,'-Djava.awt.headless=true','-cp',check,'BoundsCheck']).strip())
entries['META-INF/mojin-patches/GuiScalePatch.java']=(ROOT/'tools/GuiScalePatch.java').read_bytes()
entries['META-INF/mojin-patches/README.txt']=b'Modified TipTheScales 1.12.2-1.0.4: use the Minecraft framebuffer dimensions instead of AWT logical desktop size, and remove maximum - 1. Original project: https://github.com/jaredlll08/TipTheScales . License: LGPL-2.1. Reproducible patch source is included. Original attribution and other classes are unchanged.\n'
output=ROOT/'artifacts/game-integration/TipTheScales-1.12.2-1.0.4-mojin.1.jar'
with zipfile.ZipFile(output,'w',compression=zipfile.ZIP_DEFLATED) as z:
    for n,data in sorted(entries.items()):
        i=zipfile.ZipInfo(n,(2026,9,5,0,0,0));i.compress_type=zipfile.ZIP_DEFLATED;z.writestr(i,data)
receipt={'sourceSha256':record['sha256'],'sourcePath':record['path'],'path':'mods/'+output.name,'size':output.stat().st_size,'sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'patchedClass':name,'headlessActualClassCasesPassed':6,'liveHighDpiPlayerConfirmation':False,'gameplayClassesChanged':False}
(ROOT/'packs/revisions/mb-gui-scale.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
print(json.dumps(receipt))
