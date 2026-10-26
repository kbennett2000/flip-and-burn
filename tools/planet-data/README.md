# Planet data tools

Scripts that read KSP's own planet data from the install, so the numbers in
`tests/FlipAndBurn.Core.Tests/KerbolSystem.cs` can be checked or regenerated. Both are read-only and use only the
Python standard library.

```sh
KSP="/path/to/Kerbal Space Program"
python3 extract_stock_planets.py "$KSP/KSP_Data" > /tmp/stock_planets.json
python3 check_against_kopernicus.py /tmp/stock_planets.json "$KSP/Logs/Kopernicus/Kopernicus.log"
```

- `unityfile.py` reads a Unity 2019 `.assets` file: header, object table and external file list.
- `extract_stock_planets.py` finds every `CelestialBody` and `OrbitDriver` in the PSystem prefab
  (`KSP_Data/sharedassets9.assets`) and prints radius, GeeASL and orbital elements as JSON. Field layouts come from
  KSP's `Assembly-CSharp.dll` and are listed at the top of the script.
- `check_against_kopernicus.py` recomputes each Sun-orbiting planet's sphere of influence the way KSP does and compares
  it with the runtime value Kopernicus writes to `Logs/Kopernicus/Kopernicus.log`. The check only works when
  Kopernicus is installed (it is here, for Outer Planets Mod). On 2026-10-26 every planet matched to within 1e-15.
