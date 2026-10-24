# Build script for Moonfin Jellyfin plugin
# Creates a release ZIP with proper structure for plugin manifest
# Usage: .\build.ps1 -Changelog "What changed." [-Publish] [-Version "2.4.0.1"] [-TargetAbi "10.10.0"] [-SourceUrl "https://..."] [-SkipManifestUpdate]
# The version is read from the csproj (MoonfinVersion.MoonfinForkRevision) unless -Version is given.
# -Publish also creates the GitHub release fork-v<version> on Licaa21/Plugin with the zip, at the
# commit being built, which has to be on origin/main.

param(
    [string]$Version = "",
    [string]$Changelog = "",
    [string]$TargetAbi = "10.10.0",
    [string]$SourceUrl = "",
    [switch]$Publish,
    [switch]$SkipManifestUpdate
)

$ErrorActionPreference = "Stop"

$BuildTimestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$RootDir = $PSScriptRoot

if ($Version -eq "") {
    $CsprojText = [System.IO.File]::ReadAllText((Join-Path $RootDir "backend\Moonfin.Server.csproj"))
    $Base = [regex]::Match($CsprojText, '<MoonfinVersion>([^<]+)</MoonfinVersion>').Groups[1].Value
    $Fork = [regex]::Match($CsprojText, '<MoonfinForkRevision>([^<]+)</MoonfinForkRevision>').Groups[1].Value
    if ($Base -eq "" -or $Fork -eq "") { throw "MoonfinVersion or MoonfinForkRevision is missing from Moonfin.Server.csproj" }
    $Version = "$Base.$Fork"
}
if ($Version -notmatch '^\d+(\.\d+){3}$') { throw "'$Version' is not a four-number version. Check MoonfinVersion and MoonfinForkRevision in Moonfin.Server.csproj." }
$ReleaseTag = "fork-v$Version"
if ($Publish -and $SkipManifestUpdate) { throw "-Publish needs the manifest entry, drop -SkipManifestUpdate" }
if (-not $SkipManifestUpdate -and [string]::IsNullOrWhiteSpace($Changelog)) {
    throw "Give the release a changelog (-Changelog ""..."") or build without a manifest entry (-SkipManifestUpdate)"
}
if ($Publish) {
    if (git -C $RootDir status --porcelain) { throw "-Publish: commit or remove the changes first, the zip is built from the working tree and the release is cut at the commit" }
    git -C $RootDir fetch --quiet origin main
    if ($LASTEXITCODE -ne 0) { throw "-Publish: could not fetch origin/main, so the check that the commit is on it cannot be made" }
    git -C $RootDir merge-base --is-ancestor HEAD origin/main
    if ($LASTEXITCODE -ne 0) { throw "-Publish: this commit is not on origin/main. Releases are cut from main, merge and push it there first" }
    $Gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $Gh) { $Gh = Join-Path $env:LOCALAPPDATA "GitHubCLI\bin\gh.exe" }
    if (-not (Test-Path $Gh)) { throw "-Publish: gh is not installed" }
    # Windows PowerShell 5.1 turns gh's "release not found" on stderr into an error under Stop.
    $ErrorActionPreference = "Continue"
    & $Gh auth status *> $null
    $SignedIn = $LASTEXITCODE -eq 0
    & $Gh release view $ReleaseTag --repo Licaa21/Plugin *> $null
    $ReleaseExists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = "Stop"
    if (-not $SignedIn) { throw "-Publish: gh is not signed in (gh auth login)" }
    if ($ReleaseExists) { throw "-Publish: the release $ReleaseTag already exists. Releases are kept and never replaced, so raise MoonfinForkRevision" }
}
$BackendDir = Join-Path $RootDir "backend"
$FrontendDir = Join-Path $RootDir "frontend"
$VerifyDir = Join-Path $RootDir "tools\verify-plugin"
$VerifyProject = Join-Path $VerifyDir "VerifyPlugin.csproj"

Write-Host "Building Moonfin v${Version} for Jellyfin ${TargetAbi}..."
Write-Host "Build Time: ${BuildTimestamp}"

# Validate expected Flutter web bundle location
$FrontendIndex = Join-Path $FrontendDir "index.html"
if (-not (Test-Path $FrontendIndex)) {
    Write-Host ""
    Write-Host "Warning: frontend/index.html not found."
    Write-Host "Run Mobile-Desktop/build-web-plugin.sh before packaging if you need bundled web assets."
}

