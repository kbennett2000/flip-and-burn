"""Independent check of the numbers the Flip and Burn planner shows for the test ship.

Shares no code with the plugin. Everything is recomputed from primary sources:
  - planets: KSP's PSystem prefab, decoded by tools/planet-data/extract_stock_planets.py (JSON)
  - the ship's orbit, parts and resources: the test save (saves/FlipAndBurn-Test/quicksave.sfs)
  - the Kerbstein drive: its part config (Mk7 thrust and Isp) and the save's TweakScale size
  - the moment each screenshot was planned, the target and the parking altitude: docs/test-evidence/test-run.log

Read-only. Standard library only. Usage:
  python3 check_planner.py <stock_planets.json> <quicksave.sfs> <test-run.log>
"""
import math
import re
import sys

G0 = 9.80665          # KSP PhysicsGlobals.GravitationalAcceleration
ONE_G = 9.81          # Flip and Burn's "1 g"
SHIP = "Bebop Refueller"

# Kerbstein drive, GameData/WarpPlugin/Parts/Engines/KerbsteinDrive/Kerbstein2.cfg (sandbox: every upgrade, Mk7)
MK7_THRUST_KN = 15000.0
MK7_ISP = 500000.0
DEFAULT_SIZE = 5.0            # TweakScale defaultScale in the save
THRUST_SCALE_EXP = 4          # lowerScaleThrustExponent
ISP_SCALE_EXP = 2             # lowerScaleIspExponent
ISP_THROTTLE_EXP = 0.5        # DaedalusEngineController.ispThrottleExponent default (not overridden in the cfg)
DENSITY = {"LqdDeuterium": 0.0001624, "MonoPropellant": 0.004}  # t/unit, CommunityResourcePack / Squad


# ---------------------------------------------------------------- ConfigNode reading

def parse_cfg(text):
    root = ("root", [], []); stack = [root]; pending = None
    for raw in text.split("\n"):
        line = raw.split("//", 1)[0].strip()
        if not line:
            continue
        if line == "{":
            node = (pending, [], []); stack[-1][2].append(node); stack.append(node); pending = None
        elif line == "}":
            stack.pop()
        elif "=" in line:
            k, v = line.split("=", 1); stack[-1][1].append((k.strip(), v.strip()))
        else:
            pending = line
    return root


def val(n, k, d=None):
    return next((v for kk, v in n[1] if kk == k), d)


def kids(n, name):
    return [c for c in n[2] if c[0] == name]


# ---------------------------------------------------------------- orbits

def kepler_state(sma, ecc, inc_deg, lan_deg, argpe_deg, m0, epoch, mu, ut):
    n = math.sqrt(mu / sma ** 3)
    m = (m0 + n * (ut - epoch)) % (2 * math.pi)
    e_anom = m if ecc < 0.8 else math.pi
    for _ in range(100):
        d = (e_anom - ecc * math.sin(e_anom) - m) / (1 - ecc * math.cos(e_anom))
        e_anom -= d
        if abs(d) < 1e-15:
            break
    ce, se = math.cos(e_anom), math.sin(e_anom)
    r = math.sqrt(1 - ecc * ecc)
    px, py = sma * (ce - ecc), sma * r * se
    k = 1 - ecc * ce
    vx, vy = -sma * n * se / k, sma * n * r * ce / k

    def rot(x, y):
        w, i, o = map(math.radians, (argpe_deg, inc_deg, lan_deg))
        x1, y1 = math.cos(w) * x - math.sin(w) * y, math.sin(w) * x + math.cos(w) * y
        y2, z2 = math.cos(i) * y1, math.sin(i) * y1
        return (math.cos(o) * x1 - math.sin(o) * y2, math.sin(o) * x1 + math.cos(o) * y2, z2)

    return rot(px, py), rot(vx, vy)


def add(a, b): return tuple(x + y for x, y in zip(a, b))
def sub(a, b): return tuple(x - y for x, y in zip(a, b))
def mul(a, s): return tuple(x * s for x in a)
def dot(a, b): return sum(x * y for x, y in zip(a, b))
def norm(a): return math.sqrt(dot(a, a))


