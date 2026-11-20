param([string]$OfflineTools = 'D:\Develop\AIEnter\.tools')

$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
$dotnet = Join-Path $OfflineTools 'dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools\packages'
$project = Join-Path $workspace 'src\AIFile.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$output = Join-Path $workspace "dist\$version"
$installedExe = Join-Path $output 'AIFile.exe'
if (Get-Process AIFile -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }) {
    throw "Close the running build before replacing $installedExe."
}
$source = Join-Path $OfflineTools 'feed'
$arguments = @('publish', $project, '-c', 'Release', '-p:Platform=x64', '-o', $output)
if (Test-Path -LiteralPath $source) {
    $arguments += @('--source', $source, '-p:NuGetAudit=false')
    $win2d = Join-Path $OfflineTools 'packages\microsoft.graphics.win2d\1.4.0'
    if (Test-Path -LiteralPath (Join-Path $win2d 'microsoft.graphics.win2d.1.4.0.nupkg')) {
        $arguments += @('--source', $win2d)
    }
}

& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'AIFile build failed.' }
foreach ($file in 'AIFile.exe', 'resources.pri', 'MainWindow.xbf') {
    if (!(Test-Path -LiteralPath (Join-Path $output $file))) { throw "Missing publish output: $file" }
}
