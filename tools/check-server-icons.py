"""Read public Minecraft status icons without joining or restarting the servers."""
import base64,concurrent.futures,datetime,hashlib,io,json,pathlib,socket,struct,urllib.parse,urllib.request
from PIL import Image

ROOT=pathlib.Path(__file__).resolve().parents[1]
ROUTES={'m3e':['mc.m3e.boshan.uk','mc.m3e.bk.boshan.uk'],
        'dc2':['dc2.mc.boshan.uk','dc2.bk.mc.boshan.uk'],
        'mb':['mb.mc.boshan.uk','mb.bk.mc.boshan.uk']}

def varint(value):
    result=bytearray()
    while value>127:result.append((value&127)|128);value>>=7
    result.append(value)
    return bytes(result)

def read_exact(connection,size):
    result=bytearray()
    while len(result)<size:
        block=connection.recv(size-len(result))
        if not block:raise ValueError('Incomplete status response')
        result.extend(block)
    return bytes(result)

def read_varint(connection):
    value=0
    for shift in range(0,35,7):
        byte=read_exact(connection,1)[0];value|=(byte&127)<<shift
        if byte<128:return value
    raise ValueError('Invalid status length')

def pixel_hash(data):
    with Image.open(io.BytesIO(data)) as image:
        if image.format!='PNG' or image.size!=(64,64):raise ValueError('Unexpected favicon image')
        return hashlib.sha256(image.convert('RGBA').tobytes()).hexdigest()

def probe(instance,domain,expected):
    row={'instance':instance,'domain':domain}
    try:
        query=urllib.parse.urlencode({'name':'_minecraft._tcp.'+domain,'type':'SRV'})
        with urllib.request.urlopen('https://dns.google/resolve?'+query,timeout=15) as response:answers=json.load(response)
        records=[answer['data'].split() for answer in answers.get('Answer',[]) if answer['type']==33]
        if records:
            record=min(records,key=lambda entry:int(entry[0]));host=record[3].rstrip('.');port=int(record[2])
        else:host,port=domain,25565
        address=domain.encode('utf-8')
        handshake=b'\x00'+varint(47)+varint(len(address))+address+struct.pack('>H',port)+b'\x01'
        with socket.create_connection((host,port),timeout=15) as connection:
            connection.sendall(varint(len(handshake))+handshake+b'\x01\x00')
            length=read_varint(connection)
            if not 0<length<=1024*1024:raise ValueError('Unexpected status packet size')
            if read_varint(connection)!=0:raise ValueError('Unexpected status packet')
            size=read_varint(connection)
            if not 0<size<=length:raise ValueError('Invalid status JSON size')
            status=json.loads(read_exact(connection,size))
        favicon=status.get('favicon','')
        png=base64.b64decode(''.join(favicon.split(',',1)[1].split()),validate=True) if favicon.startswith('data:image/png;base64,') else None
        digest=hashlib.sha256(png).hexdigest() if png else None
        pixels=pixel_hash(png) if png else None
        row.update(reachable=True,onlinePlayers=status.get('players',{}).get('online'),hasIcon=bool(favicon),sha256=digest,pixelSha256=pixels,matchesApprovedIcon=pixels==expected)
    except Exception as error:
        row.update(reachable=False,error=type(error).__name__,matchesApprovedIcon=False)
    return row

if __name__=='__main__':
    report_path=ROOT/'packs/server-icons.json'
    report=json.loads(report_path.read_text(encoding='utf-8'))
    # Minecraft re-encodes the PNG; compare decoded pixels, not compression bytes.
    expected=pixel_hash((ROOT/report['source']).read_bytes())
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        futures=[pool.submit(probe,instance,domain,expected) for instance,domains in ROUTES.items() for domain in domains]
        rows=[future.result() for future in futures]
    report.update(pixelSha256=expected,liveStatusCheckedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),liveStatus=rows,
                  liveStatusIconVerified=all(row['matchesApprovedIcon'] for row in rows))
    report_path.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'liveStatusIconVerified':report['liveStatusIconVerified'],'routes':rows},ensure_ascii=False))
