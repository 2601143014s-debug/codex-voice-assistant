[CmdletBinding()]
param(
    [switch]$Launch,
    [switch]$NoAutostart,
    [switch]$Repair
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$modulePath = Join-Path $root "installer\Install-Common.psm1"
$modelManifestPath = Join-Path `
    $root `
    "installer\whisper-models.manifest.json"
$kokoroModelManifestPath = Join-Path `
    $root `
    "installer\kokoro-models.manifest.json"
$kokoroRuntimeManifestPath = Join-Path `
    $root `
    "installer\kokoro-runtime.manifest.json"
$senseVoiceRuntimeManifestPath = Join-Path `
    $root `
    "installer\sensevoice-runtime.manifest.json"
$projectPath = Join-Path `
    $root `
    "src\CodexVoiceAssistant.App\CodexVoiceAssistant.App.csproj"
$installRoot = Join-Path `
    $env:LOCALAPPDATA `
    "Programs\CodexVoiceAssistant"
$dataRoot = Join-Path $env:LOCALAPPDATA "CodexVoiceAssistant"
$modelRoot = Join-Path $dataRoot "models"
$legacyRoot = Join-Path $env:LOCALAPPDATA "Jarvis"

$artifactRoot = Join-Path $root "installer\.artifacts"
$publishRoot = Join-Path $artifactRoot "publish"
$versionsRoot = Join-Path $installRoot "versions"
$stagingRoot = Join-Path $installRoot ".staging"
$currentPath = Join-Path $installRoot "current"
$statePath = Join-Path $installRoot "install-state.json"

function Move-LegacyInstallContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [Parameter(Mandatory = $true)]
        [string]$VersionsRoot
    )

    $currentPath = Join-Path $InstallRoot "current"
    $previousTarget = $null
    $legacyTarget = $null

    if (Test-FileSystemEntry -Path $currentPath) {
        $currentItem = Get-Item -LiteralPath $currentPath -Force
        $isReparse = (($currentItem.Attributes -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0)
        if ($isReparse) {
            $previousTarget = Get-ReparsePointTarget -Path $currentPath
            if (-not $previousTarget -or
                -not (Test-PathWithinOrEqual `
                    -Path $previousTarget `
                    -Root $InstallRoot)) {
                Write-Warning "Removing broken or out-of-root current junction."
                [System.IO.Directory]::Delete($currentPath, $false)
                $previousTarget = $null
            }
        }
        else {
            $legacyTarget = Join-Path `
                $VersionsRoot `
                ("legacy-{0}-{1}" -f
                    (Get-Date -Format "yyyyMMddHHmmss"),
                    [guid]::NewGuid().ToString("N").Substring(0, 8))
            New-Item `
                -ItemType Directory `
                -Force `
                -Path $legacyTarget | Out-Null
            Move-Item `
                -LiteralPath $currentPath `
                -Destination (Join-Path $legacyTarget "current")
            $previousTarget = Join-Path $legacyTarget "current"
        }
    }

    $remaining = @(
        Get-ChildItem -LiteralPath $InstallRoot -Force |
            Where-Object {
                $_.Name -notin @(
                    "versions",
                    ".staging",
                    "current",
                    "install-state.json")
            }
    )
    if ($remaining.Count -gt 0) {
        if (-not $legacyTarget) {
            $legacyTarget = Join-Path `
                $VersionsRoot `
                ("legacy-{0}-{1}" -f
                    (Get-Date -Format "yyyyMMddHHmmss"),
                    [guid]::NewGuid().ToString("N").Substring(0, 8))
            New-Item `
                -ItemType Directory `
                -Force `
                -Path $legacyTarget | Out-Null
        }

        foreach ($item in $remaining) {
            Move-Item `
                -LiteralPath $item.FullName `
                -Destination $legacyTarget
        }
    }

    if ($legacyTarget) {
        Write-Host "Preserved previous installation as rollback release."
    }

    return $previousTarget
}

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw "LOCALAPPDATA is not available."
}

if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    throw "Installer helper module not found: $modulePath"
}

