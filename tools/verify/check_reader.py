"""Independent check of the delta-v Flip and Burn shows for a KSP Interstellar fusion drive (cycle 11 reader fix).

Shares no code with the plugin. From primary sources only:
  - the drive's thrust and Isp: its part config (FusionEngineController maxThrustMk7, thrustIspMk7), scaled by the part's
    TweakScale size in the test save (saves/FlipAndBurn-Test/quicksave.sfs) with the config's scale exponents, as KSP
    Interstellar's DaedalusEngineController.OnRescale does. The test save is a sandbox game, so every upgrade is in: Mk7.
  - the throttle rule: KSP Interstellar's default ispThrottleExponent, 0.5 (the part config doesn't set it):
    Isp = engine Isp x throttle^0.5. Holding acceleration a, throttle = m a / F, so
    delta-v = 2 Isp g0 sqrt(a / F) (sqrt(m_start) - sqrt(m_end)) while the throttle stays at or under 1.
  - the propellant: LqdDeuterium in the save (and, for the state while firing, the amount the harness logged then), with
    its density from the RESOURCE_DEFINITION configs.
  - the ship's total mass: vessel.totalMass as the harness logged it (module masses aren't in the save).

Read-only. Standard library only. Usage:
  python3 check_reader.py <KSP dir> <run log>
"""
import glob
import math
import os
import re
import sys

G0 = 9.80665
ONE_G = 9.81
SHIP = "Bebop Refueller"
PART = "KspiEpsteinUDD"


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


def density(ksp, resource):
    for path in glob.glob(os.path.join(ksp, "GameData", "**", "*.cfg"), recursive=True):
        text = open(path, encoding="utf-8", errors="replace").read()
        for m in re.finditer(r"RESOURCE_DEFINITION\s*\{", text):
            body = text[m.end():block_end(text, m.end() - 1)]
            if re.search(rf"^\s*name\s*=\s*{resource}\s*$", body, re.M):
                return float(re.search(r"^\s*density\s*=\s*([0-9.eE+-]+)", body, re.M).group(1))
    raise SystemExit(f"no density for {resource}")


def drive_config(ksp):
    for path in glob.glob(os.path.join(ksp, "GameData", "**", "*.cfg"), recursive=True):
        text = open(path, encoding="utf-8", errors="replace").read()
        if not re.search(rf"^\s*name\s*=\s*{PART}\s*$", text, re.M):
            continue
        m = re.search(r"name\s*=\s*FusionEngineController", text)
        body = text[m.end():block_end(text, text.rfind("{", 0, m.start()))]

        def num(key):
            return float(re.search(rf"^\s*{key}\s*=\s*([0-9.eE+-]+)", body, re.M).group(1))

        scale = re.search(r"name\s*=\s*TweakScale.*?defaultScale\s*=\s*([0-9.]+)", text, re.S)
        return dict(path=path, thrust=num("maxThrustMk7"), isp=num("thrustIspMk7"),
                    thrust_lo=num("lowerScaleThrustExponent"), thrust_hi=num("higherScaleThrustExponent"),
                    isp_lo=num("lowerScaleIspExponent"), isp_hi=num("higherScaleIspExponent"),
                    default_scale=float(scale.group(1)))
    raise SystemExit(f"no part config for {PART}")


def ship_in_save(ksp):
    text = open(os.path.join(ksp, "saves", "FlipAndBurn-Test", "quicksave.sfs"), encoding="utf-8", errors="replace").read()
    i = text.find(f"name = {SHIP}\n")
    start = text.rfind("VESSEL", 0, i)
    body = text[start:block_end(text, text.find("{", start))]
    fuel = sum(float(a) for a in re.findall(r"name = LqdDeuterium\s*\n\s*amount = ([0-9.eE+-]+)", body))
    p = body.find(f"name = {PART}")
    part = body[p:block_end(body, body.rfind("{", 0, p))]
    current = float(re.search(r"name = TweakScale.*?currentScale = ([0-9.]+)", part, re.S).group(1))
    return fuel, current


