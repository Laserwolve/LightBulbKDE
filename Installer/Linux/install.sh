#!/usr/bin/env bash
# Builds LightBulbKDE from this checkout and installs it for the current user:
#   ~/.local/share/LightBulbKDE/                      the app, self-contained, so it needs no .NET runtime
#   ~/.local/share/applications/LightBulbKDE.desktop  its entry in the application launcher
# Settings live in ~/.config/LightBulbKDE/Settings.json, and the app writes its own autostart
# entry, ~/.config/autostart/LightBulbKDE.desktop, while "Start at login" is on.
# Building needs the .NET 10 SDK (on Ubuntu, `sudo apt install dotnet-sdk-10.0`).
# Running it again updates the installed copy, restarting it if it was running.
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
dest=$data/LightBulbKDE

case $(uname -m) in
    x86_64) rid=linux-x64 ;;
    aarch64 | arm64) rid=linux-arm64 ;;
    *)
        echo "Unsupported architecture: $(uname -m)" >&2
        exit 1
        ;;
esac

# The newest tag names the version; without one the app calls itself a development build
version=$(git -C "$repo" describe --tags --abbrev=0 2>/dev/null || echo 999.9.9-dev)

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

dotnet publish "$repo/LightBulb" \
    --configuration Release \
    --runtime "$rid" \
    --self-contained \
    -p:Version="$version" \
    -p:CSharpier_Bypass=true \
    --output "$staging/LightBulbKDE"

# Marks an installed copy, which keeps its settings in ~/.config rather than beside the binary
touch "$staging/LightBulbKDE/.installed"
cp "$repo/favicon.png" "$staging/LightBulbKDE/LightBulbKDE.png"

# A running copy is stopped first. SIGTERM makes it quit normally, which leaves the screen
# untinted and undimmed on the way out.
was_running=false
if pgrep -x LightBulbKDE >/dev/null; then
    was_running=true
    pkill -TERM -x LightBulbKDE
    for _ in $(seq 50); do
        pgrep -x LightBulbKDE >/dev/null || break
        sleep 0.2
    done
    pkill -KILL -x LightBulbKDE || true
fi

rm -rf "$dest"
mkdir -p "$data/applications"
mv "$staging/LightBulbKDE" "$dest"

cat >"$data/applications/LightBulbKDE.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=LightBulbKDE
GenericName=Night Light
Comment=Warms and dims the screen from sunset to sunrise
Exec="$dest/LightBulbKDE"
Icon=$dest/LightBulbKDE.png
Terminal=false
Categories=Utility;
StartupWMClass=LightBulbKDE
EOF

if $was_running; then
    setsid -f "$dest/LightBulbKDE" --start-hidden >/dev/null 2>&1
fi

echo "Installed LightBulbKDE $version to $dest"
