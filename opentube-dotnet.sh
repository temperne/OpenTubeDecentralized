#!/usr/bin/env bash
# OpenTube's .NET node for Linux and macOS, from one command (the site's Devices page makes it for you):
#   curl -fsSL https://<site or node>/opentube-dotnet.sh | bash -s -- --site=… --grant=…
# Installs .NET 10 if this computer doesn't have it (into ~/.dotnet, no admin rights needed), downloads OpenTube
# (no git needed) and starts the node: a window on a desktop, the terminal UI over SSH. Running it again updates.
set -euo pipefail
# Run as a regular user after su/sudo, the environment can still be root's (HOME=/root, XDG_RUNTIME_DIR=/run/user/0):
# .NET then can't write its files or its locks ("NuGet-Migrations … /run/user/0/.dotnet … EACCES"). Use this user's own.
ME="$(id -un 2>/dev/null || echo "${USER:-}")"
if [ -z "${HOME:-}" ] || [ ! -w "$HOME" ]; then
  H="$(getent passwd "$ME" 2>/dev/null | cut -d: -f6 || true)"; [ -n "$H" ] || H="$(eval echo "~$ME" 2>/dev/null || true)"
  if [ -n "$H" ] && [ -w "$H" ]; then export HOME="$H"; else export HOME="${TMPDIR:-/tmp}/opentube-$(id -u)"; mkdir -p "$HOME"; fi
fi
if [ -n "${XDG_RUNTIME_DIR:-}" ] && [ ! -w "$XDG_RUNTIME_DIR" ]; then
  if [ -d "/run/user/$(id -u)" ] && [ -w "/run/user/$(id -u)" ]; then export XDG_RUNTIME_DIR="/run/user/$(id -u)"
  else export XDG_RUNTIME_DIR="$HOME/.opentube/run"; mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"; fi
fi
export DOTNET_CLI_HOME="$HOME"
if [ -n "${NUGET_PACKAGES:-}" ] && ! mkdir -p "$NUGET_PACKAGES" 2>/dev/null; then export NUGET_PACKAGES="$HOME/.nuget/packages"; fi
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
COMMIT=""
if [ -n "${OPENTUBE_SRC:-}" ]; then cp -R "$OPENTUBE_SRC/." "$APP/"   # a local copy (for testing)
else
  COMMIT=$(curl -fsSL -H 'Accept: application/vnd.github.sha' "https://api.github.com/repos/$REPO/commits/$REF" 2>/dev/null || true)
  case "$COMMIT" in [0-9a-f]*) ;; *) COMMIT="";; esac
  curl -fsSL "https://codeload.github.com/$REPO/tar.gz/${COMMIT:-refs/heads/$REF}" | tar -xz -C "$APP" --strip-components=1
fi
printf '%s' "$COMMIT" > "$APP/.commit"
# the node updates itself: it fetches the new source here and exits with 75; this loop starts it again
export OPENTUBE_APP_DIR="$APP" OPENTUBE_REPO="$REPO" OPENTUBE_REF="$REF"
cd "$APP/dotnet"
echo "OpenTube: building and starting (the first time takes a minute)…"
# Started as "curl … | bash", this script's input is the download, not the keyboard: give the program the terminal,
# so its menus and Ctrl+C work. With no terminal at all (cron, a pipe), run it with no UI.
run() {
  if [ -t 0 ]; then dotnet run -c Release --project OpenTube.App -- "$@"
  elif (exec </dev/tty) 2>/dev/null; then dotnet run -c Release --project OpenTube.App -- "$@" </dev/tty
  else dotnet run -c Release --project OpenTube.App -- --service "$@"; fi
}
while true; do
  code=0; run "$@" || code=$?
  [ "$code" -eq 75 ] || exit "$code"
  echo "OpenTube: starting the new version…"
done
