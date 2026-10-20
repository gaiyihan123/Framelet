"""Private in-process adapter for the two packaged CPU translation models."""
import importlib.util
import gc
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent
QWEN = ROOT / 'qwen'
ARGOS = ROOT
SITE = ROOT / 'embedded' / 'site'
_dll_directories = [
    os.add_dll_directory(str(directory)) for directory in (
        ROOT / 'native',
        SITE / 'ctranslate2',
        SITE / 'llama_cpp' / 'lib',
    )
]
_network_attempts = 0

def _deny_network(event, _arguments):
    global _network_attempts
    if event.startswith(('socket.', 'urllib.')):
        _network_attempts += 1
        raise PermissionError('Network is disabled in the embedded translator')

sys.addaudithook(_deny_network)
os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['TRANSFORMERS_OFFLINE'] = '1'
os.environ['TOKENIZERS_PARALLELISM'] = 'false'
os.environ['OMP_NUM_THREADS'] = '4'

def _worker(name, source):
    spec = importlib.util.spec_from_file_location(name, source)
    if spec is None or spec.loader is None:
        raise RuntimeError('Packaged translation worker is missing')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

_qwen_worker = None
_qwen_engine = None
_argos_worker = None
_argos_engine = None
_argos_tokenizer = None

def _release_qwen():
    global _qwen_engine
    if _qwen_engine is not None:
        _qwen_engine.close()
        _qwen_engine = None

def _release_argos():
    # Keep the small CTranslate2 runtime context/pool alive: tearing down Ruy's
    # native pool on Windows can hang. Unload the weights, not the interpreter.
    if _argos_engine is not None and _argos_engine.model_is_loaded:
        _argos_engine.unload_model(to_cpu=False)

def model_state():
    return json.dumps({'qwen_loaded': _qwen_engine is not None,
                       'argos_loaded': _argos_engine is not None and _argos_engine.model_is_loaded,
                       'network_attempts': _network_attempts})

def release_models():
    # Only called with the app's inference gate and Python GIL held. Never close
    # a running inference, finalize CPython, unload native DLLs, or trim OS WS.
    _release_qwen()
    _release_argos()
    gc.collect()
    return model_state()

def translate_qwen(text):
    global _qwen_worker, _qwen_engine
    _release_argos()
    if _qwen_worker is None:
        _qwen_worker = _worker('_qing_qwen_core', QWEN / 'worker.py')
    if _qwen_engine is None:
        _qwen_engine, _ = _qwen_worker.load_engine(QWEN / 'model')
    result = _qwen_worker.translate(_qwen_engine, text)
    return json.dumps({'text': result, 'network_attempts': _network_attempts}, ensure_ascii=False)

def translate_argos(text):
    global _argos_worker, _argos_engine, _argos_tokenizer
    _release_qwen()
    gc.collect()
    if _argos_engine is None:
        _argos_worker = _worker('_qing_argos_core', ARGOS / 'worker.py')
        _argos_worker.verified_model(ARGOS / 'model')
        import ctranslate2
        import sentencepiece
        _argos_tokenizer = sentencepiece.SentencePieceProcessor(model_file=str(ARGOS / 'model' / 'sentencepiece.model'))
        _argos_engine = ctranslate2.Translator(str(ARGOS / 'model' / 'model'), device='cpu', compute_type='int8', inter_threads=1, intra_threads=4)
    elif not _argos_engine.model_is_loaded:
        _argos_engine.load_model(keep_cache=False)
    pieces = []
    for tokens in _argos_worker.token_chunks(text, _argos_tokenizer):
        output = _argos_engine.translate_batch([tokens], beam_size=4, replace_unknowns=True, length_penalty=0.2, max_input_length=0, max_decoding_length=512)[0].hypotheses[0]
        if len(output) >= 512:
            raise ValueError('Output limit reached')
        decoded = _argos_tokenizer.decode(output).replace('\u2581', ' ').strip()
        if tokens and not decoded:
            raise ValueError('Empty translation')
        pieces.append(decoded)
    return json.dumps({'text': ' '.join(pieces), 'network_attempts': _network_attempts}, ensure_ascii=False)
