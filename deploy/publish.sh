#!/bin/bash
# builds the one file Mutual.exe (no .net install needed on the other pc) into publish/
# the release shape (win-x64, self contained, single file) lives in src/Mutual/Mutual.csproj
cd "$(dirname "$0")/.."
DOTNET="$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"
"$DOTNET" publish src/Mutual -c Release -p:DebugType=none -o publish -nologo -v q && ls -la publish/Mutual.exe
