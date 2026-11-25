param(
    [string]$CodexDestination = (Join-Path $env:USERPROFILE '.agents\skills'),
    [string]$CursorDestination = (Join-Path $env:USERPROFILE '.cursor\skills'),
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
if ($Destination) {
    $CodexDestination = $Destination
}

$cli = Get-Command 'SvnWorkspaceManager.Cli.exe' -ErrorAction SilentlyContinue
if (-not $cli) {
    throw 'SvnWorkspaceManager.Cli.exe was not found on PATH. Install or publish the CLI before installing these skills.'
}

$sourceRoot = Join-Path $PSScriptRoot 'skills'
$skills = @('svn-task-workspace', 'svn-workspace-info')

foreach ($skill in $skills) {
    $source = Join-Path $sourceRoot $skill
    $codexTarget = Join-Path $CodexDestination $skill
    New-Item -ItemType Directory -Force -Path (Join-Path $codexTarget 'agents') | Out-Null
    Copy-Item (Join-Path $source 'SKILL.md') $codexTarget -Force
    Copy-Item (Join-Path $source 'agents\openai.yaml') (Join-Path $codexTarget 'agents') -Force

    $cursorTarget = Join-Path $CursorDestination $skill
    New-Item -ItemType Directory -Force -Path $cursorTarget | Out-Null
    Copy-Item (Join-Path $source 'SKILL.md') $cursorTarget -Force
}

Write-Host "Installed Codex skills to $CodexDestination"
Write-Host "Installed Cursor skills to $CursorDestination"
Write-Host "Using CLI: $($cli.Source)"
