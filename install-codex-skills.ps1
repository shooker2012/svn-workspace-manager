param(
    [string]$Destination = (Join-Path $env:USERPROFILE '.agents\skills')
)

$ErrorActionPreference = 'Stop'
$cli = Get-Command 'SvnWorkspaceManager.Cli.exe' -ErrorAction SilentlyContinue
if (-not $cli) {
    throw 'SvnWorkspaceManager.Cli.exe was not found on PATH. Install or publish the CLI before installing these skills.'
}

$sourceRoot = Join-Path $PSScriptRoot 'skills'
foreach ($skill in @('svn-task-workspace', 'svn-workspace-info')) {
    $source = Join-Path $sourceRoot $skill
    $target = Join-Path $Destination $skill
    New-Item -ItemType Directory -Force -Path (Join-Path $target 'agents') | Out-Null
    Copy-Item (Join-Path $source 'SKILL.md') $target -Force
    Copy-Item (Join-Path $source 'agents\openai.yaml') (Join-Path $target 'agents') -Force
}

Write-Host "Installed Codex skills to $Destination"
Write-Host "Using CLI: $($cli.Source)"
