"""Small live protocol probe. The denial check never completes a game/world login."""
import argparse,datetime,json,socket,struct,uuid

SERVERS={'m3e':(25501,5),'dc2':(25502,763),'mb':(25503,340),'vw':(25504,5)}
def varint(number):
    result=bytearray()
    while True:
        value=number&127;number>>=7;result.append(value|(128 if number else 0))
        if not number:return bytes(result)
def string(text):
    data=text.encode('utf-8');return varint(len(data))+data
def packet(payload):return varint(len(payload))+payload
def read_exact(sock,count):
    data=b''
    while len(data)<count:
        chunk=sock.recv(count-len(data))
        if not chunk:raise EOFError('Connection closed')
        data+=chunk
    return data
def read_var(sock):
    value=0
    for n in range(5):
        b=read_exact(sock,1)[0];value|=(b&127)<<(7*n)
        if b<128:return value
    raise ValueError('Invalid VarInt')
def decode(data):
    value=0
    for n,b in enumerate(data[:5]):
        value|=(b&127)<<(7*n)
        if b<128:return value,n+1
    raise ValueError('Invalid VarInt')

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('instance',choices=SERVERS)
    parser.add_argument('--deny',action='store_true',help='Send an uncredentialed pre-login and require the explicit unified-launcher denial')
    args=parser.parse_args();port,protocol=SERVERS[args.instance]
    report={'instance':args.instance,'mode':'unauthorized-login' if args.deny else 'status','checkedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'passed':False}
    try:
        with socket.create_connection(('192.168.5.124',port),timeout=8) as sock:
            sock.settimeout(12)
            sock.sendall(packet(varint(0)+varint(protocol)+string('localhost')+struct.pack('>H',port)+varint(2 if args.deny else 1)))
            login=string('MojinJoinProbe')+(b'\x01'+uuid.UUID('b6d4255d-70a1-410f-b651-10b992418d05').bytes if protocol==763 else b'')
            sock.sendall(packet(varint(0)+(login if args.deny else b'')))
            size=read_var(sock)
            if not 0<size<=65536:raise ValueError('Unexpected response size')
            data=read_exact(sock,size);kind,offset=decode(data);length,used=decode(data[offset:]);offset+=used
            if kind!=0 or length>len(data)-offset:raise ValueError('Unexpected response packet')
            value=json.loads(data[offset:offset+length].decode('utf-8'))
            if args.deny:
                text=json.dumps(value,ensure_ascii=False)
                report.update(passed='魔金大帅' in text and '统一客户端' in text,denial=value)
            else:report.update(passed='version' in value and 'players' in value,protocol=value.get('version',{}).get('protocol'),online=value.get('players',{}).get('online'))
    except (OSError,EOFError,ValueError) as error:report['errorCategory']=type(error).__name__
    print(json.dumps(report,ensure_ascii=False));return 0 if report['passed'] else 1
if __name__=='__main__':raise SystemExit(main())
