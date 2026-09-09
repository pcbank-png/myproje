# Windows PowerShell 5.1 compatible. Both BAT files use this build pipeline.
[CmdletBinding()]
param([switch]$PublishOnly, [switch]$CheckOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$mainProject = Join-Path $root 'src\NSXVeriKurtarmaPro\NSXVeriKurtarmaPro.csproj'
$updaterProject = Join-Path $root 'src\NSXVeriKurtarmaPro\NSXVeriKurtarmaPro.Updater\NSXVeriKurtarmaPro.Updater.csproj'
$installer = Join-Path $root 'installer\NSXVeriKurtarmaPro.iss'
$publishDir = Join-Path $root 'PUBLISH_WIN_X64'
$outputDir = Join-Path $root 'GUNCELLEME'
$mainExeName = 'NSXVeriKurtarmaPro.exe'
$updaterExeName = 'NSXVeriKurtarmaPro.Updater.exe'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$lockStream = $null
$stage = $null
$previousPublish = $null
$publishPromoted = $false
$sourceBackups = @{}
$finalArtifacts = @()

function Assert-LocalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Proje klasoru disinda islem engellendi: $full"
    }
    # Never follow junctions/symlinks during move or cleanup.
    $cursor = $full
    while ($cursor.Length -ge $root.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Baglanti/junction uzerinde islem engellendi: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
        if (-not $cursor) { break }
    }
    return $full
}

function Read-Version([string]$Path) {
    [xml]$xml = [IO.File]::ReadAllText($Path)
    $node = $xml.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $node -or $node.InnerText -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version x.y.z biciminde olmali: $Path"
    }
    return [version]$node.InnerText
}

function Get-Sha256([string]$Path) {
    # Do not depend on PowerShell module discovery when launched by another shell.
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Invoke-Publish([string]$Project, [string]$Destination, [string]$Version) {
    $arguments = @(
        'publish', $Project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo', '-m:1',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:VlcWindowsX64Enabled=true', '-p:VlcWindowsX86Enabled=false', '-p:VlcWindowsArm64Enabled=false',
        '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false', '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version", '-o', $Destination
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish basarisiz ($LASTEXITCODE): $Project" }
}

function Assert-Exe([string]$Path, [string]$Version) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "EXE bulunamadi: $Path" }
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    if ([version]$actual -ne [version]"$Version.0") { throw "EXE surumu uyusmuyor: $Path ($actual)" }
    if ((Get-Item -LiteralPath $Path).Length -lt 10MB) { throw "Self-contained EXE beklenenden kucuk: $Path" }
}

function Assert-PayloadFile([string]$Relative) {
    if ($Relative -match '(^|[\\/])(App_Data|Backups?|Logs?|UpdateTemp|Sessions?|Recovered|\.git|obj|bin)([\\/]|$)' -or
        $Relative -match '(?i)\.(pdb|lib|db|sqlite3?|bak|nsx|log|dmp)$|-(wal|shm)$' -or
        $Relative -match '(^|[\\/])(license-state\.dat|language\.json|ui-settings\.json|pro-filter-settings\.json|quick-scan-scope\.txt|appsettings(\.[^\\/]+)?\.json)$') {
        throw "Kullanici verisi/gelistirme dosyasi pakete giremez: $Relative"
    }
}

function Set-ProjectVersion([string]$Path, [string]$Version) {
    $text = [IO.File]::ReadAllText($Path)
    foreach ($name in @('Version', 'AssemblyVersion', 'FileVersion', 'InformationalVersion')) {
        $value = $Version
        if ($name -in @('AssemblyVersion', 'FileVersion')) { $value += '.0' }
        $pattern = '<' + $name + '>[^<]*</' + $name + '>'
        if ($text -match $pattern) {
            $text = [regex]::Replace($text, $pattern, ('<' + $name + '>' + $value + '</' + $name + '>'))
        }
    }
    [IO.File]::WriteAllText($Path, $text, $utf8)
}

