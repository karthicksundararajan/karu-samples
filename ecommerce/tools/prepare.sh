#!/usr/bin/env bash
set -euo pipefail
app_root=$(cd "$(dirname "$0")/.." && pwd)
karu_source=${1:-"$app_root/../../karu"}
export PATH="$HOME/.dotnet:$PATH"
for project in Karu.Metamodel Karu.Extensions.Sdk Karu.Extensions Karu.Model Karu.Extensions.Testing Karu.Extensions.Generator; do
  dotnet pack "$karu_source/src/$project" -c Debug -o "$app_root/packages" -p:UseSharedCompilation=false
  # Local development packages keep the source runtime's version; refresh only this application's package cache.
  package_name=${project,,}
  python3 - "$app_root/.packages/$package_name/0.0.0-dev" <<'PYTHON'
import shutil
import sys
shutil.rmtree(sys.argv[1], ignore_errors=True)
PYTHON
done
dotnet build "$karu_source/src/Karu.Cli" -p:UseSharedCompilation=false
dotnet build "$karu_source/src/Karu.Runtime" -p:UseSharedCompilation=false
dotnet build "$app_root/Ecommerce.slnx" -p:UseSharedCompilation=false
