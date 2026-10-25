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
    if ($SourceUrl) { throw "-Publish: -SourceUrl would make the manifest point somewhere the release is not" }
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

# The Flutter web app (the Moonfin button in Jellyfin web, served at /Moonfin/Web/) is not in git. Upstream builds it
# from Moonfin-Core and adds it to its zip by hand, so this build takes it from upstream's published release: a
# pinned version, checked against its checksum (SHA-256; the file's MD5 is also upstream's own, E4B38A94589F515CBE975D186748E08E),
# unchanged. Its source is https://github.com/Moonfin-Client/Moonfin-Core.
$WebBundleUrl = "https://github.com/Moonfin-Client/Plugin/releases/download/2.4.0/Moonfin.Server-2.4.0.0.zip"
$WebBundleSha256 = "2DADFF3D8F4F797E18B394BF9825173F9BC2F49022D6A0220EF5FB6EB4FBD35B"
$WebBundleRequired = @("index.html", "main.dart.js", "version.json", "assets/AssetManifest.bin", "canvaskit/canvaskit.wasm")
function Test-WebApp { -not ($WebBundleRequired | Where-Object { -not (Test-Path (Join-Path $FrontendDir $_)) }) }
if (-not (Test-WebApp)) {
    Write-Host ""
    Write-Host "The web app is missing or incomplete in frontend/, taking it from upstream's 2.4.0.0 release (about 66 MB)..."
    try {
        # A folder of the user's own, not the shared temp folder, so nobody else can swap the file after the checksum.
        $WebCache = Join-Path $env:LOCALAPPDATA "moonfin-build"
        New-Item -ItemType Directory -Path $WebCache -Force | Out-Null
        $WebZip = Join-Path $WebCache "moonfin-upstream-2.4.0.0.zip"
        if (-not (Test-Path $WebZip) -or (Get-FileHash $WebZip -Algorithm SHA256).Hash -ne $WebBundleSha256) {
            $PreviousProgress = $ProgressPreference
            $ProgressPreference = "SilentlyContinue"
            # Windows PowerShell 5.1 may default to a TLS version GitHub no longer accepts.
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            try {
                Invoke-WebRequest -Uri $WebBundleUrl -OutFile $WebZip -UseBasicParsing
            } finally {
                $ProgressPreference = $PreviousProgress
            }
            if ((Get-FileHash $WebZip -Algorithm SHA256).Hash -ne $WebBundleSha256) {
                Remove-Item $WebZip -Force
                throw "The downloaded upstream zip does not match its known checksum, not using it."
            }
        }
        # Unpacked into a staging folder first, so a failure while unpacking leaves frontend/ as it was.
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $Staging = Join-Path ([System.IO.Path]::GetTempPath()) ("moonfin-web-" + [guid]::NewGuid().ToString("N"))
        $StagingRoot = $Staging + [System.IO.Path]::DirectorySeparatorChar
        try {
            $Extracted = 0
            $Archive = [System.IO.Compression.ZipFile]::OpenRead($WebZip)
            try {
                foreach ($Entry in $Archive.Entries) {
                    # Only the web app. The theme and editor folders are tracked here, so they stay as they are.
                    if (-not $Entry.FullName.StartsWith("frontend/", [System.StringComparison]::Ordinal) -or $Entry.FullName.EndsWith("/") -or
                        $Entry.FullName.StartsWith("frontend/theme/", [System.StringComparison]::Ordinal) -or
                        $Entry.FullName.StartsWith("frontend/editor/", [System.StringComparison]::Ordinal)) { continue }
                    $Target = [System.IO.Path]::GetFullPath((Join-Path $Staging ($Entry.FullName -replace '/', '\')))
                    if (-not $Target.StartsWith($StagingRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe path in the upstream zip: $($Entry.FullName)" }
                    New-Item -ItemType Directory -Path (Split-Path $Target -Parent) -Force | Out-Null
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($Entry, $Target, $true)
                    $Extracted++
                }
            } finally {
                $Archive.Dispose()
            }
            if ($Extracted -eq 0) { throw "The upstream zip held no frontend/ files in the layout this script expects." }
            # Leftovers of an earlier or other web app would ship next to these files, so the folder starts clean
            # (theme and editor are tracked here and stay).
            Get-ChildItem $FrontendDir -Force | Where-Object { $_.Name -notin @("theme", "editor", ".gitkeep") } | Remove-Item -Recurse -Force
            Copy-Item (Join-Path $Staging "frontend\*") $FrontendDir -Recurse -Force
        } finally {
            if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
        }
        Write-Host "Took $Extracted web files from upstream's zip."
    } catch {
        # A build that is not a release (-SkipManifestUpdate) can go on without the web app, a release cannot.
        if (-not $SkipManifestUpdate) { throw }
        Write-Host "Warning: could not get the web app: $($_.Exception.Message)"
    }
}
if (-not (Test-WebApp)) {
    if ($SkipManifestUpdate) {
        Write-Host "Warning: the web app is not in frontend/, this zip has none."
    } else {
        throw "The web app (frontend/index.html, main.dart.js, version.json) is missing, so the zip would have no web app and the Moonfin button would be dead. Not building a release without it."
    }
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
if ($RelayAppKey -and $Publish) { throw "-Publish: a push relay key is in .env and would be stamped into the public release. Move .env away for the release." }
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
if (Test-WebApp) {
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
# Not Compress-Archive: Windows PowerShell writes backslashes in the entry names, which a Linux Jellyfin would
# extract as files literally named "frontend\index.html", and the web app would not load.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$ZipStream = $null
$ZipArchive = $null
try {
    $ZipStream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
    $ZipArchive = New-Object System.IO.Compression.ZipArchive($ZipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    foreach ($ReleaseFile in Get-ChildItem $ReleaseDir -Recurse -File -Force) {
        $EntryName = $ReleaseFile.FullName.Substring($ReleaseDir.Length + 1) -replace '\\', '/'
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($ZipArchive, $ReleaseFile.FullName, $EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally {
    if ($ZipArchive) { $ZipArchive.Dispose() }
    if ($ZipStream) { $ZipStream.Dispose() }
}

# What was just written is checked, since a wrong zip only shows after it is installed.
$Check = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $EntryNames = @($Check.Entries | ForEach-Object { $_.FullName })
} finally {
    $Check.Dispose()
}
if (@($EntryNames | Where-Object { $_.Contains('\') }).Count -gt 0) { throw "The zip has backslashes in its entry names." }
if (-not $SkipManifestUpdate) {
    foreach ($Required in $WebBundleRequired) {
        if ($EntryNames -notcontains "frontend/$Required") { throw "The zip has no frontend/$Required, so its web app is incomplete." }
    }
}

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
    $NotesFile = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($NotesFile, $Changelog, (New-Object System.Text.UTF8Encoding $false))
    $ErrorActionPreference = "Continue"
    & $Gh release create $ReleaseTag $ZipPath --repo Licaa21/Plugin --target (git -C $RootDir rev-parse HEAD) --title "Moonbase $Version (fork)" --notes-file $NotesFile
    $GhExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    Remove-Item $NotesFile -Force
    if ($GhExit -ne 0) {
        # The tree was clean when -Publish started, so the manifests can be put back as they were.
        git -C $RootDir checkout -- ':/manifest.json' ':/Jellyfin/manifest.json'
        throw "gh release create failed. The manifest entries were undone, nothing was published."
    }
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
