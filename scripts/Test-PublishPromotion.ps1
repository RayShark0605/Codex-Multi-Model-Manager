[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PublishPromotion.ps1')
$workspace = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $workspace 'artifacts'
$fixture = Join-Path $artifactRoot ('publish-audit-' + [guid]::NewGuid().ToString('N'))
Assert-PublishPath $artifactRoot $fixture
New-Item -ItemType Directory $fixture | Out-Null
$passed = [Collections.Generic.List[string]]::new()

function New-FakeRelease([string]$Directory, [string]$Value) {
    foreach ($relative in @('CodexModelManager.exe', 'helpers\credential\CodexModelManager.CredentialHelper.exe', 'helpers\mcp\CodexModelManager.TestMcpServer.exe')) {
        $file = Join-Path $Directory $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $file) | Out-Null
        [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes("MZ$Value"))
    }
}
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }

try {
    foreach ($checkpoint in @('BeforePromotion', 'OldRetained', 'NewPromoted', 'PostPromotionHashMismatch', 'Success')) {
        $case = Join-Path $fixture $checkpoint
        $stage = Join-Path $case 'stage'
        $destination = Join-Path $case 'release'
        New-FakeRelease $destination 'old'
        New-FakeRelease $stage 'new'
        $before = (Get-PublishFingerprints $destination)['CodexModelManager.exe']
        $failed = $false
        try {
            $faultAt = $checkpoint
            $inject = {
                param($phase)
                if ($phase -eq $faultAt) { throw 'Injected promotion failure' }
                if ($faultAt -eq 'PostPromotionHashMismatch' -and $phase -eq 'NewPromoted') { [IO.File]::AppendAllText((Join-Path $destination 'CodexModelManager.exe'), 'corruption') }
            }.GetNewClosure()
            Publish-StagedArtifacts $fixture $stage $destination $inject
        } catch { $failed = $true }
        if ($checkpoint -eq 'Success') {
            Assert-True (-not $failed) 'Promotion should succeed.'
            Assert-True (((Get-PublishFingerprints $destination)['CodexModelManager.exe']) -ne $before) 'New release was not published.'
            $retained = @(Get-ChildItem -LiteralPath $case -Directory -Filter 'release.previous-*')
            Assert-True ($retained.Count -eq 1) 'Previous release not retained.'
            Assert-True (((Get-PublishFingerprints $retained[0].FullName)['CodexModelManager.exe']) -eq $before) 'Retained release changed.'
        } else {
            Assert-True $failed "Fault at $checkpoint did not fail."
            Assert-True (((Get-PublishFingerprints $destination)['CodexModelManager.exe']) -eq $before) "Old release lost at $checkpoint."
        }
        $passed.Add($checkpoint)
    }
    $invalidStage = Join-Path $fixture 'invalid-stage'
    $protectedRelease = Join-Path $fixture 'protected-release'
    New-FakeRelease $protectedRelease 'protected'
    New-Item -ItemType Directory $invalidStage | Out-Null
    $failed = $false
    try { Publish-StagedArtifacts $fixture $invalidStage $protectedRelease } catch { $failed = $true }
    Assert-True $failed 'Incomplete candidate was accepted.'
    $null = Get-PublishFingerprints $protectedRelease
    $passed.Add('IncompleteCandidate')

    $rollbackStage = Join-Path $fixture 'rollback-failure\stage'
    $rollbackDestination = Join-Path $fixture 'rollback-failure\release'
    New-FakeRelease $rollbackStage 'candidate'
    New-FakeRelease $rollbackDestination 'prior'
    $oldHash = (Get-PublishFingerprints $rollbackDestination)['CodexModelManager.exe']
    $failed = $false
    $inject = {
        param($phase)
        if ($phase -eq 'OldRetained') {
            [IO.Directory]::CreateDirectory($rollbackDestination) | Out-Null
            throw 'Injected promotion and rollback obstruction'
        }
    }.GetNewClosure()
    try { Publish-StagedArtifacts $fixture $rollbackStage $rollbackDestination $inject } catch {
        $failed = $true
        Assert-True ($_.Exception -is [AggregateException]) 'Rollback failure did not aggregate the original failure.'
        Assert-True ($_.Exception.InnerExceptions.Count -eq 2) 'Both failures must remain available.'
    }
    Assert-True $failed 'Rollback obstruction did not fail.'
    $retained = @(Get-ChildItem -LiteralPath (Split-Path -Parent $rollbackDestination) -Directory -Filter 'release.previous-*')
    Assert-True ($retained.Count -eq 1) 'Rollback failed and previous release was lost.'
    Assert-True (((Get-PublishFingerprints $retained[0].FullName)['CodexModelManager.exe']) -eq $oldHash) 'Rollback failure changed previous release.'
    $null = Get-PublishFingerprints $rollbackStage
    $passed.Add('RollbackFailureKeepsEvidence')

    $failed = $false
    try { Assert-PublishPath $fixture (Join-Path $fixture '..\outside') } catch { $failed = $true }
    Assert-True $failed 'Path escape accepted.'
    $passed.Add('PathEscape')

    $link = Join-Path $fixture 'junction'
    New-Item -ItemType Junction -Path $link -Target $protectedRelease | Out-Null
    try {
        $failed = $false
        try { Assert-PublishPath $fixture (Join-Path $link 'child') } catch { $failed = $true }
        Assert-True $failed 'Junction path accepted.'
        $passed.Add('ReparsePoint')
    } finally { [IO.Directory]::Delete($link) } # Delete only the junction, never traverse its target (also on Windows PowerShell 5.1).

    $snapshot = Get-PublishSourceSnapshot $workspace
    foreach ($inputPath in @('NuGet.Config', 'Directory.Packages.props', 'publish.ps1', 'scripts/PublishPromotion.ps1')) {
        Assert-True ($snapshot.Files.Contains($inputPath)) "Source manifest omitted build input: $inputPath"
    }
    $passed.Add('SourceBuildInputsIncluded')

    $names = @('DOTNET_CLI_HOME', 'APPDATA', 'LOCALAPPDATA', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_NOLOGO', 'CMM_RUN_LIVE_LM', 'CMM_RUN_LIVE_LM_MUTATION', 'CMM_RUN_LIVE_CODEX', 'CMM_LIVE_GGUF_PATH', 'CMM_LIVE_GGUF_TEMPLATE_SHA')
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    function dotnet { $global:LASTEXITCODE = 1 }
    try {
        $failed = $false
        try { & (Join-Path $workspace 'publish.ps1') -SkipTests } catch { $failed = $true; Assert-True ($_.Exception.Message -eq 'dotnet restore failed.') 'Unexpected failure before mocked restore.' }
        Assert-True $failed 'Injected restore failure did not fail.'
        foreach ($name in $names) { Assert-True ([Environment]::GetEnvironmentVariable($name, 'Process') -eq $saved[$name]) "Environment leaked: $name" }
        $passed.Add('EnvironmentRestoredOnFailure')
    } finally { Remove-Item Function:dotnet }
    [pscustomobject]@{ Passed = $passed.Count; Tests = $passed.ToArray(); Failed = 0 } | ConvertTo-Json
} finally {
    Assert-PublishPath $artifactRoot $fixture
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
