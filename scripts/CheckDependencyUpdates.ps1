<#
.SYNOPSIS
    Checks NuGet packages for newer versions and both vendored dependencies (UAssetAPI source,
    retoc.exe) for upstream commits newer than the ones they were built from.

.DESCRIPTION
    `dotnet list package --outdated` and Dependabot see only NuGet packages. UAssetAPI is vendored
    as source and retoc as a binary built from a fork, so this reads each one's pinned commit from
    THIRD_PARTY_NOTICES.md and compares it against upstream on GitHub. UAssetAPI is version-coupled
    to Unreal Engine itself, so an upstream commit can be new engine or property-type support.

.NOTES
    Windows PowerShell 5.1. Run before cutting a release.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$notices = Get-Content -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Raw
$ghHeaders = @{ 'User-Agent' = 'UAssetEditor-dependency-check' }

function Get-PinnedCommit([string]$section, [string]$pattern) {
    $body = [regex]::Match($notices, "(?ms)^## $section\r?\n(.*?)(?=^## |\z)").Groups[1].Value
    $match = [regex]::Match($body, $pattern)
    if (-not $match.Success) { throw "THIRD_PARTY_NOTICES.md: no pinned commit found under '## $section'." }
    return $match.Groups[1].Value
}

function Write-Comparison([string]$repo, [string]$base, [string]$head, [string]$label) {
    try {
        $compare = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/compare/$base...$head" -Headers $ghHeaders
    }
    catch {
        Write-Warning "Couldn't compare $repo ${base}...${head}: $($_.Exception.Message)"
        return
    }
    if ($compare.ahead_by -eq 0) {
        Write-Host "Up to date - $label has no commits since $base." -ForegroundColor Green
        return
    }
    Write-Host "$label is $($compare.ahead_by) commit(s) ahead of $base`:" -ForegroundColor Yellow
    foreach ($commit in $compare.commits) {
        $date = ([DateTime]$commit.commit.author.date).ToString('yyyy-MM-dd')
        Write-Host "  $($commit.sha.Substring(0, 7))  $date  $(($commit.commit.message -split "`n")[0])"
    }
}

Write-Host '== NuGet package versions (dotnet list package --outdated) ==' -ForegroundColor Cyan
& dotnet list (Join-Path $repoRoot 'UAssetEditor.slnx') package --outdated
if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed with exit code $LASTEXITCODE." }

Write-Host ''
Write-Host '== UAssetAPI (vendored source) vs. atenfyr/UAssetAPI ==' -ForegroundColor Cyan
$uassetApiCommit = Get-PinnedCommit 'UAssetAPI' '`master` @ `([0-9a-f]{7,40})`'
Write-Host "Vendored from commit: $uassetApiCommit"
Write-Comparison 'atenfyr/UAssetAPI' $uassetApiCommit 'master' 'upstream master'

Write-Host ''
Write-Host '== retoc.exe (vendored binary) vs. its fork and trumank/retoc ==' -ForegroundColor Cyan
$retocCommit = Get-PinnedCommit 'retoc' 'built from commit `([0-9a-f]{7,40})` on branch'
$retocBranch = Get-PinnedCommit 'retoc' 'on branch `([^`]+)`'
Write-Host "Built from commit: $retocCommit ($retocBranch)"

$retocExe = Join-Path $repoRoot 'src\UAssetEditor.App\vendor\retoc.exe'
$pinnedHash = Get-PinnedCommit 'retoc' 'SHA-256 `([0-9a-f]{64})`'
$actualHash = (Get-FileHash -LiteralPath $retocExe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $pinnedHash) {
    Write-Warning "retoc.exe SHA-256 is $actualHash, but THIRD_PARTY_NOTICES.md records $pinnedHash."
}

Write-Comparison 'gravenuance/retoc' $retocCommit $retocBranch "fork branch $retocBranch"
Write-Comparison 'trumank/retoc' $retocCommit 'master' 'upstream master'
