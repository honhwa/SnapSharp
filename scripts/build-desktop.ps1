[CmdletBinding()]
param(
    [AllowEmptyString()]
    [string]$ExpectedVersion = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryRoot
try {
    [xml]$project = Get-Content -LiteralPath 'NimCap.Screenshot.csproj' -Raw
    $projectVersion = [string]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($projectVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'The project Version must use major.minor.patch format.'
    }
    if ($ExpectedVersion -and $ExpectedVersion -cne $projectVersion) {
        throw "Expected version $ExpectedVersion does not match project version $projectVersion."
    }

    # An isolated output directory prevents local models/settings or old binaries
    # from leaking into the portable distribution. No existing directory is deleted.
    $buildRoot = Join-Path $repositoryRoot ".artifacts/desktop-build/$([Guid]::NewGuid().ToString('N'))"
    $publishDirectory = Join-Path $buildRoot 'publish'
    $packageDirectory = Join-Path $buildRoot 'dist'
    New-Item -ItemType Directory -Path $publishDirectory, $packageDirectory -Force | Out-Null

    Invoke-DotNet @('restore', 'NimCap.Screenshot.slnx')
    Invoke-DotNet @('build', 'NimCap.Screenshot.slnx', '-c', 'Release', '--no-restore', '-m:1',
        '-p:UseSharedCompilation=false', "-bl:$buildRoot/build.binlog")
    Invoke-DotNet @('run', '--project', 'tools/UpdateChecks/UpdateChecks.csproj', '-c', 'Release')
    Invoke-DotNet @('run', '--project', 'tools/WindowChecks/WindowChecks.csproj', '-c', 'Release',
        '--no-build', '--', '--dpi-only')
    Invoke-DotNet @('publish', 'NimCap.Screenshot.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-o', $publishDirectory, '-m:1',
        '-p:UseSharedCompilation=false', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        "-bl:$buildRoot/publish.binlog")
    # Both entry points use the same pinned runtime and library dependencies.
    Invoke-DotNet @('publish', 'Cli/NimCap.Screenshot.Cli.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-o', $publishDirectory, '-m:1',
        "-p:NimCapCliVersion=$projectVersion", '-p:UseSharedCompilation=false',
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', "-bl:$buildRoot/cli-publish.binlog")
    Invoke-DotNet @('run', '--project', 'tools/CliChecks/CliChecks.csproj', '-c', 'Release', '--no-build',
        '--', '--cli', (Join-Path $publishDirectory 'nimcap.exe'))

    $required = @(
        'NimCap.Screenshot.exe', 'NimCap.Screenshot.dll', 'NimCap.Screenshot.deps.json',
        'NimCap.Screenshot.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll',
        'nimcap.exe', 'nimcap.dll', 'nimcap.deps.json', 'nimcap.runtimeconfig.json', 'CLI.md',
        'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md',
        'ThirdPartyLicenses/PACKAGE-NOTICES.md', 'ThirdPartyLicenses/DirectML-LICENSE.txt'
    )
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $name) -PathType Leaf)) {
            throw "The self-contained distribution is missing $name."
        }
    }
    $executable = Join-Path $publishDirectory 'NimCap.Screenshot.exe'
    $fileVersion = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
    if ($fileVersion -ne [Version]"$projectVersion.0") {
        throw "Executable version $fileVersion does not match $projectVersion."
    }
    $cliVersion = & (Join-Path $publishDirectory 'nimcap.exe') --version
    if ($LASTEXITCODE -ne 0 -or $cliVersion -cne $projectVersion) {
        throw "CLI version $cliVersion does not match project version $projectVersion."
    }
    $forbiddenExtensions = @('.onnx', '.gguf', '.safetensors', '.traineddata', '.pt', '.pth', '.pfx', '.p12', '.key')
    $forbiddenNames = @('settings.json', 'data-location.json', '.env', 'secrets.json')
    foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force -File) {
        if ($file.Extension -in $forbiddenExtensions -or $file.Name -in $forbiddenNames) {
            throw "Unexpected private data or model file in the distribution: $($file.Name)."
        }
    }
    foreach ($name in @('models', 'History', 'Diagnostics', 'downloads', '.git', '.codex', '.agents')) {
        if (Test-Path -LiteralPath (Join-Path $publishDirectory $name)) {
            throw "Unexpected local directory in the distribution: $name."
        }
    }

    $packageName = "nimcap-screenshot-v$projectVersion-win-x64.zip"
    $packagePath = Join-Path $packageDirectory $packageName
    # Archive children at ZIP root, exactly as expected by the in-app updater.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $packagePath,
        [IO.Compression.CompressionLevel]::Optimal, $false)
    $digest = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = Join-Path $packageDirectory 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($checksumPath, "$digest  $packageName`n", [Text.UTF8Encoding]::new($false))
    if ($env:GITHUB_OUTPUT) {
        @("version=$projectVersion", "package_path=$packagePath", "checksum_path=$checksumPath") |
            Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
    if ($env:GITHUB_STEP_SUMMARY) {
        @('## Windows x64 build', "Version: $projectVersion", "Package: $packageName", "SHA256: $digest",
            '', 'Checks: solution build, offline updater regression, mixed-DPI coordinate fixtures, published CLI contract and worker cleanup.',
            'Real capture, GPU models, microphone, and camera require separate hardware validation.') |
            Out-File -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
    }
    Write-Host "Package: $packagePath"
    Write-Host "Checksum: $checksumPath"
}
finally {
    Pop-Location
}
