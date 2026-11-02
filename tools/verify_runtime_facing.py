"""Inspect facial surface normals after each importer coordinate conversion."""
from pathlib import Path
import json
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"pet-backend"))
from runtime_models import read_pmx, read_fbx

results=[]
for relative in ("Litamodel/丽塔未变身1.0.pmx","model/Art_Evanescia_01/Art_Evanescia_01.fbx"):
    model=(read_pmx if relative.endswith(".pmx") else read_fbx)(ROOT/relative)
    start=0;rows=[]
    for material in model["materials"]:
        indices=set(model["indices"][start:start+material["count"]]);start+=material["count"]
        if not indices:continue
        rows.append({"name":material["name"],"vertices":len(indices),"normal":tuple(sum(model["vertices"][i][j] for i in indices)/len(indices) for j in (3,4,5)),
                     "centroid":tuple(sum(model["vertices"][i][j] for i in indices)/len(indices) for j in (0,1,2))})
    results.append({"source":relative,"materials":rows})
folder=ROOT/"verification/runtime-import"
folder.mkdir(parents=True,exist_ok=True)
folder.joinpath("facing-normals.json").write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding="utf-8")
print(json.dumps(results,ensure_ascii=True))
