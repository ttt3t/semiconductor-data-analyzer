param(
    [string]$Assembly = (Join-Path $PSScriptRoot '..\bin\MemoryUpdate\SemiconductorCsvAnalyzer.dll'),
    [string]$Config = 'C:\wpf_project\analyzer_config.ini',
    [string[]]$Files = @('C:\wpf_project\integrated.csv','C:\wpf_project\test_1000items_1000chips.csv','C:\wpf_project\integrated2.csv','C:\wpf_project\test_1000items_1000chips2.csv'),
    [int]$Cycles = 12,
    [int]$IdleMilliseconds = 1200,
    [int]$FinalIdleMilliseconds = 10000,
    [switch]$CollectEach,
    [switch]$Maps,
    [string]$RunName = 'baseline'
)
$ErrorActionPreference = 'Stop'
$taskAssembly = (Resolve-Path -LiteralPath $Assembly).Path
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\ReopenMemoryProbe'))
New-Item -ItemType Directory -Path $taskProjectRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReopenMemoryProbe.cs.txt') -Destination (Join-Path $taskProjectRoot 'Program.cs') -Force
$taskAssemblyXml = [Security.SecurityElement]::Escape($taskAssembly)
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit><ServerGarbageCollection>false</ServerGarbageCollection></PropertyGroup>
  <ItemGroup><Reference Include="SemiconductorCsvAnalyzer"><HintPath>$taskAssemblyXml</HintPath></Reference><PackageReference Include="ClosedXML" Version="0.105.1"/><PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.32"/></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $taskProjectRoot 'Probe.csproj'), $taskProject)
$env:DOTNET_CLI_HOME = Join-Path $taskProjectRoot 'CliHome'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$taskMode = if ($CollectEach) { 'collect' } else { 'natural' }
if ($Maps) { $taskMode += '-maps' }
$taskResults = Join-Path $taskProjectRoot ($RunName + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
& dotnet run --project (Join-Path $taskProjectRoot 'Probe.csproj') -c Release -p:UseSharedCompilation=false -p:RestoreSources="$env:USERPROFILE\.nuget\packages" -p:RestorePackagesPath="$env:USERPROFILE\.nuget\packages" -- $taskResults $Config $Cycles $IdleMilliseconds $taskMode $FinalIdleMilliseconds @Files
if ($LASTEXITCODE -ne 0) { throw 'Repeated-open memory probe failed.' }
