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

function Assert-WorkspaceArtifactPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $artifactsRoot.TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside workspace artifacts: $full"
    }
}

Assert-WorkspaceArtifactPath $publishRoot
Assert-WorkspaceArtifactPath $stagingRoot

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:APPDATA = Join-Path $root '.appdata'
$env:LOCALAPPDATA = Join-Path $root '.localappdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
Remove-Item Env:CMM_RUN_LIVE_LM -ErrorAction SilentlyContinue
Remove-Item Env:CMM_RUN_LIVE_CODEX -ErrorAction SilentlyContinue

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
    '-p:DebugSymbols=false'
)

try {
    dotnet restore (Join-Path $root 'CodexModelManager.sln') --runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    dotnet build (Join-Path $root 'CodexModelManager.sln') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    if (-not $SkipTests) {
        dotnet test (Join-Path $root 'tests\CodexModelManager.Tests\CodexModelManager.Tests.csproj') --configuration Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
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

    if (Test-Path -LiteralPath $publishRoot) {
        Assert-WorkspaceArtifactPath $publishRoot
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $publishRoot) | Out-Null
    Move-Item -LiteralPath $appStage -Destination $publishRoot

    $mainExe = Join-Path $publishRoot 'CodexModelManager.exe'
    if (-not (Test-Path -LiteralPath $mainExe)) { throw "Published EXE missing: $mainExe" }
    Write-Host "Published: $mainExe" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-WorkspaceArtifactPath $stagingRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
