<#
.SYNOPSIS
    Checks whether any NuGet dependency has a newer version available, whether the vendored
    retoc.exe is behind its latest GitHub release, and specifically whether UAssetAPI's
    upstream repo has unreleased commits since the exact commit our pinned version was built
    from.

.DESCRIPTION
    `dotnet list package --outdated` only catches an update once the maintainer has cut a
    new NuGet release. UAssetAPI is the one dependency here that's version-coupled to
    Unreal Engine itself (new UE releases need new UAssetAPI support), and its releases
    lag its GitHub commits by weeks to months - so this also compares our pinned version's
    exact commit (recorded in the NuGet package's own .nuspec) against the tip of its
    default branch, so a meaningful unreleased feature (e.g. a new UE version, a new
    property type) doesn't go unnoticed just because it hasn't been packaged yet.

    retoc.exe (src/UAssetEditor.App/vendor/) isn't a NuGet package at all - it's a vendored
    prebuilt binary (see THIRD_PARTY_NOTICES.md) - so `dotnet list package` can't see it
    either; this checks its own --version output against retoc's latest GitHub release tag.

.NOTES
    Run this whenever cutting a release (e.g. before `dotnet publish -p:PublishProfile=
    SingleFileRelease`) - see the "Dependency currency" project memory for why.
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "== NuGet package versions (dotnet list package --outdated) ==" -ForegroundColor Cyan
Push-Location $repoRoot
try {
    dotnet list package --outdated
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "== retoc.exe: vendored version vs. latest GitHub release ==" -ForegroundColor Cyan

$retocExe = Join-Path $repoRoot "src\UAssetEditor.App\vendor\retoc.exe"
if (-not (Test-Path $retocExe)) {
    Write-Warning "No vendored retoc.exe found at $retocExe"
}
else {
    $versionOutput = & $retocExe --version
    $pinnedRetocVersion = ($versionOutput -split ' ')[-1].Trim().TrimStart('v')
    Write-Host "Vendored version: $pinnedRetocVersion"

    try {
        $ghHeaders = @{ "User-Agent" = "UAssetEditor-dependency-check" }
        $latestRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/trumank/retoc/releases/latest" -Headers $ghHeaders
        $latestRetocVersion = $latestRelease.tag_name.TrimStart('v')

        if ($latestRetocVersion -eq $pinnedRetocVersion) {
            Write-Host "Up to date - $pinnedRetocVersion is the latest release." -ForegroundColor Green
        }
        else {
            Write-Host "Newer release available: $latestRetocVersion (vendored: $pinnedRetocVersion) - see https://github.com/trumank/retoc/releases" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Warning "Couldn't reach GitHub to check the latest retoc release: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "== UAssetAPI: unreleased commits since our pinned version ==" -ForegroundColor Cyan

$coreCsproj = Join-Path $repoRoot "src\UAssetEditor.Core\UAssetEditor.Core.csproj"
$csprojContent = Get-Content $coreCsproj -Raw
$versionMatch = [regex]::Match($csprojContent, '<PackageReference\s+Include="UAssetAPI"\s+Version="([^"]+)"')
if (-not $versionMatch.Success) {
    Write-Warning "Could not find a UAssetAPI PackageReference in $coreCsproj"
    return
}
$pinnedVersion = $versionMatch.Groups[1].Value
Write-Host "Pinned version: $pinnedVersion"

$nuspecPath = Join-Path $env:USERPROFILE ".nuget\packages\uassetapi\$pinnedVersion\uassetapi.nuspec"
if (-not (Test-Path $nuspecPath)) {
    Write-Warning "No local NuGet cache found at $nuspecPath - run 'dotnet restore' first, or check https://github.com/atenfyr/UAssetAPI/commits/main manually."
    return
}

[xml]$nuspec = Get-Content $nuspecPath
$pinnedCommit = $nuspec.package.metadata.repository.commit
if ([string]::IsNullOrWhiteSpace($pinnedCommit)) {
    Write-Warning "The .nuspec for $pinnedVersion doesn't record a commit hash - can't diff against upstream."
    return
}
Write-Host "Built from commit: $pinnedCommit"

$ghHeaders = @{ "User-Agent" = "UAssetEditor-dependency-check" }
try {
    $repoInfo = Invoke-RestMethod -Uri "https://api.github.com/repos/atenfyr/UAssetAPI" -Headers $ghHeaders
    $defaultBranch = $repoInfo.default_branch
    $compare = Invoke-RestMethod -Uri "https://api.github.com/repos/atenfyr/UAssetAPI/compare/$pinnedCommit...$defaultBranch" -Headers $ghHeaders
}
catch {
    Write-Warning "Couldn't reach GitHub to compare commits: $($_.Exception.Message)"
    return
}

if ($compare.ahead_by -eq 0) {
    Write-Host "Up to date - no commits on $defaultBranch since our pinned version." -ForegroundColor Green
}
else {
    Write-Host "$defaultBranch is $($compare.ahead_by) commit(s) ahead of our pinned version:" -ForegroundColor Yellow
    foreach ($commit in $compare.commits) {
        $sha = $commit.sha.Substring(0, 7)
        $firstLine = ($commit.commit.message -split "`n")[0]
        $date = ([DateTime]$commit.commit.author.date).ToString("yyyy-MM-dd")
        Write-Host "  $sha  $date  $firstLine"
    }
    Write-Host ""
    Write-Host "None of this is in a NuGet release yet - see https://github.com/atenfyr/UAssetAPI/releases" -ForegroundColor Yellow
}
