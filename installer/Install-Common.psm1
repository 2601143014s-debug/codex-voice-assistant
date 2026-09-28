Set-StrictMode -Version 2.0

$script:ProductName = "CodexVoiceAssistant"
$script:ProcessNames = @(
    "CodexVoiceAssistant",
    "CodexVoiceAssistant.App")
$script:StartupValueName = "CodexVoiceAssistant"
$script:StartupRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Get-NormalizedFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Path must not be empty."
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-PathWithin {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root,

        [switch]$AllowRoot
    )

    $normalizedPath = Get-NormalizedFullPath -Path $Path
    $normalizedRoot = Get-NormalizedFullPath -Path $Root

    if (-not $AllowRoot -and
        $normalizedPath.Equals(
            $normalizedRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must be below the allowed root: $normalizedPath"
    }

    $rootPrefix = $normalizedRoot.TrimEnd("\") + "\"
    if (-not $normalizedPath.Equals(
            $normalizedRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $normalizedPath.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$normalizedPath' is outside allowed root '$normalizedRoot'."
    }

    return $normalizedPath
}

function Test-ReparsePoint {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileSystemInfo]$Item
    )

    return (($Item.Attributes -band
        [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Test-FileSystemEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $null -ne (Get-Item `
        -LiteralPath $Path `
        -Force `
        -ErrorAction SilentlyContinue)
}

function Assert-RealDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-FileSystemEntry -Path $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer) {
        throw "Expected a directory: $Path"
    }

    if (Test-ReparsePoint -Item $item) {
        throw "Refusing to use reparse-point directory '$Path'."
    }
}

function Remove-DirectoryTreeSafely {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot
    )

    $normalizedPath = Assert-PathWithin `
        -Path $Path `
        -Root $AllowedRoot

    if (-not (Test-FileSystemEntry -Path $normalizedPath)) {
        return
    }

    $item = Get-Item -LiteralPath $normalizedPath -Force
    if (-not $item.PSIsContainer) {
        throw "Expected a directory: $normalizedPath"
    }

    if (Test-ReparsePoint -Item $item) {
        [System.IO.Directory]::Delete($normalizedPath, $false)
        return
    }

    function Remove-Children {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Directory
        )

        foreach ($child in Get-ChildItem -LiteralPath $Directory -Force) {
            if (Test-ReparsePoint -Item $child) {
                if ($child.PSIsContainer) {
                    [System.IO.Directory]::Delete(
                        $child.FullName,
                        $false)
                }
                else {
                    [System.IO.File]::Delete($child.FullName)
                }
                continue
            }

            if ($child.PSIsContainer) {
                Remove-Children -Directory $child.FullName
                [System.IO.Directory]::Delete(
                    $child.FullName,
                    $false)
            }
            else {
                [System.IO.File]::Delete($child.FullName)
            }
        }
    }

    Remove-Children -Directory $normalizedPath
    [System.IO.Directory]::Delete($normalizedPath, $false)
}

function Remove-FileSafely {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot
    )

    $normalizedPath = Assert-PathWithin `
        -Path $Path `
        -Root $AllowedRoot `
        -AllowRoot

    if (Test-Path -LiteralPath $normalizedPath) {
        [System.IO.File]::Delete($normalizedPath)
    }
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "File not found: $Path"
    }

    return (Get-FileHash `
        -LiteralPath $Path `
        -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-FileIntegrity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [long]$Length,

        [Parameter(Mandatory = $true)]
        [string]$Sha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $Length) {
        return $false
    }

    return (Get-FileSha256 -Path $Path) -eq $Sha256.ToLowerInvariant()
}

function Assert-FileIntegrity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [long]$Length,

        [Parameter(Mandatory = $true)]
        [string]$Sha256
    )

    if (-not (Test-FileIntegrity `
            -Path $Path `
            -Length $Length `
            -Sha256 $Sha256)) {
        throw "SHA-256 or size verification failed for '$Path'."
    }
}

function Get-RelativePathPortable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalizedRoot = Get-NormalizedFullPath -Path $Root
    $normalizedPath = Get-NormalizedFullPath -Path $Path
    $rootPrefix = $normalizedRoot.TrimEnd("\") + "\"

    if (-not $normalizedPath.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$normalizedPath' is outside '$normalizedRoot'."
    }

    return $normalizedPath.Substring($rootPrefix.Length).Replace("\", "/")
}

function Assert-SafeRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [System.IO.Path]::IsPathRooted($Path)) {
        throw "Unsafe relative path in manifest: '$Path'."
    }

    $parts = $Path.Replace("\", "/").Split("/")
    foreach ($part in $parts) {
        if ($part -eq "..") {
            throw "Unsafe relative path in manifest: '$Path'."
        }
    }

    return $Path.Replace("\", "/")
}

function Get-ReleaseFileRecords {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string[]]$ExcludeRelativePath = @()
    )

    $root = Get-NormalizedFullPath -Path $Path
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Release directory not found: $root"
    }

    $excluded = @{}
    foreach ($relativePath in $ExcludeRelativePath) {
        $key = $relativePath.Replace("\", "/").ToLowerInvariant()
        $excluded[$key] = $true
    }

    $records = @()
    foreach ($file in Get-ChildItem `
            -LiteralPath $root `
            -File `
            -Recurse `
            -Force) {
        $relativePath = Get-RelativePathPortable `
            -Root $root `
            -Path $file.FullName
        if ($excluded.ContainsKey($relativePath.ToLowerInvariant())) {
            continue
        }

        $records += [pscustomobject]@{
            Path = $relativePath
            Length = [long]$file.Length
            Sha256 = Get-FileSha256 -Path $file.FullName
        }
    }

    return @($records | Sort-Object -Property Path)
}

function Get-ReleaseFingerprint {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Records
    )

    $builder = New-Object System.Text.StringBuilder
    foreach ($record in $Records) {
        [void]$builder.Append(
            $record.Path.ToLowerInvariant())
        [void]$builder.Append("|")
        [void]$builder.Append(([long]$record.Length).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture))
        [void]$builder.Append("|")
        [void]$builder.Append($record.Sha256.ToLowerInvariant())
        [void]$builder.Append("`n")
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString(
            $sha256.ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Write-JsonFileAtomic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Value,

        [int]$Depth = 6
    )

    $normalizedPath = Get-NormalizedFullPath -Path $Path
    $parent = Split-Path -Path $normalizedPath -Parent
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth $Depth
    $temporary = "$normalizedPath.tmp.$([guid]::NewGuid().ToString("N"))"

    try {
        [System.IO.File]::WriteAllText(
            $temporary,
            $json,
            $script:Utf8NoBom)
        Move-Item `
            -LiteralPath $temporary `
            -Destination $normalizedPath `
            -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            [System.IO.File]::Delete($temporary)
        }
    }
}

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "JSON file not found: $Path"
    }

    try {
        return ([System.IO.File]::ReadAllText($Path) | ConvertFrom-Json)
    }
    catch {
        throw "Invalid JSON file '$Path': $($_.Exception.Message)"
    }
}

function New-ReleaseManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReleaseRoot,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$ReleaseId,

        [Parameter(Mandatory = $true)]
        [object[]]$Records,

        [Parameter(Mandatory = $true)]
        [string]$SourceFingerprint
    )

    $manifestPath = Join-Path $ReleaseRoot "install.manifest.json"
    $manifest = [pscustomobject]@{
        schemaVersion = 1
        product = $script:ProductName
        version = $Version
        releaseId = $ReleaseId
        sourceFingerprint = $SourceFingerprint
        createdUtc = [DateTime]::UtcNow.ToString("o")
        files = [object[]]@(
            $Records | ForEach-Object {
                [pscustomobject]@{
                    path = $_.Path
                    length = [long]$_.Length
                    sha256 = $_.Sha256
                }
            }
        )
    }

    Write-JsonFileAtomic `
        -Path $manifestPath `
        -Value $manifest `
        -Depth 6

    return $manifestPath
}

function Test-ReleaseManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReleaseRoot,

        [string]$ExpectedReleaseId
    )

    $root = Get-NormalizedFullPath -Path $ReleaseRoot
    $manifestPath = Join-Path $root "install.manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return $false
    }

    try {
        $manifest = Read-JsonFile -Path $manifestPath
    }
    catch {
        return $false
    }

    if ($manifest.schemaVersion -ne 1 -or
        $manifest.product -ne $script:ProductName) {
        return $false
    }

    if ($ExpectedReleaseId -and
        $manifest.releaseId -ne $ExpectedReleaseId) {
        return $false
    }

    $expected = @{}
    foreach ($record in @($manifest.files)) {
        try {
            $relativePath = Assert-SafeRelativePath -Path $record.path
        }
        catch {
            return $false
        }

        $expected[$relativePath.ToLowerInvariant()] = $true
        $filePath = Get-NormalizedFullPath -Path (
            Join-Path $root $relativePath.Replace("/", "\"))
        try {
            $filePath = Assert-PathWithin `
                -Path $filePath `
                -Root $root
        }
        catch {
            return $false
        }

        if (-not (Test-FileIntegrity `
                -Path $filePath `
                -Length ([long]$record.length) `
                -Sha256 $record.sha256)) {
            return $false
        }
    }

    try {
        $actual = Get-ReleaseFileRecords `
            -Path $root `
            -ExcludeRelativePath @("install.manifest.json")
    }
    catch {
        return $false
    }

    if ($actual.Count -ne $expected.Count) {
        return $false
    }

    foreach ($record in $actual) {
        if (-not $expected.ContainsKey($record.Path.ToLowerInvariant())) {
            return $false
        }
    }

    return $true
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    $normalizedSource = Get-NormalizedFullPath -Path $Source
    $normalizedDestination = Get-NormalizedFullPath -Path $Destination

    if (-not (Test-Path -LiteralPath $normalizedSource -PathType Container)) {
        throw "Source directory not found: $normalizedSource"
    }

    if (-not (Test-Path -LiteralPath $normalizedDestination)) {
        New-Item `
            -ItemType Directory `
            -Force `
            -Path $normalizedDestination | Out-Null
    }

    foreach ($item in Get-ChildItem -LiteralPath $normalizedSource -Force) {
        if (Test-ReparsePoint -Item $item) {
            throw "Refusing to copy reparse point '$($item.FullName)'."
        }

        Copy-Item `
            -LiteralPath $item.FullName `
            -Destination $normalizedDestination `
            -Recurse `
            -Force
    }
}

function Get-ReparsePointTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-FileSystemEntry -Path $Path)) {
        return $null
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (-not (Test-ReparsePoint -Item $item)) {
        return $null
    }

    $target = $item.Target
    if ($target -is [array]) {
        if ($target.Count -eq 0) {
            return $null
        }
        $target = $target[0]
    }

    if ([string]::IsNullOrWhiteSpace([string]$target)) {
        return $null
    }

    if (-not [System.IO.Path]::IsPathRooted([string]$target)) {
        $target = Join-Path $item.Parent.FullName ([string]$target)
    }

    return (Get-NormalizedFullPath -Path ([string]$target))
}

function New-DirectoryJunction {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    if (Test-FileSystemEntry -Path $Path) {
        throw "Junction path already exists: $Path"
    }

    if (-not (Test-Path -LiteralPath $Target -PathType Container)) {
        throw "Junction target does not exist: $Target"
    }

    New-Item `
        -Path $Path `
        -ItemType Junction `
        -Target $Target | Out-Null
}

function Set-CurrentRelease {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [Parameter(Mandatory = $true)]
        [string]$ReleaseRoot
    )

    $normalizedInstallRoot = Get-NormalizedFullPath -Path $InstallRoot
    $normalizedReleaseRoot = Assert-PathWithin `
        -Path $ReleaseRoot `
        -Root $normalizedInstallRoot
    $currentPath = Join-Path $normalizedInstallRoot "current"
    $currentTarget = Get-ReparsePointTarget -Path $currentPath

    if ($currentTarget -and
        $currentTarget.Equals(
            $normalizedReleaseRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{
            Switched = $false
            BackupPath = $null
        }
    }

    if (Test-FileSystemEntry -Path $currentPath) {
        $currentItem = Get-Item -LiteralPath $currentPath -Force
        if (-not (Test-ReparsePoint -Item $currentItem)) {
            throw "Refusing to replace non-junction current path '$currentPath'."
        }
    }

    $suffix = [guid]::NewGuid().ToString("N")
    $newLink = Join-Path $normalizedInstallRoot ".current-new-$suffix"
    $backupLink = Join-Path $normalizedInstallRoot ".current-backup-$suffix"

    New-DirectoryJunction -Path $newLink -Target $normalizedReleaseRoot

    $backupCreated = $false
    try {
        if (Test-FileSystemEntry -Path $currentPath) {
            Move-Item `
                -LiteralPath $currentPath `
                -Destination $backupLink
            $backupCreated = $true
        }

        Move-Item `
            -LiteralPath $newLink `
            -Destination $currentPath

        $verifiedTarget = Get-ReparsePointTarget -Path $currentPath
        if (-not $verifiedTarget -or
            -not $verifiedTarget.Equals(
                $normalizedReleaseRoot,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Current junction verification failed."
        }

        $backupPathValue = $null
        if ($backupCreated) {
            $backupPathValue = $backupLink
        }

        return [pscustomobject]@{
            Switched = $true
            BackupPath = $backupPathValue
        }
    }
    catch {
        if (Test-FileSystemEntry -Path $currentPath) {
            $failedCurrent = Get-Item -LiteralPath $currentPath -Force
            if (Test-ReparsePoint -Item $failedCurrent) {
                [System.IO.Directory]::Delete($currentPath, $false)
            }
        }

        if ($backupCreated -and (Test-Path -LiteralPath $backupLink)) {
            Move-Item `
                -LiteralPath $backupLink `
                -Destination $currentPath
        }

        throw
    }
    finally {
        if (Test-FileSystemEntry -Path $newLink) {
            $newItem = Get-Item -LiteralPath $newLink -Force
            if (Test-ReparsePoint -Item $newItem) {
                [System.IO.Directory]::Delete($newLink, $false)
            }
        }
    }
}

function Restore-CurrentRelease {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [string]$BackupPath,

        [string]$PreviousTarget
    )

    $normalizedInstallRoot = Get-NormalizedFullPath -Path $InstallRoot
    $currentPath = Join-Path $normalizedInstallRoot "current"

    if (Test-FileSystemEntry -Path $currentPath) {
        $currentItem = Get-Item -LiteralPath $currentPath -Force
        if (Test-ReparsePoint -Item $currentItem) {
            [System.IO.Directory]::Delete($currentPath, $false)
        }
        else {
            throw "Refusing to remove non-junction current path '$currentPath'."
        }
    }

    if ($BackupPath -and (Test-FileSystemEntry -Path $BackupPath)) {
        Move-Item `
            -LiteralPath $BackupPath `
            -Destination $currentPath
        return
    }

    if ($PreviousTarget -and
        (Test-Path -LiteralPath $PreviousTarget -PathType Container)) {
        New-DirectoryJunction `
            -Path $currentPath `
            -Target $PreviousTarget
    }
}

function Install-ReleaseDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Target,

        [Parameter(Mandatory = $true)]
        [string]$ReleaseId,

        [switch]$Force
    )

    $normalizedSource = Get-NormalizedFullPath -Path $Source
    $normalizedTarget = Get-NormalizedFullPath -Path $Target
    $targetParent = Split-Path -Path $normalizedTarget -Parent
    if (-not (Test-Path -LiteralPath $targetParent -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    }

    if (Test-FileSystemEntry -Path $normalizedTarget) {
        if (-not $Force -and
            (Test-ReleaseManifest `
                -ReleaseRoot $normalizedTarget `
                -ExpectedReleaseId $ReleaseId)) {
            return $false
        }

        $quarantine = Join-Path `
            $targetParent `
            (".replace-" + [guid]::NewGuid().ToString("N"))
        Move-Item `
            -LiteralPath $normalizedTarget `
            -Destination $quarantine

        try {
            Move-Item `
                -LiteralPath $normalizedSource `
                -Destination $normalizedTarget

            if (-not (Test-ReleaseManifest `
                    -ReleaseRoot $normalizedTarget `
                    -ExpectedReleaseId $ReleaseId)) {
                throw "Installed release verification failed: $normalizedTarget"
            }

            Remove-DirectoryTreeSafely `
                -Path $quarantine `
                -AllowedRoot $targetParent
            return $true
        }
        catch {
            if (Test-FileSystemEntry -Path $normalizedTarget) {
                Remove-DirectoryTreeSafely `
                    -Path $normalizedTarget `
                    -AllowedRoot $targetParent
            }
            if (Test-FileSystemEntry -Path $quarantine) {
                Move-Item `
                    -LiteralPath $quarantine `
                    -Destination $normalizedTarget
            }
            throw
        }
    }

    Move-Item `
        -LiteralPath $normalizedSource `
        -Destination $normalizedTarget

    if (-not (Test-ReleaseManifest `
            -ReleaseRoot $normalizedTarget `
            -ExpectedReleaseId $ReleaseId)) {
        throw "Installed release verification failed: $normalizedTarget"
    }

    return $true
}

function Get-CodexVoiceAssistantProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot
    )

    $normalizedInstallRoot = Get-NormalizedFullPath -Path $InstallRoot
    $matches = @()

    try {
        $cimProcesses = @()
        foreach ($processName in $script:ProcessNames) {
            $cimProcesses += @(
                Get-CimInstance `
                    -ClassName Win32_Process `
                    -Filter "Name = '$processName.exe'")
        }

        foreach ($process in $cimProcesses) {
            $processPath = $process.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($processPath)) {
                continue
            }

            try {
                $null = Assert-PathWithin `
                    -Path $processPath `
                    -Root $normalizedInstallRoot
                $matches += [pscustomobject]@{
                    Id = [int]$process.ProcessId
                    Path = $processPath
                }
            }
            catch {
            }
        }
    }
    catch {
        foreach ($process in Get-Process `
                -Name $script:ProcessNames `
                -ErrorAction SilentlyContinue) {
            try {
                $processPath = $process.Path
                $null = Assert-PathWithin `
                    -Path $processPath `
                    -Root $normalizedInstallRoot
                $matches += [pscustomobject]@{
                    Id = [int]$process.Id
                    Path = $processPath
                }
            }
            catch {
            }
        }
    }

    return @($matches | Sort-Object -Property Id -Unique)
}

function Stop-CodexVoiceAssistantProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [int]$GracefulTimeoutSeconds = 8,

        [int]$ForcedTimeoutSeconds = 8
    )

    $processes = @(
        Get-CodexVoiceAssistantProcesses -InstallRoot $InstallRoot)
    if ($processes.Count -eq 0) {
        return 0
    }

    foreach ($process in $processes) {
        try {
            $liveProcess = Get-Process -Id $process.Id -ErrorAction Stop
            $null = $liveProcess.CloseMainWindow()
        }
        catch {
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($GracefulTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and
        @(
            Get-CodexVoiceAssistantProcesses `
                -InstallRoot $InstallRoot).Count -gt 0) {
        Start-Sleep -Milliseconds 250
    }

    $remaining = @(
        Get-CodexVoiceAssistantProcesses -InstallRoot $InstallRoot)
    foreach ($process in $remaining) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($ForcedTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and
        @(
            Get-CodexVoiceAssistantProcesses `
                -InstallRoot $InstallRoot).Count -gt 0) {
        Start-Sleep -Milliseconds 250
    }

    $remaining = @(
        Get-CodexVoiceAssistantProcesses -InstallRoot $InstallRoot)
    if ($remaining.Count -gt 0) {
        throw "Unable to stop Codex Voice Assistant processes: $(
            ($remaining | ForEach-Object { $_.Id }) -join ', ')"
    }

    return $processes.Count
}

function Get-StartupRegistrationSnapshot {
    $property = Get-ItemProperty `
        -Path $script:StartupRegistryPath `
        -Name $script:StartupValueName `
        -ErrorAction SilentlyContinue

    if ($null -eq $property -or
        $null -eq $property.PSObject.Properties[$script:StartupValueName]) {
        return [pscustomobject]@{
            Exists = $false
            Value = $null
        }
    }

    $propertyValue = $property.PSObject.Properties[
        $script:StartupValueName].Value
    return [pscustomobject]@{
        Exists = $true
        Value = [string]$propertyValue
    }
}

function Set-StartupRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedInstallRoot
    )

    $normalizedExecutable = Assert-PathWithin `
        -Path $ExecutablePath `
        -Root $ExpectedInstallRoot
    $snapshot = Get-StartupRegistrationSnapshot
    if ($snapshot.Exists) {
        $existingPath = Get-ExecutablePathFromCommandLine `
            -CommandLine $snapshot.Value
        if (-not $existingPath -or
            -not (Test-PathWithinOrEqual `
                -Path $existingPath `
                -Root $ExpectedInstallRoot)) {
            throw "Refusing to overwrite unrelated startup value '$(
                $snapshot.Value)'."
        }
    }

    New-Item -Path $script:StartupRegistryPath -Force | Out-Null
    Set-ItemProperty `
        -Path $script:StartupRegistryPath `
        -Name $script:StartupValueName `
        -Value ("`"$normalizedExecutable`"") `
        -Type String
}

function Get-ExecutablePathFromCommandLine {
    param(
        [AllowNull()]
        [string]$CommandLine
    )

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return $null
    }

    $trimmed = $CommandLine.Trim()
    if ($trimmed.StartsWith('"')) {
        $closingQuote = $trimmed.IndexOf('"', 1)
        if ($closingQuote -lt 2) {
            return $null
        }
        return $trimmed.Substring(1, $closingQuote - 1)
    }

    $match = [regex]::Match(
        $trimmed,
        '^(?<path>.+?\.exe)(?:\s|$)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups["path"].Value
}

function Test-PathWithinOrEqual {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    try {
        $null = Assert-PathWithin `
            -Path $Path `
            -Root $Root `
            -AllowRoot
        return $true
    }
    catch {
        return $false
    }
}

function Remove-StartupRegistrationSafely {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedInstallRoot
    )

    $snapshot = Get-StartupRegistrationSnapshot
    if (-not $snapshot.Exists) {
        return $false
    }

    $existingPath = Get-ExecutablePathFromCommandLine `
        -CommandLine $snapshot.Value
    if (-not $existingPath -or
        -not (Test-PathWithinOrEqual `
            -Path $existingPath `
            -Root $ExpectedInstallRoot)) {
        Write-Warning "Startup value '$($snapshot.Value)' is not owned by '$ExpectedInstallRoot'; leaving it unchanged."
        return $false
    }

    Remove-ItemProperty `
        -Path $script:StartupRegistryPath `
        -Name $script:StartupValueName `
        -ErrorAction SilentlyContinue
    return $true
}

function Restore-StartupRegistrationSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Snapshot
    )

    if ($Snapshot.Exists) {
        Set-ItemProperty `
            -Path $script:StartupRegistryPath `
            -Name $script:StartupValueName `
            -Value ([string]$Snapshot.Value) `
            -Type String
    }
    else {
        Remove-ItemProperty `
            -Path $script:StartupRegistryPath `
            -Name $script:StartupValueName `
            -ErrorAction SilentlyContinue
    }
}

function Set-LogonScheduledTask {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [string]$TaskName = "CodexVoiceAssistant",

    [string]$Delay = "PT5S",

    [switch]$UseStartupShortcut
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Scheduled task executable not found: $ExecutablePath"
    }

    $normalizedExecutable = Get-NormalizedFullPath `
        -Path $ExecutablePath
    $releaseDirectory = Split-Path -Path $normalizedExecutable -Parent
    $installRoot = Split-Path -Path $releaseDirectory -Parent
    $watcherPath = Join-Path $installRoot "launch-when-codex.ps1"
    $watcherContent = @'
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

$ErrorActionPreference = "SilentlyContinue"
$healthPath = Join-Path `
    $env:LOCALAPPDATA `
    "CodexVoiceAssistant\runtime-health.json"

function Get-CodexProcesses {
    return @(
        Get-Process -Name "ChatGPT" -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Path -like "*\OpenAI.Codex_*\app\ChatGPT.exe" -or
                -not [string]::IsNullOrWhiteSpace($_.MainWindowTitle)
            })
}

function Get-AssistantProcesses {
    return @(
        Get-CimInstance `
            -ClassName Win32_Process `
            -Filter "Name = 'CodexVoiceAssistant.exe'" |
            Where-Object {
                $_.ExecutablePath -and
                $_.ExecutablePath.Equals(
                    $ExecutablePath,
                    [System.StringComparison]::OrdinalIgnoreCase)
            })
}

function Stop-AssistantProcesses {
    foreach ($process in @(Get-AssistantProcesses)) {
        Stop-Process `
            -Id ([int]$process.ProcessId) `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

function Test-AssistantHealthy {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Process
    )

    if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
        return $false
    }
    try {
        $health = Get-Content -LiteralPath $healthPath -Raw |
            ConvertFrom-Json
        $updated = [DateTimeOffset]$health.updatedUtc
        return [int]$health.processId -eq [int]$Process.ProcessId -and
            $updated -gt [DateTimeOffset]::UtcNow.AddSeconds(-20)
    }
    catch {
        return $false
    }
}

function Start-AssistantForSession {
    if (@(Get-AssistantProcesses).Count -eq 0) {
        Remove-Item `
            -LiteralPath $healthPath `
            -Force `
            -ErrorAction SilentlyContinue
        Start-Process `
            -FilePath (Join-Path $env:SystemRoot "explorer.exe") `
            -ArgumentList "`"$ExecutablePath`""
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        $app = @(Get-AssistantProcesses) |
            Sort-Object -Property ProcessId -Unique |
            Select-Object -First 1
        if ($null -eq $app) {
            return $false
        }
        if (Test-AssistantHealthy -Process $app) {
            return $true
        }
        Start-Sleep -Seconds 1
    }

    Stop-AssistantProcesses
    return $false
}

$sessionActive = $false
$startCompleted = $false
$nextAttempt = [DateTime]::UtcNow

while ($true) {
    $codexPresent = @(Get-CodexProcesses).Count -gt 0
    if (-not $codexPresent) {
        if ($sessionActive) {
            Stop-AssistantProcesses
        }
        $sessionActive = $false
        $startCompleted = $false
        $nextAttempt = [DateTime]::UtcNow
        Start-Sleep -Seconds 2
        continue
    }

    if (-not $sessionActive) {
        $sessionActive = $true
        $startCompleted = $false
        $nextAttempt = [DateTime]::UtcNow.AddSeconds(10)
    }

    if (-not $startCompleted -and
        [DateTime]::UtcNow -ge $nextAttempt) {
        if (Start-AssistantForSession) {
            $startCompleted = $true
        }
        else {
            $nextAttempt = [DateTime]::UtcNow.AddSeconds(30)
        }
    }

    Start-Sleep -Seconds 2
}
'@
    [System.IO.File]::WriteAllText(
        $watcherPath,
        $watcherContent,
        (New-Object System.Text.UTF8Encoding($false)))

    $powershell = Join-Path `
        $env:SystemRoot `
        "System32\WindowsPowerShell\v1.0\powershell.exe"
    if ($UseStartupShortcut) {
        $startupDirectory = Join-Path `
            $env:APPDATA `
            "Microsoft\Windows\Start Menu\Programs\Startup"
        if (-not (Test-Path `
                -LiteralPath $startupDirectory `
                -PathType Container)) {
            New-Item `
                -ItemType Directory `
                -Force `
                -Path $startupDirectory | Out-Null
        }
        $shortcutPath = Join-Path `
            $startupDirectory `
            "CodexVoiceAssistant.lnk"
        $shell = New-Object -ComObject WScript.Shell
        try {
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $powershell
            $shortcut.Arguments =
                "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass " +
                "-File `"$watcherPath`" " +
                "-ExecutablePath `"$normalizedExecutable`""
            $shortcut.WorkingDirectory = $installRoot
            $shortcut.WindowStyle = 7
            $shortcut.Description =
                "Start Codex Voice Assistant when Codex starts."
            $shortcut.Save()
        }
        finally {
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject(
                $shell) | Out-Null
        }
        return
    }

    $userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $arguments = "-NoProfile -NonInteractive -WindowStyle Hidden " + "-ExecutionPolicy Bypass -File `"$watcherPath`" " + "-ExecutablePath `"$normalizedExecutable`""
    $action = New-ScheduledTaskAction `
        -Execute $powershell `
        -Argument $arguments `
        -WorkingDirectory $installRoot
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $trigger.Delay = $Delay
    $principal = New-ScheduledTaskPrincipal `
        -UserId $userId `
        -LogonType Interactive `
        -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -MultipleInstances IgnoreNew
    $settings.ExecutionTimeLimit = "PT0S"
    $settings.RestartCount = 999
    $settings.RestartInterval = "PT1M"

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Description "Start Codex Voice Assistant when the Codex desktop app starts." `
        -Force | Out-Null
}

function Remove-LogonScheduledTask {
    param(
        [string]$TaskName = "CodexVoiceAssistant"
    )

    $task = Get-ScheduledTask `
        -TaskName $TaskName `
        -ErrorAction SilentlyContinue
    if ($null -eq $task) {
        return $false
    }
    Unregister-ScheduledTask `
        -TaskName $TaskName `
        -Confirm:$false
    return $true
}

function Remove-LaunchWatcherShortcut {
    $startupDirectory = Join-Path `
        $env:APPDATA `
        "Microsoft\Windows\Start Menu\Programs\Startup"
    $shortcutPath = Join-Path `
        $startupDirectory `
        "CodexVoiceAssistant.lnk"
    if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) {
        return $false
    }

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        if ($shortcut.Arguments -notmatch
            [regex]::Escape("launch-when-codex.ps1")) {
            return $false
        }
        [System.IO.File]::Delete($shortcutPath)
        return $true
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject(
            $shell) | Out-Null
    }
}

function Get-FileSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            Exists = $false
            Bytes = $null
        }
    }

    return [pscustomobject]@{
        Exists = $true
        Bytes = [System.IO.File]::ReadAllBytes($Path)
    }
}

function Restore-FileSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Snapshot,

        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot
    )

    $normalizedPath = Assert-PathWithin `
        -Path $Path `
        -Root $AllowedRoot `
        -AllowRoot

    if ($Snapshot.Exists) {
        $parent = Split-Path -Path $normalizedPath -Parent
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        [System.IO.File]::WriteAllBytes(
            $normalizedPath,
            [byte[]]$Snapshot.Bytes)
    }
    elseif (Test-Path -LiteralPath $normalizedPath) {
        [System.IO.File]::Delete($normalizedPath)
    }
}