try {
    foreach ($path in @($mainProject, $updaterProject, $installer)) {
        $null = Assert-LocalPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Dosya bulunamadi: $path" }
    }
    $current = Read-Version $mainProject
    $updaterVersion = Read-Version $updaterProject
    if ($PublishOnly -and $current -ne $updaterVersion) { throw 'Ana program ve updater surumleri esit olmali.' }
    $next = $current.ToString(3)
    if (-not $PublishOnly) {
        $highest = $current
        if ($updaterVersion -gt $highest) { $highest = $updaterVersion }
        $next = '{0}.{1}.{2}' -f $highest.Major, $highest.Minor, ($highest.Build + 1)
    }
    foreach ($part in ($next -split '\.')) {
        if ([int]$part -gt 65534) { throw 'Assembly surum siniri asildi.' }
    }
    Write-Host "NSX Veri Kurtarma Pro: $current -> $next"
    if ($PublishOnly) { Write-Host 'Mevcut surum publish edilir; surum artirilmaz.' }
    if ($CheckOnly) { Write-Host 'KONTROL OK - Dosya degistirilmedi.'; exit 0 }
    $null = Get-Command dotnet -ErrorAction Stop
    $null = Assert-LocalPath $publishDir
    $null = Assert-LocalPath $outputDir
    # Exclusive lock shared by both BATs, preventing simultaneous version changes.
    $lockPath = Assert-LocalPath (Join-Path $root '.nsx-package.lock')
    $lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if ((Read-Version $mainProject) -ne $current -or (Read-Version $updaterProject) -ne $updaterVersion) {
        throw 'Surum baska bir islemde degisti. BAT dosyasini yeniden calistirin.'
    }
    $stage = Assert-LocalPath (Join-Path $root ('.nsx-package-' + [guid]::NewGuid().ToString('N')))
    $null = New-Item -ItemType Directory -Path $stage
    $mainStage = Join-Path $stage 'payload'
    $updaterStage = Join-Path $stage 'updater'
    Write-Host '[1/4] Ana program publish ediliyor...'
    Invoke-Publish $mainProject $mainStage $next
    Write-Host '[2/4] Updater publish ediliyor...'
    Invoke-Publish $updaterProject $updaterStage $next
    Assert-Exe (Join-Path $mainStage $mainExeName) $next
    Assert-Exe (Join-Path $updaterStage $updaterExeName) $next
    # Native self-extract is enabled for BOTH executables; retain emitted sidecars too.
    foreach ($file in Get-ChildItem -LiteralPath $updaterStage -File -Recurse) {
        $relative = $file.FullName.Substring($updaterStage.Length + 1)
        Assert-PayloadFile $relative
        $target = Join-Path $mainStage $relative
        if (Test-Path -LiteralPath $target) { throw "Publish dosya cakismasi: $relative" }
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    foreach ($required in @('Languages\tr-TR.json', 'Languages\en-US.json', 'libvlc\win-x64\libvlc.dll', 'libvlc\win-x64\libvlccore.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $mainStage $required))) { throw "Gerekli calisma dosyasi eksik: $required" }
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $mainStage 'libvlc\win-x64\plugins') -File -Recurse).Count -eq 0) {
        throw 'Video oynatici eklentileri eksik.'
    }
    foreach ($sourceLanguage in Get-ChildItem -LiteralPath (Join-Path $root 'src\NSXVeriKurtarmaPro\Languages') -File -Recurse) {
        $relative = $sourceLanguage.FullName.Substring((Join-Path $root 'src\NSXVeriKurtarmaPro').Length + 1)
        $copy = Join-Path $mainStage $relative
        if (-not (Test-Path -LiteralPath $copy) -or (Get-Sha256 $sourceLanguage.FullName) -ne (Get-Sha256 $copy)) {
            throw "Dil paketi eksik/eski: $relative"
        }
    }
    $payloadFiles = @(Get-ChildItem -LiteralPath $mainStage -File -Recurse)
    foreach ($file in $payloadFiles) { Assert-PayloadFile $file.FullName.Substring($mainStage.Length + 1) }
    Write-Host '[3/4] Paket icerigi ve surumleri denetlendi.'

    if (-not $PublishOnly) {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zipName = "NSXVeriKurtarmaPro_Update_$next.zip"
        $zipPath = Join-Path $outputDir $zipName
        $hashPath = $zipPath + '.sha256'
        $infoPath = Join-Path $outputDir "NSXVeriKurtarmaPro_Update_$next.json"
        foreach ($artifact in @($zipPath, $hashPath, $infoPath)) {
            if (Test-Path -LiteralPath $artifact) { throw "Mevcut paket korunuyor; dosya zaten var: $artifact" }
        }
        $tempZip = Join-Path $stage $zipName
        $archive = [IO.Compression.ZipFile]::Open($tempZip, [IO.Compression.ZipArchiveMode]::Create)
        $expected = @{}
        try {
            foreach ($file in $payloadFiles) {
                $relative = $file.FullName.Substring($mainStage.Length + 1).Replace('\', '/')
                # Installed updater executes in-place and explicitly skips itself.
                if ($relative -ieq $updaterExeName) { continue }
                $expected[$relative] = Get-Sha256 $file.FullName
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $archive.Dispose() }
        $archive = [IO.Compression.ZipFile]::OpenRead($tempZip)
        try {
            if ($archive.Entries.Count -ne $expected.Count -or $null -eq $archive.GetEntry($mainExeName) -or $null -ne $archive.GetEntry($updaterExeName)) {
                throw 'ZIP dosya sayisi/ana EXE/updater yerlesimi hatali.'
            }
            foreach ($entry in $archive.Entries) {
                $stream = $entry.Open()
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                finally { $stream.Dispose(); $sha.Dispose() }
                if ($hash -cne $expected[$entry.FullName]) { throw "ZIP butunluk hatasi: $($entry.FullName)" }
            }
        }
        finally { $archive.Dispose() }
        $zipHash = Get-Sha256 $tempZip
        $metadata = [ordered]@{
            productCode = 'NSXDATARECOVERYPRO'; version = $next; fileVersion = "$next.0"
            architecture = 'win-x64'; selfContained = $true; fileName = $zipName
            sha256 = $zipHash; sizeBytes = (Get-Item -LiteralPath $tempZip).Length
            fileCount = $expected.Count; updaterIncluded = $false; createdUtc = [DateTime]::UtcNow.ToString('o')
        }
        $null = New-Item -ItemType Directory -Path $outputDir -Force
        foreach ($path in @($mainProject, $updaterProject, $installer)) { $sourceBackups[$path] = [IO.File]::ReadAllBytes($path) }
        Set-ProjectVersion $mainProject $next
        Set-ProjectVersion $updaterProject $next
        $iss = [IO.File]::ReadAllText($installer)
        $iss = [regex]::Replace($iss, '(?m)^#define MyAppVersion "[^"]+"', ('#define MyAppVersion "' + $next + '"'))
        [IO.File]::WriteAllText($installer, $iss, $utf8)
        Move-Item -LiteralPath $tempZip -Destination $zipPath
        $finalArtifacts += $zipPath
        $finalArtifacts += $hashPath
        [IO.File]::WriteAllText($hashPath, "$zipHash  $zipName" + [Environment]::NewLine, $utf8)
        $finalArtifacts += $infoPath
        [IO.File]::WriteAllText($infoPath, ($metadata | ConvertTo-Json), $utf8)
    }
    # Promote only the complete, verified build. Previous publish is recoverable.
    if (Test-Path -LiteralPath $publishDir) {
        $previousPublish = Assert-LocalPath (Join-Path $root ('PUBLISH_WIN_X64_ONCEKI_' + [DateTime]::Now.ToString('yyyyMMdd_HHmmss_fff')))
        Move-Item -LiteralPath $publishDir -Destination $previousPublish
    }
    Move-Item -LiteralPath $mainStage -Destination $publishDir
    $publishPromoted = $true
    Write-Host "[4/4] TAMAMLANDI - v$next"
    Write-Host "Publish: $publishDir"
    if ($previousPublish) { Write-Host "Onceki publish korundu: $previousPublish" }
    if (-not $PublishOnly) {
        Write-Host "Guncelleme ZIP: $zipPath"
        Write-Host "SHA256: $zipHash"
        Write-Host 'Sonraki calistirmada surum tekrar bir artirilir.'
    }
}
catch {
    [Console]::Error.WriteLine("HATA: " + $_.Exception.Message)
    foreach ($path in $sourceBackups.Keys) { [IO.File]::WriteAllBytes($path, $sourceBackups[$path]) }
    foreach ($artifact in $finalArtifacts) {
        $null = Assert-LocalPath $artifact
        if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force }
    }
    if ($previousPublish -and -not $publishPromoted -and -not (Test-Path -LiteralPath $publishDir)) {
        $null = Assert-LocalPath $previousPublish
        Move-Item -LiteralPath $previousPublish -Destination $publishDir
    }
    exit 1
}
finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $null = Assert-LocalPath $stage
        # Remove only this run's GUID staging folder, never a previous publish.
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Continue
    }
    if ($lockStream) { $lockStream.Dispose() }
}