# Build the .NET plugin from clean Release state so the package cannot reuse an
# assembly produced from an older project or checkout path.
Write-Host ""
Write-Host "--- Building server plugin ---"
$CsprojPath = Join-Path $BackendDir "Moonfin.Server.csproj"
$PublishDir = Join-Path $BackendDir "bin\Release\net8.0\publish"
$ReleaseBinDir = Join-Path $BackendDir "bin\Release"
$ReleaseObjDir = Join-Path $BackendDir "obj\Release"
$VerifyReleaseBinDir = Join-Path $VerifyDir "bin\Release"
$VerifyReleaseObjDir = Join-Path $VerifyDir "obj\Release"
if (Test-Path $ReleaseBinDir) { Remove-Item $ReleaseBinDir -Recurse -Force }
if (Test-Path $ReleaseObjDir) { Remove-Item $ReleaseObjDir -Recurse -Force }
if (Test-Path $VerifyReleaseBinDir) { Remove-Item $VerifyReleaseBinDir -Recurse -Force }
if (Test-Path $VerifyReleaseObjDir) { Remove-Item $VerifyReleaseObjDir -Recurse -Force }
# Optional push relay key, read from a gitignored .env at the repo root. Without
# one the build still succeeds and the plugin ships with push disabled.
$RelayAppKey = ""
$EnvFile = Join-Path $PSScriptRoot "..\.env"
if (Test-Path $EnvFile) {
    $match = Select-String -Path $EnvFile -Pattern '^\s*MOONFIN_RELAY_APP_KEY\s*=' | Select-Object -Last 1
    if ($match) { $RelayAppKey = ($match.Line -split '=', 2)[1].Trim().Trim('"').Trim("'") }
}
if ($RelayAppKey) {
    Write-Host "Push relay key found, stamping it into this build."
} else {
    Write-Host "No push relay key, building with push disabled."
}

dotnet publish $CsprojPath -c Release -o $PublishDir `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:Version=$Version `
    "-p:MoonfinRelayAppKey=$RelayAppKey"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host ""
Write-Host "--- Verifying server plugin ---"
dotnet run --project $VerifyProject -c Release -- (Join-Path $PublishDir "Moonfin.Server.dll") $Version
if ($LASTEXITCODE -ne 0) { throw "plugin verification failed" }

# Create release directory
$ReleaseDir = Join-Path $RootDir "release"
if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }
New-Item -ItemType Directory -Path $ReleaseDir | Out-Null

# Copy the plugin DLL plus its bundled dependencies
$DllPath = Join-Path $PublishDir "Moonfin.Server.dll"
Copy-Item $DllPath $ReleaseDir
$SharpCompressPath = Join-Path $PublishDir "SharpCompress.dll"
Copy-Item $SharpCompressPath $ReleaseDir

# Bundle Flutter web files next to plugin DLL for local/sideload installs
if (Test-Path $FrontendIndex) {
    $ReleaseFrontend = Join-Path $ReleaseDir "frontend"
    New-Item -ItemType Directory -Path $ReleaseFrontend -Force | Out-Null
    Copy-Item (Join-Path $FrontendDir "*") $ReleaseFrontend -Recurse -Force

    $NodeModules = Join-Path $ReleaseFrontend "node_modules"
    if (Test-Path $NodeModules) { Remove-Item $NodeModules -Recurse -Force }

    $PackageJson = Join-Path $ReleaseFrontend "package.json"
    if (Test-Path $PackageJson) { Remove-Item $PackageJson -Force }

    $PackageLock = Join-Path $ReleaseFrontend "package-lock.json"
    if (Test-Path $PackageLock) { Remove-Item $PackageLock -Force }
}

# Generate meta.json for plugin discovery
$PluginGuid = "8c5d0e91-4f2a-4b6d-9e3f-1a7c8d9e0f2b"
$TimestampIso = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$Meta = [ordered]@{
    category = "General"
    changelog = ""
    description = "Moonbase is the Moonfin server plugin for Jellyfin, powering settings sync, the Moonfin Web app at /Moonfin/Web/, a built-in theme editor with custom theme APIs, media bar and ratings integrations, and Seerr proxy/SSO with admin defaults and broadcasts across Moonfin clients."
    guid = $PluginGuid
    name = "Moonbase"
    overview = "Moonfin server plugin for Jellyfin with web app hosting, settings sync, theming, and Seerr integrations"
    owner = "Licaa21"
    targetAbi = "${TargetAbi}.0"
    timestamp = $TimestampIso
    version = $Version
    status = "Active"
    autoUpdate = $true
    assemblies = @("Moonfin.Server.dll")
}
$MetaJson = ConvertTo-Json -InputObject $Meta -Depth 10
$MetaPath = Join-Path $ReleaseDir "meta.json"
[System.IO.File]::WriteAllText($MetaPath, $MetaJson, (New-Object System.Text.UTF8Encoding $false))

