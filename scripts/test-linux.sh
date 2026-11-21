#!/usr/bin/env bash
# Compila, prueba y publica con Native AOT dentro de un contenedor Linux, sin tocar el árbol de trabajo.
# Sirve para verificar Linux desde macOS o Windows sin esperar a GitHub Actions. Requiere Docker.
set -euo pipefail

cd "$(dirname "$0")/.."

docker run --rm -v "$PWD":/src:ro mcr.microsoft.com/dotnet/sdk:11.0 bash -c '
  set -e
  mkdir /work
  cd /src
  tar --exclude=./artifacts --exclude=.git --exclude=bin --exclude=obj -cf - . | tar -xf - -C /work
  cd /work
  uname -srm
  dotnet build -c Release
  dotnet test -c Release --no-build
  apt-get update -qq && apt-get install -y -qq clang zlib1g-dev > /dev/null
  dotnet publish tests/TekiumDB.AotSmoke -c Release -o /tmp/aot
  /tmp/aot/TekiumDB.AotSmoke
'
