#!/usr/bin/env bash
# Installs a local .NET 8 SDK (~/.dotnet) and a tModLoader build (./.tml) to compile against.
# The tModLoader copy is only used for references + packaging; play with tModLoader from Steam.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TML_VERSION="${TML_VERSION:-latest}"

if ! command -v dotnet >/dev/null && [ ! -x "$HOME/.dotnet/dotnet" ]; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet"
fi

if [ ! -f "$ROOT/.tml/tModLoader.dll" ]; then
  if [ "$TML_VERSION" = latest ]; then
    URL=$(curl -sSL https://api.github.com/repos/tModLoader/tModLoader/releases/latest | grep -o 'https://[^"]*/tModLoader.zip' | head -1)
  else
    URL="https://github.com/tModLoader/tModLoader/releases/download/$TML_VERSION/tModLoader.zip"
  fi
  mkdir -p "$ROOT/.tml"
  curl -sSL "$URL" -o /tmp/tModLoader.zip
  python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" /tmp/tModLoader.zip "$ROOT/.tml"
fi
echo "Toolchain ready. Build with: (cd TerraCommand && ~/.dotnet/dotnet build -c Release)"
