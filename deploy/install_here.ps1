# installs publish\Mutual.exe into Program Files (one admin prompt) and opens it. same thing the exe
# offers on its own when you run it, this js skips the question
$ErrorActionPreference = 'Stop'
$src = (Resolve-Path (Join-Path $PSScriptRoot '..\publish\Mutual.exe')).Path
$p = Start-Process $src -ArgumentList "--install-copy `"$src`"" -Verb RunAs -Wait -PassThru
if ($p.ExitCode -ne 0) { throw 'install failed' }
$exe = Join-Path $env:ProgramFiles 'Mutual\Mutual.exe'
Start-Process $exe
Write-Host "Mutual installed at $exe"
