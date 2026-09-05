export type Profile = { id: string; loginName: string; gameName: string; kind?: 'microsoft' | 'hub' };
export type SkinTexture = {pngBase64:string;model:'classic'|'slim'};
export type MicrosoftCode = {userCode:string;verificationUrl:string;expiresAt:string};
export type Progress = { instance: string; phase: string; completed: number; total: number; bytesPerSecond: number; paused?: boolean };
export type ContentUpdate = {version:string;sequence:number};
export type InstanceStatus = {installs:Record<string,{version:string;sequence?:number;state:string}>;states:Record<string,string>;progress:Record<string,Progress>;availableUpdates?:Record<string,ContentUpdate>};
export type NetworkDiagnostic = {id:string;stage:string;category:string;host?:string;path?:string;httpStatus?:number;code:string;proxyMode:string;file?:string;attempt?:number};
export type BridgeError = Error & {diagnostic?:NetworkDiagnostic};
export type Settings = { root: string; contentDirectoryConfigured: boolean; memory: Record<string, number>; java: Record<string, string>; jvm: Record<string, string>; width: number; height: number; fullscreen: boolean; preferDedicatedGpu:boolean; windowBehavior: string; concurrency: number; limitMiB: number; proxy: string; proxyMode:'direct'|'system'|'manual'; skinSource:'account'|'littleskin'; reducedMotion: boolean; theme: string; selectedRoutes: Record<string, string> };
declare global { interface Window { chrome?: { webview?: { postMessage: (value: unknown) => void; addEventListener: (name: string, cb: (event: {data: any}) => void) => void } } } }
const pending = new Map<string, {resolve: (value: any) => void; reject: (reason: Error) => void}>();
const events = new Set<(data: any) => void>();
window.chrome?.webview?.addEventListener('message', ({data}) => {
  if (data.event) { for (const cb of events) cb(data); return; }
  const task = pending.get(data.id); if (!task) return;
  pending.delete(data.id); data.ok ? task.resolve(data.result) : task.reject(Object.assign(new Error(data.error || '操作未完成，请重试。'),{diagnostic:data.diagnostic}));
});
export const isNative = !!window.chrome?.webview;
export function invoke<T = any>(command: string, args: unknown = {}): Promise<T> {
  if (!isNative) return Promise.reject(new Error('当前为界面预览。请在 Windows 启动器中使用此功能。'));
  return new Promise((resolve, reject) => { const id = crypto.randomUUID(); pending.set(id, {resolve, reject}); window.chrome!.webview!.postMessage({id, command, args}); });
}
export function subscribe(cb: (data: any) => void) { events.add(cb); return () => {events.delete(cb);}; }
export const defaultSettings: Settings = { root: '', contentDirectoryConfigured:false, memory: {m3e:8192, dc2:8192, mb:8736, vw:4096}, java: {m3e:'', dc2:'', mb:'',vw:''}, jvm: {m3e:'',dc2:'',mb:'-XX:+UseZGC',vw:''}, width:1280, height:720, fullscreen:false, preferDedicatedGpu:true,windowBehavior:'keep', concurrency:4, limitMiB:0, proxy:'', proxyMode:'direct',skinSource:'account',reducedMotion:false, theme:'dark', selectedRoutes:{m3e:'auto', dc2:'auto', mb:'auto',vw:'auto'} };
