from array import array
import json
import os
import struct
import sys
from xml.sax.saxutils import escape
from zipfile import ZipFile

output = sys.stdout.buffer
sys.stdout = sys.stderr

import torch

model_path, sample_rate_arg, verify_archive = sys.argv[1:]
if verify_archive == "1":
    with ZipFile(model_path) as archive:
        if archive.testzip() is not None:
            raise ValueError("Invalid Silero archive")

sample_rate = int(sample_rate_arg)
torch.set_num_threads(min(4, max(1, (os.cpu_count() or 2) // 2)))
model = torch.package.PackageImporter(model_path).load_pickle("tts_models", "model")
model.to(torch.device("cpu"))
# SSML in v5.6 expects an alphabet map even for voices without extensions.
for package in model.packages:
    if package.ext_alph is None:
        package.ext_alph = {}

for line in sys.stdin.buffer:
    try:
        request = json.loads(line)
        text = request["text"]
        rate = round(request["speed"] * 100)
        values = {"text": text} if rate == 100 else {
            "ssml_text": f'<speak><prosody rate="{rate}%">{escape(text)}</prosody></speak>'
        }
        with torch.inference_mode():
            audio = model.apply_tts(speaker=request["speaker"], sample_rate=sample_rate, **values)
            pcm = array("h", audio.clamp(-1, 1).mul(32767).to(torch.int16).tolist()).tobytes()
        output.write(struct.pack("<i", len(pcm)))
        output.write(pcm)
    except Exception:
        output.write(struct.pack("<i", -1))
    output.flush()
