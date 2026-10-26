"""Independent check of the ship numbers the Flip and Burn window shows for any ship in the test save.

Shares no code with the plugin. From primary sources only:
  - the ship's parts, their dry masses, resources and staging: the test save (saves/FlipAndBurn-Test/quicksave.sfs)
  - each engine's vacuum thrust, vacuum Isp and propellant mix: its part config in GameData
  - every resource's density: the RESOURCE_DEFINITION configs in GameData
  - the ship's total mass in flight and which engines were running: docs/test-evidence/test-run.log (the harness logs
    vessel.totalMass; module masses such as fairing panels aren't in the save)

Fuel reach is worked out here by walking the part tree from each running engine and stopping at decouplers (their
crossfeed is off by default), not by asking KSP. Then the rocket equation, phase by phase if the engines run dry at
different times.

Read-only. Standard library only. Usage:
  python3 check_engines.py <KSP dir> <test-run.log> "<ship name>" [<ship name> ...]
"""
import glob
import math
import os
import re
import sys

G0 = 9.80665
ONE_G = 9.81


def block_end(text, start):
    depth, i = 0, start
    while i < len(text):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return len(text)


def densities(ksp):
    out = {}
    for path in glob.glob(os.path.join(ksp, "GameData", "**", "*.cfg"), recursive=True):
        try:
            text = open(path, encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        for m in re.finditer(r"RESOURCE_DEFINITION\s*\{", text):
            body = text[m.end():block_end(text, m.end() - 1)]
            name = re.search(r"^\s*name\s*=\s*(\S+)", body, re.M)
            dens = re.search(r"^\s*density\s*=\s*([0-9.eE+-]+)", body, re.M)
            if name and dens:
                out[name.group(1)] = float(dens.group(1))
    return out


def engine_configs(ksp, wanted):
    """part name -> list of engine modules {engineID, maxThrust kN, isp, propellants[(name, ratio)]}."""
    out = {}
    for path in glob.glob(os.path.join(ksp, "GameData", "**", "*.cfg"), recursive=True):
        try:
            text = open(path, encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        for m in re.finditer(r"^\s*PART\s*\{", text, re.M):
            body = text[m.end():block_end(text, m.end() - 1)]
            name = re.search(r"^\s*name\s*=\s*(\S+)", body, re.M)
            if not name or name.group(1).replace("_", ".") not in wanted:
                continue
            mods = []
            for mm in re.finditer(r"MODULE\s*\{", body):
                mb = body[mm.end():block_end(body, mm.end() - 1)]
                mname = re.search(r"^\s*name\s*=\s*(\S+)", mb, re.M).group(1)
                if not mname.startswith("ModuleEngines"):
                    continue
                props = []
                for pm in re.finditer(r"PROPELLANT\s*\{", mb):
                    pb = mb[pm.end():block_end(mb, pm.end() - 1)]
                    props.append((re.search(r"name\s*=\s*(\S+)", pb).group(1), float(re.search(r"ratio\s*=\s*([0-9.]+)", pb).group(1))))
                curve = re.search(r"atmosphereCurve\s*\{(.*?)\}", mb, re.S).group(1)
                isp0 = float(re.search(r"key\s*=\s*0\s+([0-9.]+)", curve).group(1))
                eid = re.search(r"engineID\s*=\s*(\S+)", mb)
                mods.append(dict(module=mname, id=eid.group(1) if eid else "", thrust=float(re.search(r"maxThrust\s*=\s*([0-9.]+)", mb).group(1)),
                                 isp=isp0, props=props))
            out[name.group(1).replace("_", ".")] = mods
    return out


def vessel(sfs_text, ship):
    m = re.search(r"\n\t\tVESSEL\r?\n\t\t\{\r?\n\t\t\tpid = \S+\r?\n\t\t\tpersistentId = \S+\r?\n\t\t\tname = " + re.escape(ship) + r"\r?\n", sfs_text)
    start = sfs_text.rfind("VESSEL", 0, m.end())
    body = sfs_text[start:block_end(sfs_text, sfs_text.find("{", start))]
    parts = []
    for pm in re.finditer(r"\n\t\t\tPART\r?\n\t\t\t\{", body):
        pb = body[pm.end():block_end(body, pm.end() - 1)]
        get = lambda k: re.search(r"\n\t\t\t\t" + k + r" = (.*?)\r?\n", pb).group(1)
        res = {r.group(1): float(r.group(2)) for r in re.finditer(
            r"RESOURCE\r?\n\t\t\t\t\{\r?\n\t\t\t\t\tname = (\S+)\r?\n\t\t\t\t\tamount = (\S+)", pb)}
        mods = re.findall(r"MODULE\r?\n\t\t\t\t\{\r?\n\t\t\t\t\tname = (\S+)", pb)
        parts.append(dict(name=get("name"), parent=int(get("parent")), mass=float(get("mass")), istg=int(get("istg")), res=res, mods=mods))
    stg = int(re.search(r"\n\t\t\tstg = (\d+)", body).group(1))
    return parts, stg


def reach(parts, start):
    """Parts whose fuel the engine at index start can draw: the connected tree without crossing a decoupler."""
    children = {}
    for i, p in enumerate(parts):
        if i != 0:
            children.setdefault(p["parent"], []).append(i)
    blocked = lambda i: any("Decouple" in m for m in parts[i]["mods"])
    seen, todo = {start}, [start]
    while todo:
        i = todo.pop()
        for j in children.get(i, []) + ([parts[i]["parent"]] if i != 0 else []):
            if j in seen:
                continue
            seen.add(j)
            if not blocked(j):
                todo.append(j)
    return [i for i in seen if not blocked(i)]


def main():
    ksp, log_path, ships = sys.argv[1], sys.argv[2], sys.argv[3:]
    dens = densities(ksp)
    text = open(os.path.join(ksp, "saves", "FlipAndBurn-Test", "quicksave.sfs"), encoding="utf-8", errors="replace").read()
    log = open(log_path, encoding="utf-8", errors="replace").read()
    for ship in ships:
        parts, stg = vessel(text, ship)
        print(f"\n== {ship}: {len(parts)} parts, current stage {stg}")
        sec = log[log.find(f"delta-v sources for '{ship}'"):]
        total_t = float(re.search(r"diag mass: vessel.totalMass = ([0-9.Ee+-]+) t", sec).group(1))
        diag = sec[:sec.find("diag FlipAndBurn")]
        running = re.findall(r"diag engine: part=(\S+) module=(\S+) ignited=True operational=True", diag)
        live = {m.group(1): m for m in re.finditer(
            r"diag engine: part=(\S+) module=\S+ ignited=True operational=True flameout=\w+ maxThrust=([0-9.Ee+-]+) kN "
            r"thrustPercentage=([0-9.]+) vacuumIsp=([0-9.Ee+-]+) s .*? propellants=(\S+)", diag)}
        save_mass = sum(p["mass"] for p in parts) + sum(a * dens.get(r, 0) for p in parts for r, a in p["res"].items())
        print(f"  mass: parts + resources in the save {save_mass:.4f} t; in flight (vessel.totalMass) {total_t:.4f} t; "
              f"difference {total_t - save_mass:.4f} t (module masses such as fairing panels)")
        cfgs = engine_configs(ksp, {r[0] for r in running})
        engines = []
        for part_name, module in running:
            idx = next(i for i, p in enumerate(parts) if p["name"] == part_name and i not in [e["index"] for e in engines])
            cfg = next((c for c in cfgs.get(part_name, []) if c["module"] == module), None)
            if module not in ("ModuleEngines", "ModuleEnginesFX") and part_name in live:
                # Another mod's engine module rewrites its figures at runtime (KSPIE does), so its part config can't
                # reproduce them. Use what the module reported in flight: KSP's own figures, which is all anyone has.
                lm = live[part_name]
                props = [(x.split(":")[0], float(x.split(":")[1])) for x in lm.group(5).split(",")]
                cfg = dict(module=module, id="", thrust=float(lm.group(2)) * float(lm.group(3)) / 100, isp=float(lm.group(4)), props=props)
                print(f"  {part_name} ({module}): not a stock engine module; using its in-flight figures from the log")
            if cfg is None:
                print(f"  {part_name}: no stock-style engine config found; not checked independently")
                continue
            r = reach(parts, idx)
            avail = {}
            for i in r:
                for res, a in parts[i]["res"].items():
                    avail[res] = avail.get(res, 0) + a
            flow = cfg["thrust"] * 1000 / (cfg["isp"] * G0)
            mix = sum(ratio * dens.get(n, 0) * 1000 for n, ratio in cfg["props"])
            rates = {n: flow / mix * ratio for n, ratio in cfg["props"] if dens.get(n, 0) > 0}
            engines.append(dict(index=idx, name=part_name, thrust=cfg["thrust"] * 1000, isp=cfg["isp"], flow=flow, rates=rates, avail=avail))
            print(f"  {part_name} ({module}): {cfg['thrust']} kN, Isp {cfg['isp']} s, mix {cfg['props']}; "
                  f"reaches {', '.join(sorted(parts[i]['name'] for i in r if parts[i]['res']))}: "
                  + ", ".join(f"{n} {avail.get(n, 0):.2f}" for n in rates))
        if not engines:
            continue
        left = {}
        for e in engines:
            for n in e["rates"]:
                left[n] = max(left.get(n, 0), e["avail"].get(n, 0))
        m = total_t * 1000
        active, phases = list(engines), []
        while active:
            rates = {}
            for e in active:
                for n, r in e["rates"].items():
                    rates[n] = rates.get(n, 0) + r
            t = min(left[n] / r for n, r in rates.items())
            used = sum(e["flow"] for e in active) * t
            f = sum(e["thrust"] for e in active)
            phases.append((f, f / (sum(e["flow"] for e in active) * G0), m, m - used))
            m -= used
            for n, r in rates.items():
                left[n] -= r * t
            active = [e for e in active if all(left[n] > 1e-9 for n in e["rates"])]
            if t <= 0:
                break
        full = sum(isp * G0 * math.log(a / b) for f, isp, a, b in phases if b < a)
        at1g = 0.0
        for f, isp, a, b in phases:
            if f / a < ONE_G:
                break
            at1g += isp * G0 * math.log(a / b)
        stages_below = sorted({p["istg"] for p in parts if 0 <= p["istg"] < stg and
                               (any(x.startswith("ModuleEngines") for x in p["mods"]) or any("Decouple" in x for x in p["mods"]))})
        for k, (f, isp, a, b) in enumerate(phases):
            print(f"  phase {k + 1}: thrust {f / 1000:.1f} kN, Isp {isp:.2f} s, mass {a:.1f} -> {b:.1f} kg")
        print(f"  max acceleration {phases[0][0] / phases[0][2]:.4f} m/s^2 = {phases[0][0] / phases[0][2] / ONE_G:.4f} g")
        print(f"  delta-v at full thrust {full:.1f} m/s; at 1 g {at1g:.1f} m/s"
              + ("" if phases[0][0] / phases[0][2] >= ONE_G else " (can't hold 1 g)"))
        print(f"  stages below the current one, with engines or decouplers: {len(stages_below)} {stages_below}")


if __name__ == "__main__":
    main()
