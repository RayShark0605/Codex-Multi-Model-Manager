[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'publish\win-x64'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot ('.publish-staging-' + [guid]::NewGuid().ToString('N'))))
. (Join-Path $root 'scripts\PublishPromotion.ps1')

function Assert-WorkspaceArtifactPath([string]$Path) {
    Assert-PublishPath $artifactsRoot $Path
}

Assert-WorkspaceArtifactPath $publishRoot
Assert-WorkspaceArtifactPath $stagingRoot

$environmentNames = @('DOTNET_CLI_HOME', 'APPDATA', 'LOCALAPPDATA', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_NOLOGO', 'CMM_RUN_LIVE_LM', 'CMM_RUN_LIVE_LM_MUTATION', 'CMM_RUN_LIVE_CODEX', 'CMM_LIVE_GGUF_PATH', 'CMM_LIVE_GGUF_TEMPLATE_SHA')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:APPDATA = Join-Path $root '.appdata'
$env:LOCALAPPDATA = Join-Path $root '.localappdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
Remove-Item Env:CMM_RUN_LIVE_LM -ErrorAction SilentlyContinue
Remove-Item Env:CMM_RUN_LIVE_LM_MUTATION -ErrorAction SilentlyContinue
Remove-Item Env:CMM_RUN_LIVE_CODEX -ErrorAction SilentlyContinue
Remove-Item Env:CMM_LIVE_GGUF_PATH -ErrorAction SilentlyContinue
Remove-Item Env:CMM_LIVE_GGUF_TEMPLATE_SHA -ErrorAction SilentlyContinue

$sourceSnapshot = Get-PublishSourceSnapshot $root
$publishSucceeded = $false
$commonPublish = @(
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:SourceRevisionId=$($sourceSnapshot.SourceIdentity)"
)

try {
    dotnet restore (Join-Path $root 'CodexModelManager.sln') --runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    dotnet build (Join-Path $root 'CodexModelManager.sln') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    if (-not $SkipTests) {
        dotnet test (Join-Path $root 'tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj') --configuration Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Core unit tests failed.' }
        dotnet test (Join-Path $root 'tests\CodexModelManager.App.Tests\CodexModelManager.App.Tests.csproj') --configuration Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'App unit tests failed.' }
    }

    New-Item -ItemType Directory -Force $stagingRoot | Out-Null
    $appStage = Join-Path $stagingRoot 'app'
    $credentialStage = Join-Path $stagingRoot 'credential'
    $mcpStage = Join-Path $stagingRoot 'mcp'

    dotnet publish (Join-Path $root 'src\CodexModelManager.App\CodexModelManager.App.csproj') @commonPublish --output $appStage
    if ($LASTEXITCODE -ne 0) { throw 'Main application publish failed.' }
    dotnet publish (Join-Path $root 'src\CodexModelManager.CredentialHelper\CodexModelManager.CredentialHelper.csproj') @commonPublish --output $credentialStage
    if ($LASTEXITCODE -ne 0) { throw 'Credential Helper publish failed.' }
    dotnet publish (Join-Path $root 'src\CodexModelManager.TestMcpServer\CodexModelManager.TestMcpServer.csproj') @commonPublish --output $mcpStage
    if ($LASTEXITCODE -ne 0) { throw 'MCP Helper publish failed.' }

    $credentialDestination = Join-Path $appStage 'helpers\credential'
    $mcpDestination = Join-Path $appStage 'helpers\mcp'
    New-Item -ItemType Directory -Force $credentialDestination, $mcpDestination | Out-Null
    Copy-Item -LiteralPath (Join-Path $credentialStage 'CodexModelManager.CredentialHelper.exe') -Destination $credentialDestination
    Copy-Item -LiteralPath (Join-Path $mcpStage 'CodexModelManager.TestMcpServer.exe') -Destination $mcpDestination

    $currentSource = Get-PublishSourceSnapshot $root
    if ($currentSource.SourceIdentity -ne $sourceSnapshot.SourceIdentity) { throw 'Source inputs changed during publishing; staging was not promoted.' }
    $manifest = [ordered]@{ Source = $sourceSnapshot; PublishedUtc = [DateTime]::UtcNow.ToString('o'); Executables = Get-PublishFingerprints $appStage }
    [IO.File]::WriteAllText((Join-Path $appStage 'source-manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Publish-StagedArtifacts $artifactsRoot $appStage $publishRoot

    $mainExe = Join-Path $publishRoot 'CodexModelManager.exe'
    if (-not (Test-Path -LiteralPath $mainExe)) { throw "Published EXE missing: $mainExe" }
    $publishSucceeded = $true
    Write-Host "Published: $mainExe" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        if ($publishSucceeded) {
            try {
                Assert-WorkspaceArtifactPath $stagingRoot
                Remove-Item -LiteralPath $stagingRoot -Recurse -Force
            } catch {
                try { Write-Warning "Published successfully; staging cleanup failed, retained at $stagingRoot" -WarningAction Continue } catch { }
            }
        } else {
            # Do not erase rejected candidates/manifests or replace the original failure.
            try { Write-Warning "Publish failed; staging evidence retained at $stagingRoot" -WarningAction Continue } catch { }
        }
    }
}
}
finally {
    foreach ($name in $environmentNames) {
        if ($null -eq $savedEnvironment[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    }
}
