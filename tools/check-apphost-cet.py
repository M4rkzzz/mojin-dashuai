"""Read the actual native PE CET bit rather than trusting an incremental build property."""
import argparse, hashlib, json, struct
from pathlib import Path

def inspect(path):
    return inspect_bytes(path.read_bytes())

def inspect_bytes(data):
    if data[:2]!=b'MZ':raise ValueError('Not a PE executable')
    pos=struct.unpack_from('<I',data,0x3c)[0]
    if data[pos:pos+4]!=b'PE\0\0':raise ValueError('Invalid PE signature')
    coff=pos+4
    count=struct.unpack_from('<H',data,coff+2)[0]
    optional_size=struct.unpack_from('<H',data,coff+16)[0]
    opt=coff+20
    magic=struct.unpack_from('<H',data,opt)[0]
    if magic not in (0x10b,0x20b):raise ValueError('Unknown PE format')
    dirs=opt+(112 if magic==0x20b else 96)
    rva,size=struct.unpack_from('<II',data,dirs+6*8)
    sections=opt+optional_size
    def offset(rva):
        for i in range(count):
            start=sections+40*i
            vsize,va,rawsize,raw=struct.unpack_from('<IIII',data,start+8)
            if va<=rva<va+max(vsize,rawsize):return raw+rva-va
        raise ValueError('Debug data RVA not mapped')
    extended=[]
    for n in range(size//28):
        item=offset(rva)+28*n
        kind,length,_,raw=struct.unpack_from('<IIII',data,item+12)
        if kind==20:
            if length<4:raise ValueError('Extended characteristics too short')
            extended.append({'offset':raw,'flags':struct.unpack_from('<I',data,raw)[0]})
    if len(extended)!=1:raise ValueError('Expected exactly one extended DLL characteristics entry')
    return {'sha256':hashlib.sha256(data).hexdigest(),'size':len(data),'dllCharacteristics':struct.unpack_from('<H',data,opt+70)[0],
            'extendedDllCharacteristics':extended,'cetCompatible':bool(extended[0]['flags']&1)}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable',type=Path)
    parser.add_argument('--expected',choices=('enabled','disabled'),required=True)
    args=parser.parse_args();result=inspect(args.executable)
    if result['cetCompatible']!=(args.expected=='enabled'):raise SystemExit('Native CET flag mismatch')
    print(json.dumps(result))
