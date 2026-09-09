Set-StrictMode -Version Latest

function Get-PublishSourceSnapshot([string]$RepositoryRoot) {
    $head = & git -C $RepositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot establish source commit identity.' }
    $paths = @(& git -C $RepositoryRoot -c core.quotepath=false ls-files --cached --others --exclude-standard -- src tests scripts publish.ps1 Directory.Build.props Directory.Build.targets Directory.Packages.props CodexModelManager.sln global.json NuGet.Config .editorconfig .gitattributes .config)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'Cannot enumerate publish source inputs.' }
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $files = [ordered]@{}
    $identity = [Text.StringBuilder]::new()
    foreach ($relative in $paths) {
        $path = Join-Path $RepositoryRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue } # Tracked source deletions are represented by absence.
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $files[$relative] = $hash
        [void]$identity.Append($relative).Append(' ').Append($hash).Append("`n")
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $contentHash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity.ToString()))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [pscustomobject][ordered]@{ SchemaVersion = 1; Commit = $head.Trim(); ContentSha256 = $contentHash; SourceIdentity = $head.Trim() + '.content-' + $contentHash; Files = $files }
}

function Assert-PublishPath([string]$ArtifactsRoot, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside workspace artifacts: $full"
    }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point is not a publish path: $current" }
        }
        $current = Split-Path -Parent $current
    }
    if (Test-Path -LiteralPath $full -PathType Container) {
        $links = @(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
        if ($links.Count -ne 0) { throw "Publish tree contains a reparse point: $full" }
    }
}

function Get-PublishFingerprints([string]$Directory) {
    $result = @{}
    foreach ($relative in @('CodexModelManager.exe', 'helpers\credential\CodexModelManager.CredentialHelper.exe', 'helpers\mcp\CodexModelManager.TestMcpServer.exe')) {
        $file = Join-Path $Directory $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Published EXE missing: $file" }
        $stream = [IO.File]::OpenRead($file)
        try {
            if ($stream.Length -lt 3 -or $stream.ReadByte() -ne 77 -or $stream.ReadByte() -ne 90) { throw "Invalid PE artifact: $file" }
        } finally { $stream.Dispose() }
        $result[$relative] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    return $result
}

function Publish-StagedArtifacts([string]$ArtifactsRoot, [string]$Stage, [string]$Destination, [scriptblock]$Checkpoint = {}) {
    Assert-PublishPath $ArtifactsRoot $Stage
    Assert-PublishPath $ArtifactsRoot $Destination
    $expected = Get-PublishFingerprints $Stage
    $rootBytes = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($ArtifactsRoot).ToUpperInvariant())
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $lockId = [BitConverter]::ToString($sha.ComputeHash($rootBytes)).Replace('-', '') } finally { $sha.Dispose() }
    $mutex = [Threading.Mutex]::new($false, "Local\CodexModelManager.Publish.$lockId")
    $acquired = $false
    $oldMoved = $false
    $newMoved = $false
    $backup = $Destination + '.previous-' + [guid]::NewGuid().ToString('N')
    Assert-PublishPath $ArtifactsRoot $backup
    try {
        try { $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds(15)) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Another publisher owns the promotion lock.' }
        Assert-PublishPath $ArtifactsRoot $Stage
        Assert-PublishPath $ArtifactsRoot $Destination
        $parent = Split-Path -Parent $Destination
        New-Item -ItemType Directory -Force $parent | Out-Null
        & $Checkpoint 'BeforePromotion'
        if (Test-Path -LiteralPath $Destination) {
            Move-Item -LiteralPath $Destination -Destination $backup -ErrorAction Stop
            $oldMoved = $true
        }
        & $Checkpoint 'OldRetained'
        Move-Item -LiteralPath $Stage -Destination $Destination -ErrorAction Stop
        $newMoved = $true
        & $Checkpoint 'NewPromoted'
        $actual = Get-PublishFingerprints $Destination
        foreach ($relative in $expected.Keys) {
            if ($actual[$relative] -ne $expected[$relative]) { throw "Published artifact changed: $relative" }
        }
    } catch {
        $primary = $_.Exception
        try {
            if ($newMoved) {
                Assert-PublishPath $ArtifactsRoot $Destination
                Assert-PublishPath $ArtifactsRoot $Stage
                if (Test-Path -LiteralPath $Stage) { throw "Cannot preserve rejected staging: $Stage" }
                Move-Item -LiteralPath $Destination -Destination $Stage -ErrorAction Stop
            }
            if ($oldMoved) {
                Assert-PublishPath $ArtifactsRoot $backup
                Assert-PublishPath $ArtifactsRoot $Destination
                if (Test-Path -LiteralPath $Destination) { throw "Cannot restore retained release: $Destination" }
                Move-Item -LiteralPath $backup -Destination $Destination -ErrorAction Stop
            }
        } catch {
            throw [AggregateException]::new("Publish failed and restoration failed; retained release: $backup", [Exception[]]@($primary, $_.Exception))
        }
        throw $primary
    } finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
    # Keep the previous complete release. It is recoverable even if cleanup or a
    # later publish is interrupted; publishing never recursively deletes it.
    if ($oldMoved) { Write-Host "Previous release retained: $backup" }
}
