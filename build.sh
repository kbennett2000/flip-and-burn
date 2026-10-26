#!/usr/bin/env bash
# Builds FlipAndBurn.dll and installs it to <KSPDir>/GameData/FlipAndBurn/Plugins/.
# KSPDir comes from local.props (copy local.props.example to start).
set -euo pipefail

cd "$(dirname "$0")"
config="${1:-Release}"
project="src/FlipAndBurn/FlipAndBurn.csproj"

if [[ ! -f local.props ]]; then
    echo "local.props is missing. Copy local.props.example to local.props and set KSPDir." >&2
    exit 1
fi

dotnet build "$project" -c "$config" -nologo

ksp_dir="$(dotnet msbuild "$project" -nologo -getProperty:KSPDir)"
dll="src/FlipAndBurn/bin/$config/FlipAndBurn.dll"
dest="$ksp_dir/GameData/FlipAndBurn/Plugins"

mkdir -p "$dest"
cp "$dll" "$dest/"
echo "Installed $dll -> $dest/"
