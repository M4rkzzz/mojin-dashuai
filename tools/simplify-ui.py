from pathlib import Path
path=Path(__file__).resolve().parents[1]/'ui/src/main.tsx'
text=path.read_text(encoding='utf-8')
text=text.replace('>魔金大帅<span>三服统一客户端</span>','>魔金大帅')
text=text.replace('<em>早期预览 · 0.1.0</em>','')
text=text.replace('<span className="quiet-label"><Globe2 size={14}/> 三个世界，一处出发</span>','')
start='<div className="login-story">'
end='</div></div><footer>'
if start in text:
    a=text.index(start);b=text.index(end,a)
    text=text[:a]+text[b+6:]
text=text.replace('<span>与伙伴一起，探索更多可能。</span><span>Windows x64 · 自动配置运行环境</span>','')
text=text.replace('<span className="status-label"><span className="tiny-dot"/> 准备好出发了吗</span>','')
text=text.replace('<span className="eyebrow">CHOOSE YOUR WORLD</span><h1>世界很大，一起去看看。</h1><p>选择你的下一场冒险，其余的交给启动器。</p>','<h1>选择服务器</h1>')
text=text.replace('<span className="outline-badge"><Download size={14}/> 选好再下载</span>','')
text=text.replace('<span className="card-en">{w.en}</span>','')
text=text.replace('<p>{w.tagline}</p>','')
text=text.replace('<div className="card-top"><span>WORLD 0{i+1}</span>','<div className="card-top"><span>0{i+1}</span>')
a=text.find('<div className="lobby-foot">')
if a>=0:
    b=text.index('</>:',a)
    text=text[:a]+text[b:]
text=text.replace('<div className="main-footer"><span>魔金大帅</span><span>一起创造，下一种可能 <span className="footer-spark">✦</span></span></div>','')
text=text.replace('<span className="eyebrow">WELCOME BACK</span>','')
text=text.replace("{mode==='login'?'欢迎回家。':mode==='register'?'故事，从你开始。':'找回你的旅程。'}","{mode==='login'?'登录':mode==='register'?'注册':'找回密码'}")
a=text.find('<p className="login-subtitle">');b=text.find('</p>',a)
if a>=0:text=text[:a]+text[b+4:]
text=text.replace('<div className="login-assurance"><ShieldCheck size={13}/><span>账号凭据由启动器安全保存</span></div>','')
text=text.replace('<span className="eyebrow">{w.en}</span>','')
text=text.replace('<p>{w.desc}</p>','')
text=text.replace('<span className="eyebrow">FIND YOUR CONNECTION</span>','')
text=text.replace('<h2>选择一条线路</h2>','<h2>线路</h2>')
text=text.replace('<span>自动选择<span>按本次可达性与延迟选择</span></span>','<span>自动选择</span>')
a=text.find('<div className="world-intro">');b=text.find('</div></div><aside',a)
if a>=0:text=text[:a]+text[b+6:]
text=text.replace('Java {w.java} · 自动配置','Java {w.java} x64')
text=text.replace('<p className="launch-hint">{install?\'使用已保存的账号与线路\':\'仅下载这个世界及配套 Java\'}</p>','')
text=text.replace('<span className="eyebrow">MAKE YOURSELF AT HOME</span><h1>按你的习惯来。</h1><p>运行环境自动就位，也为你的偏好留出空间。</p>','<h1>设置</h1>')
text=text.replace('{desc&&<p>{desc}</p>}','')
text=text.replace('<span>设置在三个世界之间独立保存。</span>','')
text=text.replace('<div className="settings-tip"><ShieldCheck size={20}/><p>下载支持断点续传与损坏重试。文件校验完成后才会应用更新。</p></div>','')
text=text.replace('<span>让每次出发，都充满期待。</span>','')
text=text.replace('下载速度上限','下载限速（MiB/s）')
text=text.replace('新的入口，熟悉的伙伴。','公告')
path.write_text(text,encoding='utf-8')
