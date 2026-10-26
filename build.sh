#!/usr/bin/env bash
# Usage:
#   ./build.sh          Build FlipAndBurn.dll and install it to <KSPDir>/GameData/FlipAndBurn/Plugins/.
#                       Also removes the test harness if an earlier test run left it behind.
#   ./build.sh test     Build, install, then run the in-flight test: install the dev-only test harness, launch KSP
#                       with -flipandburn-test, wait for the harness to finish, and remove the harness again.
#                       Writes the screenshot and harness log to docs/test-evidence/.
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
    cp "src/FlipAndBurn/bin/$config/FlipAndBurn.dll" "$plugin_dest/"
    echo "Installed FlipAndBurn.dll -> $plugin_dest/"
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
shot="$ksp_dir/FlipAndBurnTest/flip-and-burn-window.png"
evidence="$repo/docs/test-evidence"
stall_seconds=120
max_launches=4          # first launch plus up to 3 relaunches after a stall
overall_timeout=2400    # seconds

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
rm -f "$shot"

# Fallback screenshot from outside the game, used when the harness says its in-game capture came out blank.
# KSP runs as an X11 window (XWayland on GNOME), which ffmpeg's x11grab can read.
external_capture() {
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
external_done=0

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

    if grep -a -q '\[FlipAndBurn.Test\] step 8: done' "$ksp_log" 2>/dev/null; then
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

    if (( external_done == 0 )) && grep -a -q '\[FlipAndBurn.Test\] step 7: in-game capture blank; external capture requested' "$ksp_log"; then
        external_capture
        external_done=1
    fi

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
summary="$evidence/flip-and-burn-window.log"
{
    echo "# FlipAndBurn test run $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    grep -a -E "KSPAssembly '(FlipAndBurn|FlipAndBurn.TestHarness|MechJeb2)'|^MechJeb2 v|From LOADING to MAINMENU|\[FlipAndBurn\]|\[FlipAndBurn.Test\]" "$ksp_log" || true
    echo "# Exception lines mentioning FlipAndBurn or MechJeb: $(grep -a -i exception "$ksp_log" | grep -c -i -E 'flipandburn|mechjeb|mumech' || true)"
    echo "# Stack frames in FlipAndBurn or MechJeb code: $(grep -a -c -E '(at |\()(MuMech|FlipAndBurn|MechJebLib)[.:]' "$ksp_log" || true)"
} > "$summary"
cat "$summary"

ok=1
grep -a -q '\[FlipAndBurn.Test\] step 7: screenshot saved' "$ksp_log" || ok=0
grep -a -q '\[FlipAndBurn.Test\] FAILED' "$ksp_log" && ok=0
if grep -q -E '^# (Exception lines|Stack frames).*: [1-9]' "$summary"; then ok=0; fi

# Keep full resolution so small UI text stays readable, but as a JPEG (about 2.5 MB instead of 10 MB at 4K) so the
# repo does not grow by 10 MB every time the evidence is refreshed.
if [[ -f "$shot" ]]; then
    rm -f "$evidence/flip-and-burn-window.png" "$evidence/flip-and-burn-window.jpg"
    if command -v ffmpeg >/dev/null; then
        ffmpeg -hide_banner -loglevel error -y -i "$shot" -q:v 2 "$evidence/flip-and-burn-window.jpg"
        echo "Screenshot saved to $evidence/flip-and-burn-window.jpg"
    else
        cp "$shot" "$evidence/flip-and-burn-window.png"
        echo "Screenshot copied to $evidence/flip-and-burn-window.png"
    fi
fi

if (( ok )); then
    echo "TEST PASSED"
else
    echo "TEST FAILED" >&2
    exit 1
fi
