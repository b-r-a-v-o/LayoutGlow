$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
& $csc -nologo -target:winexe -out:"$root\LayoutGlow.exe" "$root\src\LayoutGlow.cs" -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:"$fx\WPF\UIAutomationClient.dll" -r:"$fx\WPF\UIAutomationTypes.dll"
if ($LASTEXITCODE -eq 0) { Write-Host "BUILD OK: $root\LayoutGlow.exe" } else { Write-Host "BUILD FAILED ($LASTEXITCODE)" }
