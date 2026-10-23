[CmdletBinding()]
param([switch]$SkipRestore)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'SvnWorkspaceManager.slnx'
$testProject = Join-Path $root 'tests\SvnWorkspaceManager.Tests\SvnWorkspaceManager.Tests.csproj'

if (-not $SkipRestore) {
    dotnet restore $solution
    if ($LASTEXITCODE -ne 0) {
        Write-Warning '在线还原失败，改用本机 NuGet 缓存搭建临时离线源。'
        $feed = Join-Path $env:TEMP 'svnwm-nuget-feed'
        New-Item -ItemType Directory -Force -Path $feed | Out-Null
        $cache = Join-Path $env:USERPROFILE '.nuget\packages'
        if (Test-Path -LiteralPath $cache) {
            Get-ChildItem -LiteralPath $cache -Recurse -Filter *.nupkg | ForEach-Object {
                $destination = Join-Path $feed $_.Name
                if (-not (Test-Path -LiteralPath $destination)) {
                    Copy-Item -LiteralPath $_.FullName -Destination $destination
                }
            }
        }
        dotnet restore $solution --source $feed
    }
}

dotnet build $solution --no-restore -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test $testProject --no-build --logger "console;verbosity=normal"
exit $LASTEXITCODE
