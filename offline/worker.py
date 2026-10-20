"""Argos en->zh 1.9 model, using the same CTranslate2/SentencePiece backend.

No model downloader, HTTP API, telemetry or disk text cache. JSON lines on stdio
are private to the parent process. The Windows app bounds requests and kills the
worker on cancellation, timeout and exit. See MODEL-NOTICE.md for attribution.
"""
import sys
import os
import json
import hashlib
import re
from pathlib import Path

NETWORK_ATTEMPTS = 0
_NATIVE_ENGINE = None
def deny_network(event, args):
    global NETWORK_ATTEMPTS
    if event.startswith('socket.') or event.startswith('urllib.'):
        NETWORK_ATTEMPTS += 1
        raise PermissionError('Network is disabled in the offline translator')

sys.addaudithook(deny_network)
# Audit-hook rejection is an application guard, not an OS sandbox for native DLLs.
# Imports and model loading below must work with all Python socket APIs disabled.
os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['TRANSFORMERS_OFFLINE'] = '1'
os.environ['TOKENIZERS_PARALLELISM'] = 'false'
os.environ['OMP_NUM_THREADS'] = '4'

def emit(value):
    sys.stdout.write(json.dumps(value, ensure_ascii=True, separators=(',', ':'))+'\n')
    sys.stdout.flush()

def verified_model(folder):
    manifest=json.loads((folder/'manifest.json').read_text(encoding='utf-8'))
    if manifest.get('from') != 'en' or manifest.get('to') != 'zh':
        raise ValueError('Wrong model languages')
    for name, digest in manifest['files'].items():
        path=(folder/name).resolve()
        if not path.is_relative_to(folder.resolve()) or hashlib.sha256(path.read_bytes()).hexdigest()!=digest:
            raise ValueError('Model integrity check failed')

def token_chunks(text, tokenizer, limit=192):
    # Bound model input explicitly: CTranslate2's default can silently truncate.
    # Prefer natural sentence/line boundaries; split an oversized sentence into
    # consecutive token slices, never dropping its final tokens or Unicode text.
    for sentence in re.split(r'(?<=[.!?])\s+|[\r\n]+', text):
        if not sentence.strip():
            continue
        tokens=tokenizer.encode(sentence.strip(),out_type=str)
        for start in range(0,len(tokens),limit):
            yield tokens[start:start+limit]

def run():
    global _NATIVE_ENGINE
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8',line_buffering=True)
    sys.stderr.reconfigure(encoding='utf-8')
    if len(sys.argv)!=3 or sys.argv[1]!='--model':
        emit({'type':'startup_error','code':'arguments'})
        return 2
    folder=Path(sys.argv[2])
    try:
        verified_model(folder)
        import ctranslate2
        import sentencepiece
        tokenizer=sentencepiece.SentencePieceProcessor(model_file=str(folder/'sentencepiece.model'))
        engine=ctranslate2.Translator(str(folder/'model'),device='cpu',compute_type='int8',inter_threads=1,intra_threads=4)
        _NATIVE_ENGINE=engine  # Keep alive until process-level exit; avoid Ruy finalizer hang.
    except Exception:
        emit({'type':'startup_error','code':'model_or_runtime'})
        return 2
    emit({'type':'ready','protocol':1,'engine':'CTranslate2','model':'Argos en-zh 1.9','network_attempts':NETWORK_ATTEMPTS})
    while True:
        line=sys.stdin.readline(262145)
        if not line:
            return 0
        if len(line)>262144 or not line.endswith('\n'):
            emit({'type':'error','id':None,'code':'request_too_large'})
            return 2
        request={}
        try:
            request=json.loads(line)
            text=request['text']
            if type(request.get('id')) is not int or not isinstance(text,str) or len(text)>16000:
                raise ValueError('Invalid request')
            pieces=[]
            for tokens in token_chunks(text,tokenizer):
                result=engine.translate_batch([tokens],beam_size=4,replace_unknowns=True,length_penalty=0.2,max_input_length=0,max_decoding_length=512)[0]
                output=result.hypotheses[0]
                if len(output)>=512:
                    raise ValueError('Output limit reached')
                # The packaged tokenizer can emit a literal SentencePiece marker.
                # Match Argos' cleanup of that marker, but retain real underscores.
                decoded=tokenizer.decode(output).replace('\u2581',' ').strip()
                if tokens and not decoded:
                    raise ValueError('Empty translation')
                pieces.append(decoded)
            emit({'type':'result','id':request['id'],'text':' '.join(pieces),'network_attempts':NETWORK_ATTEMPTS})
        except Exception:
            # Never echo source text, file paths or a traceback to logs/protocol.
            emit({'type':'error','id':request.get('id'),'code':'translation_failed'})

if __name__=='__main__':
    code=run()
    sys.stdout.flush()
    sys.stderr.flush()
    # Ruy's native pool can hang during CPython interpreter finalization on
    # Windows. This dedicated process has no pending files or persistent state;
    # after completing the stdio protocol, let the OS reclaim all native state.
    os._exit(code)