Import-Module -Name $modulePath -Force

$localAppDataRoot = Get-NormalizedFullPath -Path $env:LOCALAPPDATA
$installRoot = Assert-PathWithin `
    -Path $installRoot `
    -Root $localAppDataRoot
$dataRoot = Assert-PathWithin `
    -Path $dataRoot `
    -Root $localAppDataRoot
$modelRoot = Assert-PathWithin `
    -Path $modelRoot `
    -Root $dataRoot
$legacyRoot = Assert-PathWithin `
    -Path $legacyRoot `
    -Root $localAppDataRoot
Assert-RealDirectory -Path $dataRoot
Assert-RealDirectory -Path $modelRoot

$dotnet = Get-Command "dotnet" -ErrorAction Stop
$null = Get-Command "curl.exe" -ErrorAction Stop

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Application project not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $modelManifestPath -PathType Leaf)) {
    throw "Whisper model manifest not found: $modelManifestPath"
}

$null = Get-ModelManifest -Path $modelManifestPath
if (-not (Test-Path -LiteralPath $kokoroModelManifestPath -PathType Leaf)) {
    throw "Kokoro model manifest not found: $kokoroModelManifestPath"
}
if (-not (Test-Path `
        -LiteralPath $kokoroRuntimeManifestPath `
        -PathType Leaf)) {
    throw "Kokoro runtime manifest not found: $kokoroRuntimeManifestPath"
}
if (-not (Test-Path `
        -LiteralPath $senseVoiceRuntimeManifestPath `
        -PathType Leaf)) {
    throw "SenseVoice runtime manifest not found: $senseVoiceRuntimeManifestPath"
}

$null = Get-ModelManifest -Path $kokoroModelManifestPath