# ---------------------------------------------------------------- the trip model (ADR 0002), written independently

def trip_for_fixed_end(r0, v0, aim, v_end, accel):
    """Smallest T whose stronger burn half is <= accel. Doubling search, then bisection."""
    def peak(t):
        c = mul(sub(v_end, v0), 1 / t)
        d = sub(sub(aim, r0), mul(add(v0, v_end), t / 2))
        ab = 4 * norm(d) / t ** 2
        e = mul(d, 1 / norm(d)) if norm(d) > 0 else (0, 0, 0)
        return math.sqrt(dot(c, c) + ab * ab + 2 * ab * abs(dot(c, e)))
    lo, hi = 1.0, 2.0
    while peak(hi) > accel:
        lo, hi = hi, hi * 2
    for _ in range(200):
        mid = (lo + hi) / 2
        if peak(mid) > accel:
            lo = mid
        else:
            hi = mid
    return hi


def plan(r0, v0, ut, planet_state, mu_p, radius_p, parking_alt, accel):
    rp = radius_p + parking_alt
    t = 0.0
    for _ in range(200):
        pos, vel = planet_state(ut + t)
        aim = add(pos, mul(sub(r0, pos), rp / norm(sub(r0, pos))))
        t_new = trip_for_fixed_end(r0, v0, aim, vel, accel)
        if abs(t_new - t) < 1e-6:
            t = t_new
            break
        t = t_new
    pos, vel = planet_state(ut + t)
    aim = add(pos, mul(sub(r0, pos), rp / norm(sub(r0, pos))))
    c = mul(sub(vel, v0), 1 / t)
    d = sub(sub(aim, r0), mul(add(v0, vel), t / 2))
    ab = 4 * norm(d) / t ** 2
    e = mul(d, 1 / norm(d))
    before, after = norm(add(c, mul(e, ab))), norm(sub(c, mul(e, ab)))
    burn_dv = t / 2 * (before + after)
    peak_speed = max(norm(mul(add(c, mul(e, ab)), t / 2)), norm(mul(c, t)))
    parking_dv = math.sqrt(mu_p / rp)
    return dict(trip_time=t, flip=t / 2, peak=peak_speed, total_dv=burn_dv + parking_dv, burn_dv=burn_dv, parking_dv=parking_dv)


# ---------------------------------------------------------------- KSP-style time (Kerbin time: 6 h days, 426-day years)

def ksp_compact(seconds):
    s = int(seconds)
    y, s = divmod(s, 426 * 6 * 3600)
    d, s = divmod(s, 6 * 3600)
    h, s = divmod(s, 3600)
    m, _ = divmod(s, 60)
    parts = [f"{y}y" if y else None, f"{d}d" if d else None, f"{h}h", f"{m}m"]
    return ", ".join(p for p in parts if p)


def speed_text(ms):
    k = ms / 1000
    return f"{ms:,.0f} m/s" if ms < 1000 else f"{k:,.2f} km/s" if k < 10 else f"{k:,.1f} km/s" if k < 100 else f"{k:,.0f} km/s"


