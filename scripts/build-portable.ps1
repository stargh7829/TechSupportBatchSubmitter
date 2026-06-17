#requires -Version 7.0

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw '必须使用 PowerShell 7 或更高版本运行此脚本。'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localDotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$project = Join-Path $repoRoot 'src\TechSupportBatchSubmitter.Wpf\TechSupportBatchSubmitter.Wpf.csproj'
$solution = Join-Path $repoRoot 'TechSupportBatchSubmitter.sln'
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$publishDirectory = Join-Path $artifactsRoot 'portable\TechSupportBatchSubmitter'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = $projectXml.Project.PropertyGroup.Version |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw '项目文件缺少 Version。'
}

$zipPath = Join-Path $artifactsRoot "技术支持批量提交工具-$version-win-x64.zip"
$latestZipPath = Join-Path $artifactsRoot '技术支持批量提交工具-win-x64.zip'

if (-not $artifactsRoot.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录不在仓库内：$artifactsRoot"
}

$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or -not $sdkVersion.StartsWith('8.')) {
    throw "需要 .NET 8 SDK，当前版本：$sdkVersion"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& $dotnet test $solution -c Release
if ($LASTEXITCODE -ne 0) {
    throw '测试失败，已停止发布。'
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw '发布失败。'
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\便携版使用说明.txt') `
    -Destination (Join-Path $publishDirectory '使用说明.txt')

$webViewInstaller = Join-Path $publishDirectory 'MicrosoftEdgeWebview2Setup.exe'
Invoke-WebRequest `
    -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' `
    -OutFile $webViewInstaller

if ((Get-Item -LiteralPath $webViewInstaller).Length -lt 1MB) {
    throw 'WebView2 安装程序下载结果异常。'
}

foreach ($path in @($zipPath, $latestZipPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath
Copy-Item -LiteralPath $zipPath -Destination $latestZipPath

Write-Host "发布目录：$publishDirectory"
Write-Host "版本压缩包：$zipPath"
Write-Host "通用压缩包：$latestZipPath"