# Create the ZIP file
$ZipName = "Moonfin.Server-${Version}.zip"
$ZipPath = Join-Path $RootDir $ZipName
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path (Join-Path $ReleaseDir "*") -DestinationPath $ZipPath

# Calculate MD5 checksum
$Hash = (Get-FileHash $ZipPath -Algorithm MD5).Hash.ToUpper()

# Update manifest.json
$ManifestFile = Join-Path $RootDir "manifest.json"
$ManifestUpdated = $false
if (-not $SkipManifestUpdate -and (Test-Path $ManifestFile)) {
    $Timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")
    # Read as UTF-8 explicitly, Get-Content defaults to the system codepage on
    # Windows PowerShell 5.1 and would mangle non-ASCII characters
    $Manifest = [System.IO.File]::ReadAllText($ManifestFile) | ConvertFrom-Json
    $Manifest = @($Manifest)

    # Prepend rather than assign into versions[0], so cutting a release can't overwrite the
    # previous one's entry.
    if (@($Manifest[0].versions | Where-Object { $_.version -eq $Version }).Count -gt 0) {
        Write-Error "manifest.json already lists version $Version. Bump the version, or remove that entry first if you are deliberately re-cutting it."
        exit 1
    }

    if (-not [string]::IsNullOrWhiteSpace($SourceUrl)) {
        $NewSourceUrl = $SourceUrl
    } else {
        $NewSourceUrl = "https://github.com/Licaa21/Plugin/releases/download/$ReleaseTag/$ZipName"
    }

    $NewEntry = [ordered]@{
        version = $Version
        changelog = $Changelog
        targetAbi = "${TargetAbi}.0"
        sourceUrl = $NewSourceUrl
        checksum = $Hash
        timestamp = $Timestamp
        dependencies = @()
    }

    $Manifest[0].versions = @([pscustomobject]$NewEntry) + @($Manifest[0].versions)

    $Json = ConvertTo-Json -InputObject $Manifest -Depth 10
    if ($Manifest.Count -eq 1 -and -not $Json.TrimStart().StartsWith('[')) {
        $Json = "[$Json]"
    }
    [System.IO.File]::WriteAllText($ManifestFile, $Json, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "Prepended version $Version to manifest.json"

    # The catalog URL people add points at the manifest in the repo root, not this one.
    $RepoRootManifest = Join-Path (Split-Path $RootDir -Parent) "manifest.json"
    if (Test-Path $RepoRootManifest) {
        [System.IO.File]::WriteAllText($RepoRootManifest, $Json, (New-Object System.Text.UTF8Encoding $false))
        Write-Host "Synced the repo root manifest.json"
    }
    $ManifestUpdated = $true
}

# Last, so a failure above leaves no release behind. The manifest entry is only local until it is
# committed and pushed, which is when installs can see it.
if ($Publish) {
    # A file, since Windows PowerShell 5.1 mangles double quotes in native arguments.
    $NotesFile = Join-Path $RootDir "release-notes.tmp"
    [System.IO.File]::WriteAllText($NotesFile, $Changelog, (New-Object System.Text.UTF8Encoding $false))
    & $Gh release create $ReleaseTag $ZipPath --repo Licaa21/Plugin --target (git -C $RootDir rev-parse HEAD) --title "Moonbase $Version (fork)" --notes-file $NotesFile
    $GhExit = $LASTEXITCODE
    Remove-Item $NotesFile -Force
    if ($GhExit -ne 0) { throw "gh release create failed. The manifest entry is already written: undo it with git checkout on both manifest.json files before running again." }
    Write-Host "Published release $ReleaseTag. Commit and push the manifest.json files, then merge main into implement."
}

# Cleanup
Remove-Item $ReleaseDir -Recurse -Force

Write-Host ""
Write-Host "========================================="
Write-Host "Build complete!"
Write-Host "Build Time: ${BuildTimestamp}"
Write-Host "========================================="
Write-Host "ZIP file: $ZipName"
Write-Host "MD5 Checksum: $Hash"
Write-Host "Manifest updated: $ManifestUpdated"
Write-Host "========================================="
Write-Host ""
Write-Host "Done!"