$mutex = New-Object System.Threading.Mutex `
    -ArgumentList @(
        $false,
        "Local\CodexVoiceAssistant.Installer")
$lockAcquired = $false
$stagingPath = $null
$installedExecutable = $null
$releaseChanged = $false
$releaseId = $null

try {
    $lockAcquired = $mutex.WaitOne(
        [TimeSpan]::FromMinutes(10),
        $false)
    if (-not $lockAcquired) {
        throw "Another Codex Voice Assistant installer is already running."
    }

    Write-Host "Cleaning publish output..."
    Remove-DirectoryTreeSafely `
        -Path $publishRoot `
        -AllowedRoot (Join-Path $root "installer")
    New-Item `
        -ItemType Directory `
        -Force `
        -Path $publishRoot | Out-Null

    Write-Host "Publishing self-contained WPF application..."
    & $dotnet.Source publish `
        $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publishRoot `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $executableCandidates = @(
        Get-ChildItem `
            -LiteralPath $publishRoot `
            -File `
            -Force `
            -Filter "CodexVoiceAssistant.exe")
    if ($executableCandidates.Count -ne 1) {
        throw "Expected exactly one Codex Voice Assistant executable in publish output; found $($executableCandidates.Count)."
    }

    $publishedExecutable = $executableCandidates[0].FullName
    $executableName = $executableCandidates[0].Name
    $applicationBaseName = [System.IO.Path]::GetFileNameWithoutExtension(
        $executableName)
    foreach ($requiredFile in @(
            $publishedExecutable,
            (Join-Path $publishRoot "$applicationBaseName.dll"),
            (Join-Path $publishRoot "$applicationBaseName.deps.json"),
            (Join-Path $publishRoot "$applicationBaseName.runtimeconfig.json"))) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Published application is incomplete; missing '$requiredFile'."
        }
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
        $publishedExecutable)
    $productVersion = $versionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion)) {
        $productVersion = "1.0.0"
    }
    $safeVersion = [regex]::Replace(
        $productVersion,
        "[^0-9A-Za-z._-]",
        "_")

    $records = @(Get-ReleaseFileRecords -Path $publishRoot)
    $fingerprint = Get-ReleaseFingerprint -Records $records
    $releaseId = "{0}-{1}" -f $safeVersion, $fingerprint.Substring(0, 12)
    $null = New-ReleaseManifest `
        -ReleaseRoot $publishRoot `
        -Version $productVersion `
        -ReleaseId $releaseId `
        -Records $records `
        -SourceFingerprint $fingerprint

    if (-not (Test-ReleaseManifest `
            -ReleaseRoot $publishRoot `
            -ExpectedReleaseId $releaseId)) {
        throw "Published release manifest verification failed."
    }

    if (Test-FileSystemEntry -Path $installRoot) {
        Assert-RealDirectory -Path $installRoot
    }
    else {
        New-Item `
            -ItemType Directory `
            -Force `
            -Path $installRoot | Out-Null
    }

    Assert-RealDirectory -Path $versionsRoot
    Assert-RealDirectory -Path $stagingRoot
    if (-not (Test-FileSystemEntry -Path $versionsRoot)) {
        New-Item `
            -ItemType Directory `
            -Force `
            -Path $versionsRoot | Out-Null
    }

    $stagingPath = Join-Path `
        $stagingRoot `
        ("{0}-{1}" -f $releaseId, [guid]::NewGuid().ToString("N"))
    New-Item `
        -ItemType Directory `
        -Force `
        -Path $stagingPath | Out-Null
    Copy-DirectoryContents `
        -Source $publishRoot `
        -Destination $stagingPath
    if (-not (Test-ReleaseManifest `
            -ReleaseRoot $stagingPath `
            -ExpectedReleaseId $releaseId)) {
        throw "Staged release verification failed."
    }

    Write-Host "Stopping installed application before commit..."
    $stoppedProcesses = Stop-CodexVoiceAssistantProcess `
        -InstallRoot $installRoot
    if ($stoppedProcesses -gt 0) {
        Write-Host "Stopped $stoppedProcesses application process(es)."
    }

    $previousTarget = Move-LegacyInstallContent `
        -InstallRoot $installRoot `
        -VersionsRoot $versionsRoot
    $currentTarget = Get-ReparsePointTarget -Path $currentPath
    if ($currentTarget) {
        if (-not (Test-PathWithinOrEqual `
                -Path $currentTarget `
                -Root $installRoot)) {
            throw "Current junction points outside the installation root."
        }
        $previousTarget = $currentTarget
    }

    $targetReleaseRoot = Join-Path $versionsRoot $releaseId
    if (Test-FileSystemEntry -Path $targetReleaseRoot) {
        Assert-RealDirectory -Path $targetReleaseRoot
    }

    Write-Host "Installing verified models..."
    $null = Invoke-WhisperModelInstall `
        -ManifestPath $modelManifestPath `
        -ModelRoot $modelRoot `
        -LegacyRoot $legacyRoot
    $null = Invoke-WhisperModelInstall `
        -ManifestPath $kokoroModelManifestPath `
        -ModelRoot $modelRoot `
        -LegacyRoot $legacyRoot
    Install-PythonRuntime `
        -ManifestPath $kokoroRuntimeManifestPath `
        -PayloadRoot $root `
        -DataRoot $dataRoot
    Install-PythonRuntime `
        -ManifestPath $senseVoiceRuntimeManifestPath `
        -PayloadRoot $root `
        -DataRoot $dataRoot

    $stateSnapshot = Get-FileSnapshot -Path $statePath
    $startupSnapshot = Get-StartupRegistrationSnapshot
    $switchResult = $null
    $switched = $false

    try {
        $releaseChanged = Install-ReleaseDirectory `
            -Source $stagingPath `
            -Target $targetReleaseRoot `
            -ReleaseId $releaseId `
            -Force:$Repair

        $switchResult = Set-CurrentRelease `
            -InstallRoot $installRoot `
            -ReleaseRoot $targetReleaseRoot
        $switched = [bool]$switchResult.Switched

        $installedExecutable = Join-Path `
            $currentPath `
            $executableName
        if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
            throw "Active release executable not found: $installedExecutable"
        }

        $installedUtc = [DateTime]::UtcNow.ToString("o")
        if ($stateSnapshot.Exists) {
            try {
                $previousState = Read-JsonFile -Path $statePath
                if ($previousState.releaseId -eq $releaseId -and
                    -not [string]::IsNullOrWhiteSpace(
                        [string]$previousState.installedUtc)) {
                    $installedUtc = [string]$previousState.installedUtc
                }
            }
            catch {
            }
        }

        $state = [pscustomobject]@{
            schemaVersion = 1
            product = "CodexVoiceAssistant"
            releaseId = $releaseId
            version = $productVersion
            activePath = $targetReleaseRoot
            previousPath = $previousTarget
            sourceFingerprint = $fingerprint
            installedUtc = $installedUtc
        }
        Write-JsonFileAtomic `
            -Path $statePath `
            -Value $state `
            -Depth 4

        if ($NoAutostart) {
            $null = Remove-StartupRegistrationSafely `
                -ExpectedInstallRoot $installRoot
            $null = Remove-LogonScheduledTask `
                -TaskName "CodexVoiceAssistant"
            $null = Remove-LaunchWatcherShortcut
        }
        else {
            $null = Remove-StartupRegistrationSafely `
                -ExpectedInstallRoot $installRoot
            $null = Remove-LogonScheduledTask `
                -TaskName "CodexVoiceAssistant"
            try {
                Set-LogonScheduledTask `
                    -ExecutablePath $installedExecutable `
                    -TaskName "CodexVoiceAssistant" `
                    -Delay "PT5S" `
                    -UseStartupShortcut
            }
            catch {
                Write-Warning "Unable to register Codex launch watcher: $_"
            }
        }
    }
    catch {
        $commitError = $_
        try {
            if ($switched) {
                Restore-CurrentRelease `
                    -InstallRoot $installRoot `
                    -BackupPath $switchResult.BackupPath `
                    -PreviousTarget $previousTarget
            }

            Restore-FileSnapshot `
                -Path $statePath `
                -Snapshot $stateSnapshot `
                -AllowedRoot $installRoot
            Restore-StartupRegistrationSnapshot `
                -Snapshot $startupSnapshot
        }
        catch {
            throw "Installation commit failed: $commitError`nRollback also failed: $_"
        }

        throw $commitError
    }

    if ($switchResult -and
        $switchResult.BackupPath -and
        (Test-FileSystemEntry -Path $switchResult.BackupPath)) {
        Remove-DirectoryTreeSafely `
            -Path $switchResult.BackupPath `
            -AllowedRoot $installRoot
    }

    if ($stagingPath -and (Test-FileSystemEntry -Path $stagingPath)) {
        Remove-DirectoryTreeSafely `
            -Path $stagingPath `
            -AllowedRoot $stagingRoot
    }

    if (Test-FileSystemEntry -Path $stagingRoot) {
        $remainingStaging = @(
            Get-ChildItem -LiteralPath $stagingRoot -Force)
        if ($remainingStaging.Count -eq 0) {
            [System.IO.Directory]::Delete($stagingRoot, $false)
        }
    }

    $action = "Installed"
    if (-not $releaseChanged) {
        $action = "Repaired"
    }
    if ($switchResult -and
        -not $switchResult.Switched -and
        -not $releaseChanged) {
        $action = "Verified"
    }
    if ($Repair -and $releaseChanged) {
        $action = "Repaired"
    }

    Write-Host "$action release: $releaseId" -ForegroundColor Green
    Write-Host "Active executable: $installedExecutable"
    if ($NoAutostart) {
        Write-Host "Codex launch watcher: disabled"
    }
    else {
        Write-Host "Codex launch watcher: enabled"
    }

    if ($Launch) {
        Start-Process -FilePath $installedExecutable | Out-Null
        Write-Host "Started application."
    }
}
finally {
    if ($stagingPath -and
        (Test-FileSystemEntry -Path $stagingPath) -and
        (Test-FileSystemEntry -Path $installRoot)) {
        try {
            Remove-DirectoryTreeSafely `
                -Path $stagingPath `
                -AllowedRoot $installRoot
        }
        catch {
            Write-Warning "Unable to clean staging directory '$stagingPath': $_"
        }
    }

    if ($lockAcquired) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