def main():
    planets_json, sfs_path, log_path = sys.argv[1:4]
    import json
    planets = json.load(open(planets_json))
    mu = lambda b: b["Radius"] ** 2 * b["GeeASL"] * G0
    mu_sun = mu(planets["Sun"])

    def body_state(name):
        b = planets[name]; o = b["orbit"]
        return lambda ut: kepler_state(o["semiMajorAxis"], o["eccentricity"], o["inclination"], o["LAN"], o["argumentOfPeriapsis"],
                                       o["meanAnomalyAtEpoch"], o["epoch"], mu_sun, ut)

    # ---- ship from the save
    root = parse_cfg(open(sfs_path, encoding="utf-8", errors="replace").read())
    fs = kids(kids(root, "GAME")[0], "FLIGHTSTATE")[0]
    vessel = next(v for v in kids(fs, "VESSEL") if val(v, "name") == SHIP)
    orb = kids(vessel, "ORBIT")[0]
    assert val(orb, "REF") == "1", "expected the ship to orbit Kerbin"
    dry = sum(float(val(p, "mass")) for p in kids(vessel, "PART"))
    res = {}
    for p in kids(vessel, "PART"):
        for r in kids(p, "RESOURCE"):
            res[val(r, "name")] = res.get(val(r, "name"), 0) + float(val(r, "amount"))
    wet = dry + sum(res.get(k, 0) * d for k, d in DENSITY.items())
    fuel = res.get("LqdDeuterium", 0) * DENSITY["LqdDeuterium"]
    drive = next(p for p in kids(vessel, "PART") if val(p, "name") == "KspiEpsteinUDD")
    size = float(next(val(m, "currentScale") for m in kids(drive, "MODULE") if val(m, "name") == "TweakScale"))
    scale = size / DEFAULT_SIZE
    thrust_n = MK7_THRUST_KN * scale ** THRUST_SCALE_EXP * 1000
    isp = MK7_ISP * scale ** ISP_SCALE_EXP
    m0, m1 = wet * 1000, (wet - fuel) * 1000

    print(f"Ship (from the save): dry {dry:.4f} t + LqdDeuterium {fuel:.4f} t + MonoPropellant "
          f"{res.get('MonoPropellant', 0) * DENSITY['MonoPropellant']:.4f} t = {wet:.4f} t")
    print(f"Kerbstein drive at TweakScale {size} m (x{scale}): thrust {thrust_n / 1000:.3f} kN, Isp {isp:.1f} s")
    full_dv = isp * G0 * math.log(m0 / m1)
    # Delta-v holding 1 g, integrated step by step with Isp = Isp0 * throttle^0.5 (not the plugin's closed form).
    m, dv, dt = m0, 0.0, 1.0
    while m > m1:
        throttle = ONE_G * m / thrust_n
        mdot = ONE_G * m / (isp * throttle ** ISP_THROTTLE_EXP * G0)
        step = min(dt, (m - m1) / mdot)
        dv += ONE_G * step
        m -= mdot * step
    print(f"  max acceleration {thrust_n / m0 / ONE_G:.4f} g; delta-v at full thrust {full_dv / 1000:.3f} km/s "
          f"({speed_text(full_dv)}); at 1.00 g {dv / 1000:.3f} km/s ({speed_text(dv)})")

    kerbin = body_state("Kerbin")
    mu_kerbin = mu(planets["Kerbin"])
    log = open(log_path, encoding="utf-8", errors="replace").read()
    for m_ in re.finditer(r"window shows \(planned at UT ([0-9.E+]+), target (\w+), acceleration ([0-9.]+) g, flip \w+, parking ([0-9.E+]+) m\)", log):
        ut, target, g, alt = float(m_.group(1)), m_.group(2), float(m_.group(3)), float(m_.group(4))
        ship_rel = kepler_state(float(val(orb, "SMA")), float(val(orb, "ECC")), float(val(orb, "INC")), float(val(orb, "LAN")),
                                float(val(orb, "LPE")), float(val(orb, "MNA")), float(val(orb, "EPH")), mu_kerbin, ut)
        k_pos, k_vel = kerbin(ut)
        r0, v0 = add(k_pos, ship_rel[0]), add(k_vel, ship_rel[1])
        b = planets[target]
        p = plan(r0, v0, ut, body_state(target), mu(b), b["Radius"], alt, g * ONE_G)
        print(f"\n{target} at {g} g, parking {alt / 1000:.0f} km, planned at UT {ut:.3f}:")
        print(f"  trip time   {p['trip_time']:.1f} s = {ksp_compact(p['trip_time'])}")
        print(f"  flip in     {p['flip']:.1f} s = {ksp_compact(p['flip'])}")
        print(f"  peak speed  {p['peak']:.1f} m/s = {speed_text(p['peak'])}")
        print(f"  delta-v     {p['total_dv']:.1f} m/s = {speed_text(p['total_dv'])} (burn {p['burn_dv']:.1f} + parking orbit {p['parking_dv']:.1f})")
        print(f"  shortfall at 1 g: {speed_text(max(0, p['total_dv'] - dv))}")


if __name__ == "__main__":
    main()
