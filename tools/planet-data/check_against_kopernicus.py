"""Check decoded planet data against the sphere of influence KSP computed at runtime (logged by Kopernicus).

Read-only. Usage: python3 check_against_kopernicus.py stock_planets.json "<KSP>/Logs/Kopernicus/Kopernicus.log"

SOI = a * (mu / muSun)^0.4 with mu = Radius^2 * GeeASL * 9.80665, as KSP computes them. OPM planets are read from
GameData/OPM/KopernicusConfigs/OuterPlanets/*.cfg (values copied below).
"""
import json, re, sys

G0 = 9.80665  # PhysicsGlobals.GravitationalAcceleration
stock = json.load(open(sys.argv[1]))
runtime = {m.group(1): float(m.group(2))
           for m in re.finditer(r"Found Body: (\w+):\d+ -> SOI = ([0-9.Ee+]+)", open(sys.argv[2]).read())}
opm = {  # radius, geeASL, semiMajorAxis from GameData/OPM/KopernicusConfigs/OuterPlanets/<name>.cfg
    "Sarnus": (5300000, 0.298, 125798522368), "Urlum": (2177000, 0.257, 254317012787),
    "Neidon": (2145000, 0.314, 409355191706), "Plock": (189000, 0.148, 535833706086)}

mu = lambda r, g: r * r * g * G0
mu_sun = mu(stock["Sun"]["Radius"], stock["Sun"]["GeeASL"])
rows = [(n, b["Radius"], b["GeeASL"], b["orbit"]["semiMajorAxis"]) for n, b in sorted(stock.items())
        if b.get("orbit", {}).get("referenceBody") == "Sun"] + [(n, *v) for n, v in opm.items()]
worst = 0.0
for name, r, g, a in rows:
    key = "Eeloo" if name == "Eloo" else name
    soi = a * (mu(r, g) / mu_sun) ** 0.4
    if key in runtime and not (key == "Eeloo" and "Sarnus" in runtime):
        err = abs(soi / runtime[key] - 1)
        worst = max(worst, err)
        print(f"{key:7s} SOI {soi:20.6f}  runtime {runtime[key]:20.6f}  rel.err {err:.1e}")
    else:
        print(f"{key:7s} SOI {soi:20.6f}  (no comparable runtime value; under OPM Eeloo orbits Sarnus)")
print(f"worst relative error: {worst:.1e}")
sys.exit(0 if worst < 1e-12 else 1)
