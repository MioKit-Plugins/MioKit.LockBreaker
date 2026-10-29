[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageVersion,
    [string]$PackageId,
    [string]$Configuration = "Release",
    [string]$OutputPath,
    [string]$ProjectPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot "artifacts"
}

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $candidates = @(
        (Join-Path $PSScriptRoot "plugin\MioKit.LockBreaker.csproj")
    )
    $ProjectPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $ProjectPath) {
        $found = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "plugin") -Filter "*.csproj" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
        $ProjectPath = $found
    }
}

$manifestPath = Join-Path $PSScriptRoot "plugin\plugin.json"
$forbiddenFields = @("pluginVersion", "releaseState", "releaseDate")
$forbiddenDlls = @(
    "MioKit.Sdk.dll",
    "MioKit.SourceGenerate.dll",
    "MioKit.Webview2.dll",
    "MioKit.Runtime.dll",
    "MioKit.Framework.dll",
    "MioKit.Extensions.dll"
)

if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "PackageVersion 必须是有效的 NuGet SemVer，例如 1.2.0 或 1.2.0-beta.1。"
}
if ([string]::IsNullOrWhiteSpace($ProjectPath) -or -not (Test-Path -LiteralPath $ProjectPath)) {
    throw "未找到插件项目 (.csproj)。请用 -ProjectPath 指定。"
}
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "未找到 plugin.json: $manifestPath"
}

if ([string]::IsNullOrWhiteSpace($PackageId)) {
    $PackageId = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.metadataVersion -ne "1.0" -or [string]::IsNullOrWhiteSpace($manifest.id) -or
    [string]::IsNullOrWhiteSpace($manifest.name) -or [string]::IsNullOrWhiteSpace($manifest.assembly) -or
    [string]::IsNullOrWhiteSpace($manifest.minSdkVersion)) {
    throw "plugin.json 必须使用 metadataVersion 1.0，并包含 id、name、assembly 和 minSdkVersion。"
}
foreach ($field in $forbiddenFields) {
    if ($manifest.PSObject.Properties.Name -contains $field) {
        throw "plugin.json 禁止字段 '$field'；版本及正式/预发布状态只由 PackageVersion 决定。"
    }
}

$expectedAssembly = [IO.Path]::GetFileNameWithoutExtension($ProjectPath) + ".dll"
if ($manifest.assembly -cne $expectedAssembly) {
    Write-Warning "plugin.json assembly='$($manifest.assembly)' 与默认输出 DLL '$expectedAssembly' 不一致；打包检查将以 plugin.json 为准，请确认 csproj 的 AssemblyName。"
}

Write-Host "项目: $ProjectPath"
Write-Host "PackageId: $PackageId"
Write-Host "PackageVersion: $PackageVersion"
Write-Host "输出目录: $OutputPath"

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
& dotnet build $ProjectPath -c $Configuration -p:PackageId=$PackageId -p:PackageVersion=$PackageVersion --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败。" }
& dotnet pack $ProjectPath -c $Configuration --no-build -o $OutputPath -p:PackageId=$PackageId -p:PackageVersion=$PackageVersion --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet pack 失败。" }

$packagePath = Join-Path $OutputPath "$PackageId.$PackageVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath)) { throw "未找到打包结果: $packagePath" }

$inspectPath = Join-Path ([IO.Path]::GetTempPath()) ("miokit-pack-" + [guid]::NewGuid().ToString("N"))
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $inspectPath)

    if (-not (Test-Path -LiteralPath (Join-Path $inspectPath "plugin.json"))) {
        throw "nupkg 根目录缺少 plugin.json。"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $inspectPath $manifest.assembly))) {
        throw "nupkg 根目录缺少入口 DLL: $($manifest.assembly)"
    }
    if ($manifest.icon -and -not (Test-Path -LiteralPath (Join-Path $inspectPath (($manifest.icon -replace '/', '\'))))) {
        throw "nupkg 缺少 plugin.json 声明的资源: $($manifest.icon)"
    }
    foreach ($dll in $forbiddenDlls) {
        if (Get-ChildItem -LiteralPath $inspectPath -Recurse -File -Filter $dll -ErrorAction SilentlyContinue) {
            throw "nupkg 禁止携带宿主 DLL: $dll"
        }
    }

    $nuspec = Get-ChildItem -LiteralPath $inspectPath -Filter *.nuspec -File | Select-Object -First 1
    if (-not $nuspec) { throw "nupkg 缺少 nuspec。" }
    [xml]$nuspecXml = Get-Content -LiteralPath $nuspec.FullName -Raw -Encoding UTF8

    $packageIcon = ([string]$nuspecXml.package.metadata.icon).Replace('\', '/')
    $manifestIcon = ([string]$manifest.icon).Replace('\', '/')
    if ($manifestIcon -and $packageIcon -cne $manifestIcon) {
        throw "nuspec icon '$packageIcon' 与 plugin.json icon '$manifestIcon' 不一致。"
    }

    $description = [string]$nuspecXml.package.metadata.description
    try {
        $envelope = $description | ConvertFrom-Json
    }
    catch {
        throw "nuspec description 不是有效 JSON 信封。"
    }
    if ($envelope.schema -ne "miokit.plugin-package" -or $envelope.schemaVersion -ne 1 -or
        $envelope.plugin.id -ne $manifest.id -or $envelope.plugin.assembly -ne $manifest.assembly) {
        throw "nuspec description JSON 信封无效或与 plugin.json 不一致。"
    }

    Write-Host "包内容检查通过。"
    Get-ChildItem -LiteralPath $inspectPath -Recurse -File |
        ForEach-Object { $_.FullName.Substring($inspectPath.Length).TrimStart('\', '/') } |
        Sort-Object |
        ForEach-Object { Write-Host "  $_" }
}
finally {
    if (Test-Path -LiteralPath $inspectPath) {
        Remove-Item -LiteralPath $inspectPath -Recurse -Force
    }
}

Write-Host "打包完成: $packagePath"
