"""Decode the stock planets from KSP 1.12.5's PSystem prefab (KSP_Data/sharedassets9.assets) and print them as JSON.

Read-only. Usage: python3 extract_stock_planets.py "<KSP>/KSP_Data" > stock_planets.json

Layouts (field order) come from KSP's Assembly-CSharp.dll:
  CelestialBody: bodyName, bodyDisplayName, bodyAdjectiveDisplayName, bodyDescription (strings), then doubles
                 GeeASL, Radius, Mass, Density, SurfaceArea, gravParameter, sphereOfInfluence, hillSphere, ...
  OrbitDriver:   pos, vel (Vector3d), orbit (Orbit, serialized inline)
  Orbit:         referenceBody (PPtr), inclination, eccentricity, semiMajorAxis, LAN, argumentOfPeriapsis, epoch,
                 [OrbitFrame is a non-serializable struct, skipped], pos, vel, 12 doubles, objectType (int32),
                 h, eccVec, an, meanAnomalyAtEpoch, ...
The stored gravParameter is 0; KSP computes it at load as Radius^2 * GeeASL * 9.80665 (CelestialBody.SetupConstants).
KSP names Eeloo's GameObject "Eloo".
"""
import sys, os, json
sys.path.insert(0, os.path.dirname(__file__))
from unityfile import load, R

data_dir = sys.argv[1]
main = load(os.path.join(data_dir, "sharedassets9.assets"))
cache = {}
def file_for(f, fid):
    if fid == 0: return f
    name = f["externals"][fid - 1]
    if name not in cache: cache[name] = load(os.path.join(data_dir, name))
    return cache[name]

def script_class(f, pptr):
    sf = file_for(f, pptr[0]); off, size, cid, _ = sf["objects"][pptr[1]]
    assert cid == 115
    r = R(sf["bytes"], off); r.astr(); r.i32(); r.p += 16  # m_Name, m_ExecutionOrder, m_PropertiesHash
    return r.astr()  # m_ClassName

def gameobject_name(f, pptr):
    sf = file_for(f, pptr[0]); off, size, cid, _ = sf["objects"][pptr[1]]
    assert cid == 1
    r = R(sf["bytes"], off); n = r.i32(); r.p += 12 * n; r.u32()  # m_Component (PPtr each), m_Layer
    return r.astr()

def mb_header(f, off):
    r = R(f["bytes"], off)
    go = r.pptr(); r.u8(); r.align(4); script = r.pptr(); name = r.astr()
    return r, go, script

bodies = {}
for pid, (off, size, cid, _) in main["objects"].items():
    if cid != 114: continue
    r, go, script = mb_header(main, off)
    cls = script_class(main, script)
    if cls not in ("CelestialBody", "OrbitDriver"): continue
    name = gameobject_name(main, go)
    b = bodies.setdefault(name, {})
    if cls == "CelestialBody":
        b["bodyName"] = r.astr(); r.astr(); r.astr(); r.astr()
        for k in ("GeeASL", "Radius", "Mass", "Density", "SurfaceArea", "gravParameter", "sphereOfInfluence", "hillSphere"):
            b[k] = r.f64()
    else:
        r.p += 48  # OrbitDriver.pos, vel (Vector3d x2)
        ref = r.pptr()
        el = {k: r.f64() for k in ("inclination", "eccentricity", "semiMajorAxis", "LAN", "argumentOfPeriapsis", "epoch")}
        r.p += 48 + 12 * 8 + 4 + 72  # Orbit.pos, vel; 12 doubles; objectType (int32); h, eccVec, an
        el["meanAnomalyAtEpoch"] = r.f64()
        el["referenceBodyPPtr"] = ref
        b["orbit"] = el
        b["_orbitdriver_pid"] = pid

# resolve referenceBody PPtr -> name (points at a CelestialBody MonoBehaviour)
cb_by_pid = {}
for pid, (off, size, cid, _) in main["objects"].items():
    if cid != 114: continue
    r, go, script = mb_header(main, off)
    if script_class(main, script) == "CelestialBody": cb_by_pid[pid] = gameobject_name(main, go)
for n, b in bodies.items():
    if "orbit" in b:
        fid, rp = b["orbit"].pop("referenceBodyPPtr")
        b["orbit"]["referenceBody"] = cb_by_pid.get(rp, f"?{fid}:{rp}") if fid == 0 else f"ext{fid}:{rp}"
    b.pop("_orbitdriver_pid", None)
json.dump(bodies, sys.stdout, indent=1, sort_keys=True)
