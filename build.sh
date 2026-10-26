#!/usr/bin/env bash
# Usage:
#   ./build.sh          Build FlipAndBurn.dll and install it to <KSPDir>/GameData/FlipAndBurn/Plugins/.
#                       Also removes the test harness if an earlier test run left it behind.
#   ./build.sh test     Build, install, then run the in-flight test: install the dev-only test harness, launch KSP
#                       with -flipandburn-test, wait for the harness to finish, and remove the harness again.
#                       Writes the screenshots, the harness log and the cheat check to docs/test-evidence/.
#
# KSPDir comes from local.props (copy local.props.example to start).
set -euo pipefail

cd "$(dirname "$0")"
repo="$(pwd)"
mode="${1:-install}"
config=Release

plugin_project="src/FlipAndBurn/FlipAndBurn.csproj"
harness_project="src/FlipAndBurn.TestHarness/FlipAndBurn.TestHarness.csproj"

if [[ ! -f local.props ]]; then
    echo "local.props is missing. Copy local.props.example to local.props and set KSPDir." >&2
    exit 1
fi

ksp_dir="$(dotnet msbuild "$plugin_project" -nologo -getProperty:KSPDir)"
plugin_dest="$ksp_dir/GameData/FlipAndBurn/Plugins"
harness_dir="$ksp_dir/GameData/FlipAndBurnTestHarness"

remove_harness() {
    if [[ -e "$harness_dir" ]]; then
        rm -rf "$harness_dir"
        echo "Removed test harness from $harness_dir"
    fi
}

install_plugin() {
    dotnet build "$plugin_project" -c "$config" -nologo
    mkdir -p "$plugin_dest"
    cp "src/FlipAndBurn/bin/$config/FlipAndBurn.dll" "src/FlipAndBurn/bin/$config/FlipAndBurn.Core.dll" "$plugin_dest/"
    echo "Installed FlipAndBurn.dll and FlipAndBurn.Core.dll -> $plugin_dest/"
}

case "$mode" in
    install)
        install_plugin
        remove_harness
        exit 0
        ;;
    test)
        ;;
    *)
        echo "Unknown mode '$mode'. Use: ./build.sh  or  ./build.sh test" >&2
        exit 2
        ;;
esac

# ---------------------------------------------------------------- test run

ksp_bin="$ksp_dir/KSP.x86_64"
ksp_log="$ksp_dir/KSP.log"
test_save="$ksp_dir/saves/FlipAndBurn-Test"
mj_settings="$ksp_dir/GameData/MechJeb2/Plugins/PluginData/MechJeb2"
shot_dir="$ksp_dir/FlipAndBurnTest"
expected_shots=(a-1-before-flip.png a-2-during-flip.png a-3-after-flip.png a-4-after-abort.png b-1-coast.png b-2-after-abort.png)
evidence="$repo/docs/test-evidence"
stall_seconds=120
max_launches=4          # first launch plus up to 3 relaunches after a stall
overall_timeout=14400   # seconds (the autopilot proof warps to the flip of a 3-day trip)

if pgrep -x KSP.x86_64 >/dev/null; then
    echo "KSP is already running. Close it first." >&2
    exit 1
fi
if [[ ! -f "$test_save/quicksave.sfs" ]]; then
    echo "Test save missing: $test_save/quicksave.sfs" >&2
    exit 1
fi

install_plugin
dotnet build "$harness_project" -c "$config" -nologo

# Start every run from the same game state: the test save's quicksave.
cp -p "$test_save/quicksave.sfs" "$test_save/persistent.sfs"
cp -p "$test_save/quicksave.loadmeta" "$test_save/persistent.loadmeta"

# MechJeb rewrites its settings files when a flight runs. Snapshot them so the test leaves Kris's settings as they were.
# KSP also rewrites settings.cfg, and the harness may change graphics settings while it runs.
backup_dir="$(mktemp -d)"
cp -a "$mj_settings" "$backup_dir/MechJeb2"
cp -p "$ksp_dir/settings.cfg" "$backup_dir/settings.cfg"
# BetterTimeWarp rewrites its own settings file whenever KSP runs.
btw_settings="$ksp_dir/GameData/BetterTimeWarp/PluginData/BetterTimeWarp.cfg"
[[ -f "$btw_settings" ]] && cp -p "$btw_settings" "$backup_dir/BetterTimeWarp.cfg"

# The harness turns KSP's infinite propellant cheat on for one screenshot. Fingerprint Kris's settings and every save
# except the test copy now, to show afterwards that the cheat left no trace.
fingerprint_saves() {
    (cd "$ksp_dir/saves" && find . -path ./FlipAndBurn-Test -prune -o -type f -print0 | sort -z | xargs -0 sha256sum)
}
fingerprint_saves > "$backup_dir/saves-before.sha256"

