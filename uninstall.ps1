[CmdletBinding()]
param(
    [switch]$RemoveModels,
    [switch]$RemoveSettings
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

$installRoot = Join-Path `
    $env:LOCALAPPDATA `
    "Programs\CodexVoiceAssistant"
$dataRoot = Join-Path $env:LOCALAPPDATA "CodexVoiceAssistant"
$modelRoot = Join-Path $dataRoot "models"

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
Assert-RealDirectory -Path $dataRoot
Assert-RealDirectory -Path $modelRoot

$mutex = New-Object System.Threading.Mutex `
    -ArgumentList @(
        $false,
        "Local\CodexVoiceAssistant.Uninstaller")
$lockAcquired = $false

try {
    $lockAcquired = $mutex.WaitOne(
        [TimeSpan]::FromMinutes(5),
        $false)
    if (-not $lockAcquired) {
        throw "Another Codex Voice Assistant uninstaller is already running."
    }

    $stoppedProcesses = Stop-CodexVoiceAssistantProcess `
        -InstallRoot $installRoot
    if ($stoppedProcesses -gt 0) {
        Write-Host "Stopped $stoppedProcesses application process(es)."
    }

    $startupRemoved = Remove-StartupRegistrationSafely `
        -ExpectedInstallRoot $installRoot
    if ($startupRemoved) {
        Write-Host "Removed autostart registration."
    }
    if (Remove-LogonScheduledTask `
            -TaskName "CodexVoiceAssistant") {
        Write-Host "Removed logon task."
    }

    if (Test-FileSystemEntry -Path $installRoot) {
        Remove-DirectoryTreeSafely `
            -Path $installRoot `
            -AllowedRoot $localAppDataRoot
        Write-Host "Removed installation: $installRoot"
    }
    else {
        Write-Host "Installation directory was already absent."
    }

    if ($RemoveModels) {
        if (Test-Path -LiteralPath $modelRoot -PathType Container) {
            foreach ($manifestPath in @(
                    $modelManifestPath,
                    $kokoroModelManifestPath)) {
                if (-not (Test-Path `
                        -LiteralPath $manifestPath `
                        -PathType Leaf)) {
                    throw "Model manifest not found: $manifestPath"
                }

                $modelManifest = Get-ModelManifest -Path $manifestPath
                foreach ($model in @($modelManifest.models)) {
                    $target = Join-Path $modelRoot ([string]$model.name)
                    if (Test-Path -LiteralPath $target -PathType Leaf) {
                        Remove-FileSafely `
                            -Path $target `
                            -AllowedRoot $modelRoot
                    }

                    foreach ($partial in @(
                        Get-ChildItem `
                            -LiteralPath $modelRoot `
                            -File `
                            -Force |
                            Where-Object {
                                $_.Name.StartsWith(
                                    "$($model.name).partial.",
                                    [System.StringComparison]::OrdinalIgnoreCase)
                            })) {
                        Remove-FileSafely `
                            -Path $partial.FullName `
                            -AllowedRoot $modelRoot
                    }
                }
            }

            $remainingModels = @(
                Get-ChildItem -LiteralPath $modelRoot -Force)
            if ($remainingModels.Count -eq 0) {
                [System.IO.Directory]::Delete($modelRoot, $false)
                Write-Host "Removed Whisper model directory."
            }
        }

        $modelManifestCopy = Join-Path `
            $dataRoot `
            "whisper-models.manifest.json"
        if (Test-Path -LiteralPath $modelManifestCopy -PathType Leaf) {
            Remove-FileSafely `
                -Path $modelManifestCopy `
                -AllowedRoot $dataRoot
        }
        $kokoroModelManifestCopy = Join-Path `
            $dataRoot `
            "kokoro-models.manifest.json"
        if (Test-Path -LiteralPath $kokoroModelManifestCopy -PathType Leaf) {
            Remove-FileSafely `
                -Path $kokoroModelManifestCopy `
                -AllowedRoot $dataRoot
        }

        $kokoroRuntimeRoot = Join-Path $dataRoot "kokoro-runtime"
        if (Test-FileSystemEntry -Path $kokoroRuntimeRoot) {
            Remove-DirectoryTreeSafely `
                -Path $kokoroRuntimeRoot `
                -AllowedRoot $dataRoot
            Write-Host "Removed Kokoro runtime."
        }

        $senseVoiceRuntimeRoot = Join-Path `
            $dataRoot `
            "sensevoice-runtime"
        if (Test-FileSystemEntry -Path $senseVoiceRuntimeRoot) {
            Remove-DirectoryTreeSafely `
                -Path $senseVoiceRuntimeRoot `
                -AllowedRoot $dataRoot
            Write-Host "Removed SenseVoice runtime."
        }
    }

    if ($RemoveSettings) {
        foreach ($fileName in @(
                "settings.json",
                "voice-assistant.log",
                "whisper-models.manifest.json",
                "kokoro-models.manifest.json")) {
            $path = Join-Path $dataRoot $fileName
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Remove-FileSafely `
                    -Path $path `
                    -AllowedRoot $dataRoot
            }
        }
        Write-Host "Removed application settings and logs."
    }

    if (Test-Path -LiteralPath $dataRoot -PathType Container) {
        $remainingData = @(
            Get-ChildItem -LiteralPath $dataRoot -Force)
        if ($remainingData.Count -eq 0) {
            [System.IO.Directory]::Delete($dataRoot, $false)
        }
    }

    Write-Host "Codex Voice Assistant uninstalled." -ForegroundColor Green
}
finally {
    if ($lockAcquired) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
