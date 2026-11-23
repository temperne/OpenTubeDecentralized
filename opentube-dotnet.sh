#!/usr/bin/env bash
# OpenTube's .NET node for Linux and macOS, from one command (the site's Devices page makes it for you):
#   curl -fsSL https://<site or node>/opentube-dotnet.sh | bash -s -- --site=… --grant=…
# Installs .NET 10 if this computer doesn't have it (into ~/.dotnet, no admin rights needed), downloads OpenTube
# (no git needed) and starts the node: a window on a desktop, the terminal UI over SSH. Running it again updates.
set -euo pipefail
REPO="${OPENTUBE_REPO:-temperne/OpenTubeDecentralized}"
REF="${OPENTUBE_REF:-ccr-500677da-r1uf50}"
APP="$HOME/.opentube/dotnet-app"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# a .NET installed for this user comes first
if [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"; fi

has_dotnet10() { command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -Eq '^(1[0-9]|[2-9][0-9])\.'; }
if ! has_dotnet10; then
  echo "OpenTube: installing .NET 10 into ~/.dotnet (once; no admin rights needed)…"
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
  has_dotnet10 || { echo "OpenTube: .NET 10 didn't install. Install it from https://dot.net and run this again."; exit 1; }
fi
# minimal Linux servers often lack ICU (Unicode tables); .NET runs without it in invariant mode
if [ "$(uname)" = "Linux" ] && ! (ldconfig -p 2>/dev/null | grep -q libicu); then export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1; fi

echo "OpenTube: downloading the latest version…"
mkdir -p "$APP"
if [ -n "${OPENTUBE_SRC:-}" ]; then cp -R "$OPENTUBE_SRC/." "$APP/"   # a local copy (for testing)
else curl -fsSL "https://codeload.github.com/$REPO/tar.gz/refs/heads/$REF" | tar -xz -C "$APP" --strip-components=1; fi
cd "$APP/dotnet"
echo "OpenTube: building and starting (the first time takes a minute)…"
# Started as "curl … | bash", this script's input is the download, not the keyboard: give the program the terminal,
# so its menus and Ctrl+C work. With no terminal at all (cron, a pipe), run it with no UI.
if [ -t 0 ]; then exec dotnet run -c Release --project OpenTube.App -- "$@"
elif (exec </dev/tty) 2>/dev/null; then exec dotnet run -c Release --project OpenTube.App -- "$@" </dev/tty
else exec dotnet run -c Release --project OpenTube.App -- --service "$@"; fi