cleanup() {
    if pgrep -x KSP.x86_64 >/dev/null; then
        pkill -x KSP.x86_64 2>/dev/null || true
        # A frozen KSP ignores the polite signal.
        for _ in $(seq 1 15); do pgrep -x KSP.x86_64 >/dev/null || break; sleep 1; done
        pkill -9 -x KSP.x86_64 2>/dev/null || true
    fi
    remove_harness
    if [[ -d "$backup_dir/MechJeb2" ]]; then
        rm -rf "$mj_settings"
        cp -a "$backup_dir/MechJeb2" "$mj_settings"
        echo "Restored MechJeb settings in $mj_settings"
    fi
    if [[ -f "$backup_dir/BetterTimeWarp.cfg" ]]; then
        cp -p "$backup_dir/BetterTimeWarp.cfg" "$btw_settings"
        echo "Restored $btw_settings"
    fi
    if [[ -f "$backup_dir/settings.cfg" ]]; then
        cp -p "$backup_dir/settings.cfg" "$ksp_dir/settings.cfg"
        echo "Restored $ksp_dir/settings.cfg"
    fi
    rm -rf "$backup_dir"
}
trap cleanup EXIT

mkdir -p "$harness_dir/Plugins"
cp "src/FlipAndBurn.TestHarness/bin/$config/FlipAndBurn.TestHarness.dll" "$harness_dir/Plugins/"
echo "Installed test harness -> $harness_dir/Plugins/"
mkdir -p "$shot_dir"
rm -f "$shot_dir"/*.png

# Fallback screenshot from outside the game, used when the harness says its in-game capture came out blank.
# KSP runs as an X11 window (XWayland on GNOME), which ffmpeg's x11grab can read. $1 is the file to write.
external_capture() {
    local shot="$1"
    if ! command -v ffmpeg >/dev/null || ! command -v xwininfo >/dev/null; then
        echo "External capture needs ffmpeg and xwininfo; skipping." >&2
        return
    fi
    local win
    win="$(xwininfo -root -tree 2>/dev/null | grep '("KSP.x86_64"' | head -1 | awk '{print $1}')"
    if [[ -z "$win" ]]; then
        echo "External capture: KSP window not found." >&2
        return
    fi
    mkdir -p "$(dirname "$shot")"
    if ffmpeg -hide_banner -loglevel error -y -f x11grab -window_id "$win" -i "${DISPLAY:-:0}" -frames:v 1 "$shot.tmp.png"; then
        mv "$shot.tmp.png" "$shot"
        echo "$(date +%T) External capture of window $win saved to $shot"
    else
        echo "External capture failed." >&2
    fi
}
declare -A external_done=()

launch() {
    (cd "$ksp_dir" && nohup "$ksp_bin" -flipandburn-test >/dev/null 2>&1 &)
    echo "$(date +%T) Launched KSP with -flipandburn-test (launch $1 of at most $max_launches)"
}

start=$(date +%s)
launches=1
launch $launches
sleep 5
last_size=-1
still=0
last_report=$start

while true; do
    now=$(date +%s)
    if (( now - start > overall_timeout )); then
        echo "Gave up after ${overall_timeout}s." >&2
        break
    fi

    if grep -a -q -E '\[FlipAndBurn.Test\] step [0-9]+: done' "$ksp_log" 2>/dev/null; then
        echo "$(date +%T) Harness finished; waiting for KSP to exit"
        for _ in $(seq 1 120); do pgrep -x KSP.x86_64 >/dev/null || break; sleep 1; done
        if pgrep -x KSP.x86_64 >/dev/null; then
            echo "KSP did not exit on its own; stopping it"
            pkill -x KSP.x86_64 || true
            sleep 10
            pkill -9 -x KSP.x86_64 2>/dev/null || true
        fi
        break
    fi

    while IFS= read -r request; do
        [[ -n "$request" && -z "${external_done[$request]:-}" ]] || continue
        external_capture "$request"
        external_done[$request]=1
    done < <(grep -a -o 'external capture requested: .*\.png' "$ksp_log" 2>/dev/null | sed 's/^external capture requested: //')

    if ! pgrep -x KSP.x86_64 >/dev/null; then
        echo "$(date +%T) KSP exited before the harness finished." >&2
        break
    fi

    size=$(stat -c %s "$ksp_log" 2>/dev/null || echo 0)
    if [[ "$size" == "$last_size" ]]; then still=$((still + 5)); else still=0; last_size=$size; fi
    if (( still >= stall_seconds )) && grep -a -q '\[FlipAndBurn.Test\] step 1' "$ksp_log"; then
        # Past the main menu this is not the known startup lock-up; relaunching would just repeat it.
        echo "$(date +%T) KSP.log has not grown for ${stall_seconds}s after the harness started. Stopping KSP." >&2
        echo "Last log line: $(tail -1 "$ksp_log" | cut -c1-200)" >&2
        break
    fi
    if (( still >= stall_seconds )); then
        if (( launches >= max_launches )); then
            echo "KSP stalled $launches times; giving up." >&2
            break
        fi
        echo "$(date +%T) KSP.log has not grown for ${stall_seconds}s (startup lock-up). Killing and relaunching."
        pkill -x KSP.x86_64 || true
        for _ in $(seq 1 20); do pgrep -x KSP.x86_64 >/dev/null || break; sleep 1; done
        pkill -9 -x KSP.x86_64 2>/dev/null || true
        sleep 2
        launches=$((launches + 1))
        launch $launches
        sleep 5
        still=0
        last_size=-1
    fi

    if (( now - last_report >= 120 )); then
        echo "$(date +%T) still running: $(wc -l < "$ksp_log") log lines; last: $(tail -1 "$ksp_log" | cut -c1-120)"
        last_report=$now
    fi
    sleep 5
done

# ---------------------------------------------------------------- results

mkdir -p "$evidence"

# Did the cheat stick? Checked before cleanup restores settings.cfg, so this is what KSP left behind.
cheat_log="$evidence/cheat-check.log"
{
    echo "# Cheat check (infinite propellant, ignore max temperature) $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "## The harness's own readings"
    grep -a -E '\[FlipAndBurn.Test\].*(infinite propellant|InfinitePropellant|max temperature)' "$ksp_log" | sed 's/^.*\[FlipAndBurn.Test\] //' || true
    echo "## settings.cfg: lines mentioning a cheat or propellant, before and after the run"
    echo "before: $(grep -c -i -E 'cheat|propellant' "$backup_dir/settings.cfg" || true)"
    echo "after:  $(grep -c -i -E 'cheat|propellant' "$ksp_dir/settings.cfg" || true)"
    echo "## settings.cfg: every line KSP changed during the run (diff before -> after)"
    diff "$backup_dir/settings.cfg" "$ksp_dir/settings.cfg" && echo "(no changes)"
    echo "## Every save except FlipAndBurn-Test: files changed during the run"
    fingerprint_saves > "$backup_dir/saves-after.sha256"
    echo "files fingerprinted: $(wc -l < "$backup_dir/saves-after.sha256")"
    diff "$backup_dir/saves-before.sha256" "$backup_dir/saves-after.sha256" && echo "(no changes)"
    echo "## Every save except FlipAndBurn-Test: lines mentioning InfinitePropellant"
    echo "$(grep -r -i -l 'InfinitePropellant' --exclude-dir=FlipAndBurn-Test "$ksp_dir/saves" | wc -l) files"
} > "$cheat_log" 2>&1
cat "$cheat_log"
grep -q -E '^after:  0$' "$cheat_log" || { echo "settings.cfg mentions a cheat after the run" >&2; }

summary="$evidence/test-run.log"
{
    echo "# FlipAndBurn test run $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    grep -a -E "KSPAssembly '(FlipAndBurn|FlipAndBurn.TestHarness|MechJeb2)'|^MechJeb2 v|From LOADING to MAINMENU|\[FlipAndBurn\]|\[FlipAndBurn.Test\]" "$ksp_log" | grep -a -v 'NullReferenceException' || true
    # (The harness's own [FlipAndBurn.Test] lines are left out: they may report another mod's failure by name.)
    echo "# Exception lines mentioning FlipAndBurn or MechJeb: $(grep -a -i exception "$ksp_log" | grep -a -v '\[FlipAndBurn.Test\]' | grep -c -i -E 'flipandburn|mechjeb|mumech' || true)"
    echo "# Stack frames in FlipAndBurn or MechJeb code: $(grep -a -c -E '(at |\()(MuMech|FlipAndBurn|MechJebLib)[.:]' "$ksp_log" || true)"
} > "$summary"
cat "$summary"

ok=1
grep -a -q '\[FlipAndBurn.Test\] FAILED' "$ksp_log" && ok=0
if grep -q -E '^# (Exception lines|Stack frames).*: [1-9]' "$summary"; then ok=0; fi

# Keep full resolution so small UI text stays readable, but as JPEG (about 2.5 MB instead of 10 MB at 4K) so the repo
# does not grow by 10 MB every time the evidence is refreshed.
for name in "${expected_shots[@]}"; do
    src="$shot_dir/$name"
    if [[ ! -f "$src" ]]; then
        echo "Missing screenshot: $src" >&2
        ok=0
        continue
    fi
    base="${name%.png}"
    rm -f "$evidence/$base.png" "$evidence/$base.jpg"
    if command -v ffmpeg >/dev/null; then
        ffmpeg -hide_banner -loglevel error -y -i "$src" -q:v 2 "$evidence/$base.jpg"
        echo "Screenshot saved to $evidence/$base.jpg"
    else
        cp "$src" "$evidence/$name"
        echo "Screenshot copied to $evidence/$name"
    fi
done

if (( ok )); then
    echo "TEST PASSED"
else
    echo "TEST FAILED" >&2
    exit 1
fi