function Get-ModelManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $manifest = Read-JsonFile -Path $Path
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.product -ne $script:ProductName) {
        throw "Unsupported Whisper model manifest: $Path"
    }

    $models = @($manifest.models)
    if ($models.Count -eq 0) {
        throw "Whisper model manifest contains no models: $Path"
    }

    foreach ($model in $models) {
        $name = [string]$model.name
        if ([string]::IsNullOrWhiteSpace($name) -or
            $name -ne [System.IO.Path]::GetFileName($name) -or
            $name.IndexOfAny([char[]]@("\", "/")) -ge 0) {
            throw "Unsafe model name in manifest: '$name'."
        }

        $uri = $null
        if (-not [System.Uri]::TryCreate(
                [string]$model.url,
                [System.UriKind]::Absolute,
                [ref]$uri) -or
            $uri.Scheme -ne "https") {
            throw "Model '$name' must use an absolute HTTPS URL."
        }

        if ([long]$model.length -le 0) {
            throw "Model '$name' has an invalid length."
        }

        if (([string]$model.sha256) -notmatch "^[0-9a-fA-F]{64}$") {
            throw "Model '$name' has an invalid SHA-256 value."
        }

        $legacyPath = Assert-SafeRelativePath -Path ([string]$model.legacyPath)
        $model.legacyPath = $legacyPath
    }

    return $manifest
}

function Invoke-WhisperModelInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,

        [Parameter(Mandatory = $true)]
        [string]$ModelRoot,

        [Parameter(Mandatory = $true)]
        [string]$LegacyRoot
    )

    $manifest = Get-ModelManifest -Path $ManifestPath
    $normalizedModelRoot = Get-NormalizedFullPath -Path $ModelRoot
    if (-not (Test-Path -LiteralPath $normalizedModelRoot)) {
        New-Item `
            -ItemType Directory `
            -Force `
            -Path $normalizedModelRoot | Out-Null
    }

    foreach ($model in @($manifest.models)) {
        $name = [string]$model.name
        $target = Join-Path $normalizedModelRoot $name
        $length = [long]$model.length
        $sha256 = ([string]$model.sha256).ToLowerInvariant()

        if (Test-FileIntegrity `
                -Path $target `
                -Length $length `
                -Sha256 $sha256) {
            Write-Host "Verified model: $name" -ForegroundColor DarkGray
            continue
        }

        $partial = Join-Path `
            $normalizedModelRoot `
            ("{0}.partial.{1}" -f $name, [guid]::NewGuid().ToString("N"))
        try {
            $legacy = Join-Path `
                $LegacyRoot `
                ([string]$model.legacyPath).Replace("/", "\")
            if (Test-Path -LiteralPath $legacy -PathType Leaf) {
                if (Test-FileIntegrity `
                        -Path $legacy `
                        -Length $length `
                        -Sha256 $sha256) {
                    Write-Host "Importing verified model: $name"
                    Copy-Item `
                        -LiteralPath $legacy `
                        -Destination $partial `
                        -Force
                }
                else {
                    Write-Warning "Legacy model failed SHA-256 verification and will not be imported: $legacy"
                }
            }

            if (-not (Test-Path -LiteralPath $partial -PathType Leaf)) {
                Write-Host "Downloading model: $name"
                & curl.exe `
                    --location `
                    --fail `
                    --silent `
                    --show-error `
                    --retry 3 `
                    --retry-delay 2 `
                    --retry-connrefused `
                    --output $partial `
                    ([string]$model.url)
                if ($LASTEXITCODE -ne 0) {
                    throw "Download failed for '$name' with exit code $LASTEXITCODE."
                }
            }

            Assert-FileIntegrity `
                -Path $partial `
                -Length $length `
                -Sha256 $sha256

            if (Test-Path -LiteralPath $target -PathType Leaf) {
                $backup = "$target.replace-backup.$([guid]::NewGuid().ToString("N"))"
                try {
                    [System.IO.File]::Replace(
                        $partial,
                        $target,
                        $backup,
                        $true)
                }
                finally {
                    if (Test-Path -LiteralPath $backup) {
                        [System.IO.File]::Delete($backup)
                    }
                }
            }
            else {
                [System.IO.File]::Move($partial, $target)
            }

            Assert-FileIntegrity `
                -Path $target `
                -Length $length `
                -Sha256 $sha256
            Write-Host "Installed model: $name" -ForegroundColor Green
        }
        finally {
            if (Test-Path -LiteralPath $partial -PathType Leaf) {
                [System.IO.File]::Delete($partial)
            }
        }
    }

    $manifestCopy = Join-Path `
        (Split-Path -Path $normalizedModelRoot -Parent) `
        (Split-Path -Path $ManifestPath -Leaf)
    Copy-Item `
        -LiteralPath $ManifestPath `
        -Destination $manifestCopy `
        -Force

    return $manifest
}

function Install-PythonRuntime {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,

        [Parameter(Mandatory = $true)]
        [string]$PayloadRoot,

        [Parameter(Mandatory = $true)]
        [string]$DataRoot
    )

    $manifest = Read-JsonFile -Path $ManifestPath
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.product -ne $script:ProductName) {
        throw "Unsupported Kokoro runtime manifest: $ManifestPath"
    }

    $relativeArchive = Assert-SafeRelativePath `
        -Path ([string]$manifest.archive)
    $archivePath = Get-NormalizedFullPath -Path (
        Join-Path $PayloadRoot $relativeArchive.Replace("/", "\"))
    $archivePath = Assert-PathWithin `
        -Path $archivePath `
        -Root $PayloadRoot
    $length = [long]$manifest.length
    $sha256 = ([string]$manifest.sha256).ToLowerInvariant()
    Assert-FileIntegrity `
        -Path $archivePath `
        -Length $length `
        -Sha256 $sha256

    $targetName = "kokoro-runtime"
    $targetProperty = $manifest.PSObject.Properties["targetDirectory"]
    if ($null -ne $targetProperty -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$targetProperty.Value)) {
        $targetName = Assert-SafeRelativePath `
            -Path ([string]$targetProperty.Value)
    }
    if ($targetName.Contains("/")) {
        throw "Runtime targetDirectory must be one directory name."
    }

    $requiredFile = "Lib/site-packages/kokoro_onnx/__init__.py"
    $requiredProperty = $manifest.PSObject.Properties["requiredFile"]
    if ($null -ne $requiredProperty -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$requiredProperty.Value)) {
        $requiredFile = Assert-SafeRelativePath `
            -Path ([string]$requiredProperty.Value)
    }

    $normalizedDataRoot = Get-NormalizedFullPath -Path $DataRoot
    $target = Join-Path $normalizedDataRoot $targetName
    $marker = Join-Path $target ".installed.sha256"
    if ((Test-Path -LiteralPath $marker -PathType Leaf) -and
        [System.IO.File]::ReadAllText($marker).Trim().Equals(
            $sha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Verified Python runtime: $($manifest.version)" `
            -ForegroundColor DarkGray
        return
    }

    $staging = Join-Path `
        $normalizedDataRoot `
        (".kokoro-runtime-staging-{0}" -f
            [guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        Expand-Archive `
            -LiteralPath $archivePath `
            -DestinationPath $staging `
            -Force
        $requiresPython = $true
        $requiresPythonProperty =
            $manifest.PSObject.Properties["requiresPython"]
        if ($null -ne $requiresPythonProperty) {
            $requiresPython = [bool]$requiresPythonProperty.Value
        }
        if ($requiresPython -and -not (Test-Path `
                -LiteralPath (Join-Path $staging "python.exe") `
                -PathType Leaf)) {
            throw "Python runtime archive does not contain python.exe."
        }
        $requiredPath = Join-Path `
            $staging `
            $requiredFile.Replace("/", "\")
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Python runtime archive is incomplete: $requiredFile"
        }

        [System.IO.File]::WriteAllText(
            (Join-Path $staging ".installed.sha256"),
            $sha256,
            $script:Utf8NoBom)

        if (Test-FileSystemEntry -Path $target) {
            Assert-RealDirectory -Path $target
            Remove-DirectoryTreeSafely `
                -Path $target `
                -AllowedRoot $normalizedDataRoot
        }
        Move-Item `
            -LiteralPath $staging `
            -Destination $target
        Write-Host "Installed Python runtime: $($manifest.version)" `
            -ForegroundColor Green
    }
    finally {
        if (Test-FileSystemEntry -Path $staging) {
            Remove-DirectoryTreeSafely `
                -Path $staging `
                -AllowedRoot $normalizedDataRoot
        }
    }
}

Export-ModuleMember -Function @(
    "Assert-FileIntegrity",
    "Assert-PathWithin",
    "Assert-RealDirectory",
    "Assert-SafeRelativePath",
    "Copy-DirectoryContents",
    "Get-CodexVoiceAssistantProcesses",
    "Get-ExecutablePathFromCommandLine",
    "Get-FileSha256",
    "Get-FileSnapshot",
    "Get-ModelManifest",
    "Get-NormalizedFullPath",
    "Get-ReleaseFileRecords",
    "Get-ReleaseFingerprint",
    "Get-ReparsePointTarget",
    "Get-StartupRegistrationSnapshot",
    "Install-ReleaseDirectory",
    "Invoke-WhisperModelInstall",
    "Install-PythonRuntime",
    "New-DirectoryJunction",
    "New-ReleaseManifest",
    "Read-JsonFile",
    "Remove-DirectoryTreeSafely",
    "Remove-FileSafely",
    "Remove-LaunchWatcherShortcut",
    "Remove-StartupRegistrationSafely",
    "Remove-LogonScheduledTask",
    "Restore-CurrentRelease",
    "Restore-FileSnapshot",
    "Restore-StartupRegistrationSnapshot",
    "Set-CurrentRelease",
    "Set-StartupRegistration",
    "Set-LogonScheduledTask",
    "Stop-CodexVoiceAssistantProcess",
    "Test-FileIntegrity",
    "Test-FileSystemEntry",
    "Test-PathWithinOrEqual",
    "Test-ReleaseManifest",
    "Write-JsonFileAtomic"
)
