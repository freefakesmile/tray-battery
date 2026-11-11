# 2026-11-11 23:47
# Builds with the .NET Framework compiler that ships with Windows (no SDK); version from the git tag (CI) or git describe

param([string]$Version)
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Push-Location $PSScriptRoot
try {
    # Tag build in CI: "v0.4.1" -> 0.4.1; local: git describe (e.g. 0.4.1-2-gabc1234); otherwise "dev".
    if (-not $Version) {
        if ($env:GITHUB_REF_TYPE -eq 'tag') { $Version = $env:GITHUB_REF_NAME }
        else { try { $Version = (& git describe --tags --always 2>$null) } catch { } }
    }
    if (-not $Version) { $Version = 'dev' }
    $Version = "$Version".Trim().TrimStart('v')
    $numeric = if ($Version -match '^(\d+)\.(\d+)\.(\d+)') { "$($Matches[1]).$($Matches[2]).$($Matches[3]).0" } else { '0.0.0.0' }
    $versionCs = Join-Path $env:TEMP 'TrayBattery.Version.cs'
    Set-Content $versionCs -Encoding UTF8 -Value @(
        "[assembly: System.Reflection.AssemblyInformationalVersion(`"$Version`")]",
        "[assembly: System.Reflection.AssemblyFileVersion(`"$numeric`")]",
        "[assembly: System.Reflection.AssemblyVersion(`"$numeric`")]",
        "[assembly: System.Reflection.AssemblyProduct(`"TrayBattery`")]")

    & $csc /nologo /target:winexe /platform:x64 /optimize+ /out:TrayBattery.exe `
        /win32manifest:app.manifest `
        /r:System.Drawing.dll `
        TrayBattery.cs $versionCs
    if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }
} finally { Pop-Location }
