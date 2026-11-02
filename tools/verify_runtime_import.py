"""Read real PMX/FBX fixtures without changing source files or the user catalog."""
from pathlib import Path
import json
import sys
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"pet-backend"))
from model_library import ModelLibrary

workspace=ROOT/"verification/runtime-import/backend-queue"
workspace.mkdir(parents=True,exist_ok=True)
library=ModelLibrary(workspace)
results=[]
try:
    for relative in ("Litamodel/丽塔未变身1.0.pmx","model/Art_Evanescia_01/Art_Evanescia_01.fbx"):
        source=ROOT/relative
        before=(source.stat().st_size,source.stat().st_mtime_ns)
        job=library.start(str(source));deadline=time.monotonic()+120
        while time.monotonic()<deadline:
            result=library.status(job["job_id"])
            if result["status"] in ("completed","failed"):break
            time.sleep(.1)
        assert result["status"]=="completed",result
        assert before==(source.stat().st_size,source.stat().st_mtime_ns)
        entry=next(entry for entry in library.models() if entry["id"]==result["model_id"])
        results.append({"source":relative,"status":result["status"],"runtime_file":entry["bundle_path"],"source_preserved":True})
    workspace.joinpath("backend-queue-results.json").write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding="utf-8")
    print(json.dumps(results,ensure_ascii=False))
finally:library.worker.shutdown(wait=True)
