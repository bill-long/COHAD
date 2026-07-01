#!/bin/bash
# Installs the .NET SDK (pinned to global.json) so `dotnet build`/`dotnet test`
# work in Claude Code on the web sessions. Only runs in the remote environment;
# local machines are assumed to already have the SDK.
set -euo pipefail

# Local (non-web) sessions: do nothing.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

REPO_DIR="${CLAUDE_PROJECT_DIR:-$(pwd)}"
DOTNET_DIR="$HOME/.dotnet"

# Keep the SDK version in sync with global.json (fallback to a known-good pin).
SDK_VERSION="$(grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]+"' "$REPO_DIR/global.json" 2>/dev/null | head -n1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' || true)"
SDK_VERSION="${SDK_VERSION:-10.0.200}"

# Make this session's shell (and later hook runs) see the SDK.
export PATH="$DOTNET_DIR:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Idempotent: skip the download if the pinned SDK is already present.
if ! "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q "^${SDK_VERSION} "; then
  echo "Installing .NET SDK ${SDK_VERSION} into ${DOTNET_DIR}..."
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --version "$SDK_VERSION" --install-dir "$DOTNET_DIR"
else
  echo ".NET SDK ${SDK_VERSION} already installed."
fi

# Persist the SDK on PATH (and telemetry opt-out) for the rest of the session.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export PATH=\"$DOTNET_DIR:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

# Warm the NuGet cache so the first build/test is fast (best-effort).
"$DOTNET_DIR/dotnet" restore "$REPO_DIR/Web/Web.csproj" || true
"$DOTNET_DIR/dotnet" restore "$REPO_DIR/Web.UnitTests/Web.UnitTests.csproj" || true

echo ".NET SDK ready: $("$DOTNET_DIR/dotnet" --version)"
