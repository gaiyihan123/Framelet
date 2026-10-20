"""Tests the actual frozen Windows helper; no online service is used."""
from pathlib import Path
import argparse, json, subprocess, sys, tempfile, importlib.util

p=argparse.ArgumentParser();p.add_argument('--report',type=Path,required=True);p.add_argument('--runtime',type=Path);args=p.parse_args()
source_root=Path(__file__).resolve().parent
root=args.runtime or source_root
checks=[]
def check(ok,name):
    assert ok,name
    checks.append(name)
def run(model,requests):
    proc=subprocess.run([str(root/'engine/QingTranslate.exe'),'--model',str(model)],input=requests,text=True,encoding='utf-8',capture_output=True,timeout=40,creationflags=subprocess.CREATE_NO_WINDOW)
    return proc,[json.loads(line) for line in proc.stdout.splitlines()]

inputs=[{'id':1,'text':'Do not close this window until the download is complete.'}, {'id':2,'text':'Click Save to export the screenshot as a PNG image.'}, {'id':3,'text':123}, {'id':4,'text':'The battery is not charging. Please connect the power adapter.'}]
proc,values=run(root/'model',''.join(json.dumps(x)+'\n' for x in inputs))
check(proc.returncode==0,'frozen helper exits normally on stdin EOF')
check(values[0]['type']=='ready' and values[0]['network_attempts']==0,'model loads with Python socket APIs denied and zero network attempts')
check(values[1]['id']==1 and '不要' in values[1]['text'],'real model output preserves tested negation')
check(values[2]['id']==2 and 'PNG' in values[2]['text'],'multiple UTF-8 JSON requests retain their IDs and outputs')
check(values[3]['type']=='error' and 'text' not in values[3],'invalid input gives a bounded error without echoing private text')
check(values[4]['type']=='result' and '电池' in values[4]['text'],'valid translation still works after malformed request')
check(all(x.get('network_attempts',0)==0 for x in values),'no Python network API attempted during real inference')
check('\u2581' not in values[4]['text'],'SentencePiece markers do not leak into user-visible text')
args.report.parent.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(prefix='model-negative-',dir=args.report.parent) as tmp:
    bad=Path(tmp)
    proc,error=run(bad,'')
    check(proc.returncode==2 and error[0]['type']=='startup_error','missing model fails closed without downloading anything')
    (bad/'metadata.json').write_text('corrupt',encoding='utf-8')
    (bad/'manifest.json').write_text(json.dumps({'from':'en','to':'zh','files':{'metadata.json':'0'*64}}),encoding='utf-8')
    proc,error=run(bad,'')
    check(proc.returncode==2 and error[0]['type']=='startup_error','corrupted model hash is rejected')
proc,error=run(root/'model',json.dumps({'id':1,'text':'a'*16001})+'\n')
check(error[1]['type']=='error','oversized input is rejected by the worker as well as the GUI adapter')
proc,error=run(root/'model','x'*262145+'\n')
check(proc.returncode==2 and error[-1]['code']=='request_too_large','oversized protocol line fails closed')

# Test the guard itself, separately from the successful-inference counter.
spec=importlib.util.spec_from_file_location('worker_guard',source_root/'worker.py')
guard=importlib.util.module_from_spec(spec);spec.loader.exec_module(guard)
import socket
denied=False
try: socket.socket()
except PermissionError: denied=True
check(denied and guard.NETWORK_ATTEMPTS==1,'network guard actively denies socket creation')
report={'status':'passed','checks':checks,'count':len(checks),'sample_outputs':values,'boundary':'Python audit-hook network denial, not an OS-level firewall or a physically disconnected PC.'}
args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'status':'passed','count':len(checks)}))
