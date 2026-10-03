#!/bin/bash
# builds the one-file Mutual.exe (no .NET install needed on the other pc) into publish/
cd "$(dirname "$0")/.."
DOTNET="$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"
"$DOTNET" publish src/Mutual -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o publish -nologo -v q && ls -la publish/Mutual.exe