def delta_v(isp, thrust, a, m0, m1):
    if a * m0 > thrust:
        return float("nan")   # can't hold it
    return 2 * isp * G0 * math.sqrt(a / thrust) * (math.sqrt(m0) - math.sqrt(m1))


def main():
    ksp, log_path = sys.argv[1], sys.argv[2]
    cfg = drive_config(ksp)
    fuel_save, scale = ship_in_save(ksp)
    rho = density(ksp, "LqdDeuterium")
    factor = scale / cfg["default_scale"]
    thrust_mult = factor ** (cfg["thrust_lo"] if factor < 1 else cfg["thrust_hi"])
    isp_mult = factor ** (cfg["isp_lo"] if factor < 1 else cfg["isp_hi"])
    thrust = cfg["thrust"] * thrust_mult * 1000
    isp = cfg["isp"] * isp_mult
    print(f"{PART} config ({os.path.relpath(cfg['path'], ksp)}): Mk7 {cfg['thrust']:.0f} kN, Isp {cfg['isp']:.0f} s; "
          f"size {scale} m of {cfg['default_scale']} m -> thrust x{thrust_mult:.6f}, Isp x{isp_mult:.6f}")
    print(f"  -> full thrust {thrust / 1000:.3f} kN, engine Isp {isp:.3f} s; throttle rule Isp x throttle^0.5")
    print(f"LqdDeuterium in the save: {fuel_save:.4f} units x {rho} t/unit = {fuel_save * rho:.6f} t")

    log = open(log_path, encoding="utf-8", errors="replace").read()
    for state in ("idle", "firing at throttle 0.126"):
        m = re.search(rf"reader check \[{SHIP}, {re.escape(state)}\]: vessel.totalMass ([0-9.Ee+-]+) t; LqdDeuterium ([0-9.Ee+-]+) units", log)
        if not m:
            print(f"\n[{state}] not in the log")
            continue
        total = float(m.group(1)) * 1000
        fuel = float(m.group(2)) * rho * 1000
        if state == "idle" and abs(float(m.group(2)) - fuel_save) > 0.01:
            print(f"  note: the log's propellant ({m.group(2)} units) differs from the save's")
        dry = total - fuel
        print(f"\n[{state}] ship {total:.3f} kg, propellant {fuel:.3f} kg, dry {dry:.3f} kg")
        curve = re.search(rf"reader check \[{SHIP}, {re.escape(state)}\]: engine module: maxThrust ([0-9.Ee+-]+) kN, Isp curve at vacuum ([0-9.Ee+-]+) s, current throttle ([0-9.]+)", log)
        if curve:
            th = float(curve.group(3))
            print(f"  the engine module shows: maxThrust {float(curve.group(1)):.3f} kN, Isp curve {float(curve.group(2)):.3f} s "
                  f"(engine Isp x throttle^0.5 = {isp * math.sqrt(max(th, 0.01)) if th > 0 else isp:.3f} s at throttle {th:.3f})")
        print(f"  {'accel':>6} {'independent':>12} {'Flip and Burn (new)':>20} {'old reader':>11}  difference (new - independent)")
        for g in (0.4, 1.0, 2.0):
            ind = delta_v(isp, thrust, g * ONE_G, total, dry)
            row = re.search(rf"reader check \[{SHIP}, {re.escape(state)}\] {g:.1f} g: new ([0-9.NaN]+) m/s, old ([0-9.NaN]+) m/s", log)
            new, old = (float(row.group(1)), float(row.group(2))) if row else (float("nan"), float("nan"))
            print(f"  {g:>4.1f} g {ind:>12.1f} {new:>20.1f} {old:>11.1f}  {new - ind:+.1f} m/s ({(new - ind) / ind * 100:+.4f} %)")


if __name__ == "__main__":
    main()
