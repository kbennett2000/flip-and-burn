"""Independent check of the numbers the Flip and Burn planner shows for the test ship.

Shares no code with the plugin. Everything is recomputed from primary sources:
  - planets: KSP's PSystem prefab, decoded by tools/planet-data/extract_stock_planets.py (JSON)
  - the ship's orbit, parts and resources: the test save (saves/FlipAndBurn-Test/quicksave.sfs)
  - the Kerbstein drive: its part config (Mk7 thrust and Isp) and the save's TweakScale size
  - the moment each screenshot was planned, the target, the parking altitude, the trip type and whether infinite
    propellant was on: docs/test-evidence/test-run.log

Full burns use the planner's trip model (ADR 0002), written again from the ADR. Coast trips use a different method from
the plugin: a shooting solver that integrates the ship step by step under the Sun, Kerbin and the target (burn, coast,
burn) and solves for the burn directions, burn times and trip time that end at the parking orbit with the burns using
exactly the coast budget (90 % of the ship's delta-v at the chosen acceleration, less the planner's 6 % guidance margin).

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


# ---------------------------------------------------------------- coast trips: shooting with real gravity

DEBUG = False
COAST_RESERVE = 0.10          # ADR 0002 addendum
GUIDANCE_MARGIN = 0.06        # ADR 0003: the burns are planned this much under the budget
BRAKING_FRACTION = 0.99       # ADR 0003: braking at 99 % of the chosen acceleration


def unit_from_angles(th, ph):
    return (math.cos(th) * math.cos(ph), math.sin(th) * math.cos(ph), math.sin(ph))


def angles_of(v):
    n = norm(v)
    return math.atan2(v[1], v[0]), math.asin(max(-1.0, min(1.0, v[2] / n)))


def fly_coast(r0, v0, ut0, t1, a1, tc, t2, a2, bodies, mu_sun):
    """RK4 from (r0, v0): a1 for t1 s, coast tc s, a2 for t2 s. bodies: [(state_fn, mu, active_until_s, active_from_s)]."""
    def grav(t, r):
        g = mul(r, -mu_sun / norm(r) ** 3)
        for state, mu_b, until, since in bodies:
            if since <= t <= until:
                d = sub(r, state(ut0 + t)[0])
                g = add(g, mul(d, -mu_b / norm(d) ** 3))
        return g

    def segment(r, v, t, dur, acc, h):
        n = max(1, int(math.ceil(dur / h)))
        h = dur / n
        for _ in range(n):
            k1v = add(grav(t, r), acc); k1r = v
            k2v = add(grav(t + h / 2, add(r, mul(k1r, h / 2))), acc); k2r = add(v, mul(k1v, h / 2))
            k3v = add(grav(t + h / 2, add(r, mul(k2r, h / 2))), acc); k3r = add(v, mul(k2v, h / 2))
            k4v = add(grav(t + h, add(r, mul(k3r, h))), acc); k4r = add(v, mul(k3v, h))
            r = add(r, mul(add(add(k1r, mul(k2r, 2)), add(mul(k3r, 2), k4r)), h / 6))
            v = add(v, mul(add(add(k1v, mul(k2v, 2)), add(mul(k3v, 2), k4v)), h / 6))
            t += h
        return r, v, t

    r, v, t = segment(r0, v0, 0.0, t1, a1, 5.0)
    r, v, t = segment(r, v, t, tc, (0, 0, 0), 1800.0)
    r, v, t = segment(r, v, t, t2, a2, 5.0)
    return r, v


def solve_coast(r0, v0, ut, target_state, mu_t, rp, accel, budget, guess_total, bodies, mu_sun):
    """Unknowns: two burn directions (4 angles), t1, t2, total time T. Equations: end position and velocity at the
    parking point (6), and a1 t1 + a2 t2 = budget (1). Newton with a finite-difference Jacobian."""
    a1m, a2m = accel, accel * BRAKING_FRACTION

    def end_state(T, r_start):
        pos, vel = target_state(ut + T)
        out = sub(r_start, pos)
        out = (out[0], out[1], 0.0)
        out = mul(out, 1 / norm(out))
        side = (-out[1], out[0], 0.0)                     # prograde, in the ecliptic
        return add(pos, mul(out, rp)), add(vel, mul(side, math.sqrt(mu_t / rp)))

    # Starting guess: no gravity, equal burns.
    T = guess_total
    t1 = t2 = budget / (a1m + a2m)
    rF, vF = end_state(T, r0)
    dv = sub(vF, v0)
    dr = sub(sub(rF, r0), mul(v0, T))
    # A1 t1 + A2 t2 = dv ; A1 (t1^2/2 + t1 (T - t1)) + A2 t2^2/2 = dr  (per component, 2x2)
    p11, p12, p21, p22 = t1, t2, t1 * t1 / 2 + t1 * (T - t1), t2 * t2 / 2
    det = p11 * p22 - p12 * p21
    A1 = mul(sub(mul(dv, p22), mul(dr, p12)), 1 / det)
    A2 = mul(sub(mul(dr, p11), mul(dv, p21)), 1 / det)
    x = [*angles_of(A1), *angles_of(A2), t1, t2, T]

    def residual(x):
        th1, ph1, th2, ph2, t1, t2, T = x
        tc = T - t1 - t2
        r, v = fly_coast(r0, v0, ut, t1, mul(unit_from_angles(th1, ph1), a1m), tc, t2, mul(unit_from_angles(th2, ph2), a2m),
                         bodies(T), mu_sun)
        rF, vF = end_state(T, r0)
        dp, dvv = sub(r, rF), sub(v, vF)
        return [dp[0] / 1e6, dp[1] / 1e6, dp[2] / 1e6, dvv[0], dvv[1], dvv[2], (a1m * t1 + a2m * t2 - budget)]

    f = residual(x)
    for it in range(40):
        err = math.sqrt(sum(e * e for e in f))
        if DEBUG:
            print(f"    iter {it}: residual {err:.3e} x={['%.6g' % q for q in x]}")
        if err < 1e-3:
            break
        steps = [1e-5, 1e-5, 1e-5, 1e-5, 0.1, 0.1, 10.0]
        J = []
        for j in range(7):
            xp = list(x); xp[j] += steps[j]
            fp = residual(xp)
            J.append([(fp[i] - f[i]) / steps[j] for i in range(7)])
        # Solve J^T-layout system: sum_j J[j][i] dx_j = -f_i  (Gaussian elimination with pivoting)
        M = [[J[j][i] for j in range(7)] + [-f[i]] for i in range(7)]
        for c in range(7):
            piv = max(range(c, 7), key=lambda r_: abs(M[r_][c]))
            M[c], M[piv] = M[piv], M[c]
            for r_ in range(7):
                if r_ != c and M[c][c] != 0:
                    k = M[r_][c] / M[c][c]
                    M[r_] = [a - k * b for a, b in zip(M[r_], M[c])]
        dx = [M[i][7] / M[i][i] for i in range(7)]
        # Damp big steps in the times.
        scale = 1.0
        for i in (4, 5):
            if abs(dx[i]) > 0.3 * x[i]:
                scale = min(scale, 0.3 * x[i] / abs(dx[i]))
        if abs(dx[6]) > 0.2 * x[6]:
            scale = min(scale, 0.2 * x[6] / abs(dx[6]))
        # Backtracking: take the longest step (up to the damped one) that lowers the residual.
        for _ in range(12):
            xn = [a + scale * b for a, b in zip(x, dx)]
            fn = residual(xn)
            if math.sqrt(sum(e * e for e in fn)) < err:
                x, f = xn, fn
                break
            scale /= 2
        else:
            break
    err = math.sqrt(sum(e * e for e in f))
    th1, ph1, th2, ph2, t1, t2, T = x
    return dict(burn_up=t1, coast=T - t1 - t2, burn_down=t2, trip=T, burn_dv=a1m * t1 + a2m * t2, residual=err, iterations=it)


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
    pattern = (r"window shows \(planned at UT ([0-9.E+]+), target (\w+), acceleration ([0-9.]+) g, flip \w+, parking ([0-9.E+]+) m"
               r"(?:, trip (\w+), infinite propellant (\w+))?\)")
    for m_ in re.finditer(pattern, log):
        ut, target, g, alt = float(m_.group(1)), m_.group(2), float(m_.group(3)), float(m_.group(4))
        trip, infinite = m_.group(5) or "FullBurn", m_.group(6) == "True"
        ship_rel = kepler_state(float(val(orb, "SMA")), float(val(orb, "ECC")), float(val(orb, "INC")), float(val(orb, "LAN")),
                                float(val(orb, "LPE")), float(val(orb, "MNA")), float(val(orb, "EPH")), mu_kerbin, ut)
        k_pos, k_vel = kerbin(ut)
        r0, v0 = add(k_pos, ship_rel[0]), add(k_vel, ship_rel[1])
        b = planets[target]
        p = plan(r0, v0, ut, body_state(target), mu(b), b["Radius"], alt, g * ONE_G)
        print(f"\n{target} at {g} g, parking {alt / 1000:.0f} km, planned at UT {ut:.3f}, trip {trip}, infinite propellant {infinite}:")
        print(f"  full burn: trip time   {p['trip_time']:.1f} s = {ksp_compact(p['trip_time'])}")
        print(f"  full burn: flip in     {p['flip']:.1f} s = {ksp_compact(p['flip'])}")
        print(f"  full burn: peak speed  {p['peak']:.1f} m/s = {speed_text(p['peak'])}")
        print(f"  full burn: delta-v     {p['total_dv']:.1f} m/s = {speed_text(p['total_dv'])} (burn {p['burn_dv']:.1f} + parking orbit {p['parking_dv']:.1f})")
        if infinite:
            print("  infinite propellant on: no shortfall should be shown")
        else:
            print(f"  full burn: shortfall at {g} g: {p['total_dv'] - dv:.1f} m/s = {speed_text(max(0, p['total_dv'] - dv))}")
        if infinite or p['total_dv'] <= dv and trip == "FullBurn":
            continue

        # The coast trip: shown for "Coast in the middle", suggested (trip time) when a full burn is short.
        budget = (1 - COAST_RESERVE) * dv
        burns = budget / (1 + GUIDANCE_MARGIN)
        raw = re.search(r"coast raw: Feasible=True BurnUp=([0-9.E+]+) s Coast=([0-9.E+]+) s BurnDown=([0-9.E+]+) s TripTime=([0-9.E+]+)",
                        log[m_.end():])
        guess = float(raw.group(4)) if raw else 600000.0
        mu_t = mu(b)
        rp = b["Radius"] + alt
        soi_kerbin = planets["Kerbin"].get("sphereOfInfluence", 84159286.0)
        bodies = lambda T: [(kerbin, mu_kerbin, 20000.0, 0.0), (body_state(target), mu_t, 1e12, 0.0)]
        c = solve_coast(r0, v0, ut, body_state(target), mu_t, rp, g * ONE_G, burns, guess, bodies, mu_sun)
        print(f"  coast: budget 90 % of {dv:.1f} = {budget:.1f} m/s; burns planned within {burns:.1f} m/s (6 % margin); "
              f"needed incl. margin {c['burn_dv'] * (1 + GUIDANCE_MARGIN):.1f} m/s = {speed_text(c['burn_dv'] * (1 + GUIDANCE_MARGIN))}")
        print(f"  coast: burn to speed up {c['burn_up']:.1f} s = {ksp_compact(c['burn_up'])}")
        print(f"  coast: coast            {c['coast']:.1f} s = {ksp_compact(c['coast'])}")
        print(f"  coast: burn to stop     {c['burn_down']:.1f} s = {ksp_compact(c['burn_down'])}")
        print(f"  coast: trip time        {c['trip']:.1f} s = {ksp_compact(c['trip'])}")
        print(f"  coast: kept in reserve  {COAST_RESERVE * dv:.1f} m/s = {speed_text(COAST_RESERVE * dv)}")
        print(f"  coast: solver residual {c['residual']:.2e} after {c['iterations']} iterations")


if __name__ == "__main__":
    main()
