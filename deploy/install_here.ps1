# copies publish\Mutual.exe to %LOCALAPPDATA%\Programs\Mutual (a fixed place, so the firewall rules
# that name the program keep matching) and opens it
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot '..\publish\Mutual.exe'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Mutual'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-Process Mutual -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force
Start-Sleep -Milliseconds 500
Copy-Item $src (Join-Path $dest 'Mutual.exe') -Force
Start-Process (Join-Path $dest 'Mutual.exe')
Write-Host "Mutual installed at $dest"
