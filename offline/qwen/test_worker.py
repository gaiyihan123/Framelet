"""Exercise the installed Qwen CPU helper directly over its private stdio pipe."""
from pathlib import Path
import argparse,json,subprocess,importlib.util,hashlib
import socket
p=argparse.ArgumentParser();p.add_argument('--report',type=Path,required=True);p.add_argument('--runtime',type=Path,required=True);a=p.parse_args()
root=a.runtime;source=Path(__file__).resolve().parent;checks=[]
def check(ok,name):
    assert ok,name
    checks.append(name)
def run(model,text):
    return subprocess.run([str(root/'engine/QingTranslate.exe'),'--model',str(model)],input=text,text=True,encoding='utf-8',capture_output=True,timeout=45,creationflags=subprocess.CREATE_NO_WINDOW)
model=root/'model';items=[{'id':1,'text':'Do not close this window until the download is complete.'},{'id':2,'text':'The meeting is scheduled for the day after tomorrow at 9 a.m.'},{'id':3,'text':'Click Save to export the screenshot as a PNG image.'},{'id':4,'text':'The battery is not charging. Please connect the power adapter.'},{'id':5,'text':'You must not select Delete unless you have a backup.'}]
proc=run(model,''.join(json.dumps(x)+'\n' for x in items));lines=[json.loads(x) for x in proc.stdout.splitlines()]
check(proc.returncode==0,'Qwen helper exits cleanly when the parent closes stdin')
check(lines[0]['type']=='ready' and lines[0]['network_attempts']==0,'GGUF integrity and CPU model load succeed with socket audit enabled')
check(len(lines)==len(items)+1 and all(lines[i]['id']==i for i in range(1,len(lines))),'persistent protocol preserves ordered request IDs')
check('不要' in lines[1]['text'] and '下载' in lines[1]['text'],'live Qwen model preserves a negative instruction')
check('后天' in lines[2]['text'] and '上午9' in lines[2]['text'],'live Qwen model preserves the day-after-tomorrow and clock time')
check('保存' in lines[3]['text'] and 'PNG' in lines[3]['text'],'live Qwen model translates application controls')
translated=lines[4]['text']
check('电池' in translated and any(word in translated for word in ['未充电','没有充电','不充电']) and '电源适配器' in translated,'live Qwen model preserves a negative charging status and adapter instruction: '+translated)
warning=lines[5]['text']
check('删除' in warning and '备份' in warning and ('不要' in warning or '请勿' in warning or '否则' in warning),'live Qwen model preserves conditional deletion warning: '+warning)
check(all(x.get('network_attempts',-1)==0 for x in lines),'no Python network API attempt occurs during real inference')
proc=run(model,json.dumps({'id':1,'text':17})+'\n'+json.dumps({'id':2,'text':'Click Save to export a PNG image.'})+'\n')
lines=[json.loads(x) for x in proc.stdout.splitlines()]
check(lines[1]['type']=='error' and 'text' not in lines[1] and lines[2]['id']==2 and 'PNG' in lines[2]['text'],'malformed request is rejected without logging its contents and worker continues')
proc=run(root/'missing-model','')
check(proc.returncode==2 and json.loads(proc.stdout.splitlines()[0])['type']=='startup_error','missing model fails closed without downloads')
spec=importlib.util.spec_from_file_location('qwen_worker_audit',source/'worker.py')
worker=importlib.util.module_from_spec(spec);spec.loader.exec_module(worker)
denied=False
try:socket.socket()
except PermissionError:denied=True
check(denied and worker.NETWORK_ATTEMPTS==1,'Python socket API is actively blocked by the worker audit hook')
a.report.parent.mkdir(parents=True,exist_ok=True)
with open(model/'manifest.json',encoding='utf-8') as f:manifest=json.load(f)
report={'status':'passed','model':manifest['model'],'sha256':manifest['sha256'],'checks':checks,'count':len(checks),'sample_outputs':[{'id':x['id'],'translation':x['text']} for x in lines if x['type']=='result'],'network_calls':0,'notes':'The system Python audit hook rejects network calls; no OS firewall or network interface was changed.'}
a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'status':'passed','checks':len(checks),'network_calls':0}))
