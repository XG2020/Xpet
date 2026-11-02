"""Local model-pack library and an explicit, serialized import queue."""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from threading import RLock
from uuid import uuid4

from fastapi import APIRouter
from pydantic import BaseModel, Field
from config import BACKEND_DIR
from errors import ServiceError
from runtime_models import convert_model


class ModelLibrary:
    def __init__(self, workspace: Path):
        self.workspace = workspace.resolve()
        self.root = self.workspace / "pet-models"
        self.project = self.workspace / "pet-frontend" / "pet-frontend"
        self.jobs: dict[str, dict] = {}
        self.lock = RLock()
        self.worker = ThreadPoolExecutor(max_workers=1, thread_name_prefix="pet-model-import")

    def models(self) -> list[dict]:
        result = []
        if not self.root.exists():
            return result
        for metadata in sorted(self.root.glob("*/model.json")):
            try:
                data = json.loads(metadata.read_text(encoding="utf-8-sig"))
                bundle = (metadata.parent / data["bundle_file"]).resolve()
                if bundle.parent != metadata.parent.resolve() or bundle.suffix.lower() not in {".petmodel", ".petraw"} or not bundle.is_file():
                    continue
                result.append({"id": metadata.parent.name, "label": str(data.get("label", metadata.parent.name))[:80],
                               "bundle_path": str(bundle), "source_type": data.get("source_type", "petmodel"),
                               **{key:data[key] for key in ('target_height_m','rig_source','rig_profile','adaptation_limitations') if key in data}})
            except (ValueError, KeyError, TypeError, OSError):
                continue
        return result

    def start(self, raw_path: str) -> dict:
        source = Path(raw_path)
        if not source.is_absolute() or str(source).startswith(("\\\\", "//")):
            raise ServiceError("请选择本机模型文件的完整路径。", 422)
        source = source.resolve()
        if source.suffix.lower() not in {".pmx", ".fbx", ".glb", ".petmodel"} or not source.is_file():
            raise ServiceError("请选择 PMX、FBX、GLB 或 PETMODEL 模型文件。", 422)
        if source.stat().st_size > 1024 * 1024 * 1024:
            raise ServiceError("单个模型文件不能超过 1 GB。", 422)
        with self.lock:
            active = [j for j in self.jobs.values() if j["status"] not in {"completed", "failed"}]
            for job in active:
                if job.get("source") == str(source): return self.public(job)
            if len(active) >= 4: raise ServiceError("模型导入队列已满，请等待当前导入完成。", 429)
            job_id = uuid4().hex
            identity = hashlib.sha256((str(source).casefold() + str(source.stat().st_mtime_ns)).encode()).hexdigest()[:16]
            job = {"job_id": job_id, "model_id": "model-" + identity, "label": source.stem[:80],
                   "status": "queued", "error": "", "source": str(source)}
            self.jobs[job_id] = job
            self.worker.submit(self._run, job_id, source)
            return self.public(job)

    @staticmethod
    def public(job: dict) -> dict:
        return {key: job[key] for key in ("job_id", "model_id", "label", "status", "error")}

    def status(self, job_id: str) -> dict:
        with self.lock:
            if job_id not in self.jobs: raise ServiceError("找不到这个导入任务，请刷新模型列表。", 404)
            return self.public(self.jobs[job_id])

    def _set(self, job: dict, status: str, error: str = ""):
        with self.lock:
            job["status"], job["error"] = status, error

    def _run(self, job_id: str, source: Path):
        job = self.jobs[job_id]
        output = self.root / job["model_id"]
        try:
            if source.suffix.lower() == ".petmodel":
                self._set(job, "importing")
                with source.open("rb") as stream:
                    if not stream.read(8).startswith(b"UnityFS"):
                        raise ServiceError("文件不是有效的 Unity 模型包。", 422)
                output.mkdir(parents=True, exist_ok=True)
                target = output / (job["model_id"] + ".petmodel")
                temporary = target.with_suffix(".tmp")
                shutil.copy2(source, temporary); temporary.replace(target)
                self._json(output / "model.json", {"id": job["model_id"], "label": job["label"], "bundle_file": target.name, "source_type": "petmodel"})
            else:
                output.mkdir(parents=True, exist_ok=True)
                self._set(job, "converting")
                if source.suffix.lower() == ".fbx":
                    command = [sys.executable,"--convert-model",str(source),"--output-dir",str(output)] if getattr(sys,"frozen",False) else [
                        sys.executable,str(Path(__file__).with_name("runtime_models.py")),"--source",str(source),"--output",str(output)]
                    self.root.joinpath(".imports").mkdir(parents=True,exist_ok=True)
                    log_file=self.root/".imports"/(job_id+".log")
                    with log_file.open("wb") as log:
                        completed=subprocess.run(command,cwd=self.workspace,stdout=log,stderr=subprocess.STDOUT,timeout=120,
                            creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
                    try:metrics=json.loads(output.joinpath("conversion-metrics.json").read_text(encoding="utf-8"))
                    except (OSError,ValueError):metrics={}
                    if completed.returncode!=0 or "vertices" not in metrics:
                        raise ServiceError(str(metrics.get("error") or "FBX 导入组件无法读取模型；请确认骨架、贴图和文件格式。")[:1200],422)
                else:metrics = convert_model(source, output)
                if metrics.get('display_name'):
                    with self.lock:job['label']=str(metrics['display_name'])[:80]
                self._json(output / "model.json", {"id": job["model_id"], "label": job["label"], "bundle_file": "model.petraw",
                    "source_type": source.suffix.lower()[1:], "runtime_format": "PETRAW1", **metrics})
                if not any(m["id"] == job["model_id"] for m in self.models()):
                    raise ServiceError("导入未生成可用模型包，原模型保持不变。", 500)
            self._set(job, "completed")
        except ServiceError as error: self._set(job, "failed", error.message)
        except subprocess.TimeoutExpired: self._set(job, "failed", "FBX 导入超时，转换进程已停止；原模型文件未修改。")
        except (ValueError, RuntimeError, OSError) as error: self._set(job, "failed", str(error)[:1200])
        except Exception: self._set(job, "failed", "模型导入失败，请检查模型格式、贴图和文件权限。")

    @staticmethod
    def _json(path: Path, data: dict):
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        temporary.replace(path)


from config import LIBRARY_DIR
library = ModelLibrary(LIBRARY_DIR)
router = APIRouter(prefix="/api/models")

class ModelImportRequest(BaseModel):
    path: str = Field(min_length=1, max_length=4096)

@router.get("")
def list_models(): return {"models": library.models()}

@router.post("/import", status_code=202)
def import_model(request: ModelImportRequest): return library.start(request.path)

@router.get("/jobs/{job_id}")
def import_status(job_id: str): return library.status(job_id)
