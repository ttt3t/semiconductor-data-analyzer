param([string]$Assembly = (Join-Path $PSScriptRoot '..\bin\FeatureUpdate\SemiconductorCsvAnalyzer.dll'))
$ErrorActionPreference = 'Stop'
$taskAssembly = (Resolve-Path -LiteralPath $Assembly).Path
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\AnalyzerFeaturesProbe'))
New-Item -ItemType Directory -Path $taskProjectRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AnalyzerFeaturesProbe.cs.txt') -Destination (Join-Path $taskProjectRoot 'Program.cs') -Force
$taskAssemblyXml = [Security.SecurityElement]::Escape($taskAssembly)
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup><Reference Include="SemiconductorCsvAnalyzer"><HintPath>$taskAssemblyXml</HintPath></Reference><PackageReference Include="ClosedXML" Version="0.105.1"/><PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.32"/></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $taskProjectRoot 'Probe.csproj'), $taskProject)
$env:DOTNET_CLI_HOME = Join-Path $taskProjectRoot 'CliHome'
$env:DOTNET_NOLOGO = '1'
& dotnet run --project (Join-Path $taskProjectRoot 'Probe.csproj') -c Release -p:UseSharedCompilation=false -p:RestoreSources="$env:USERPROFILE\.nuget\packages" -p:RestorePackagesPath="$env:USERPROFILE\.nuget\packages" -- (Join-Path $taskProjectRoot 'results')
if ($LASTEXITCODE -ne 0) { throw 'Analyzer feature regression checks failed.' }
