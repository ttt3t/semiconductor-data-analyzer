param([string]$Assembly, [switch]$HistogramOnly)
$ErrorActionPreference = 'Stop'
$taskAssembly = (Resolve-Path -LiteralPath $Assembly).Path
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../bin/ChartExportProbe'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'ChartExportProbe.cs.txt') (Join-Path $taskRoot 'Program.cs') -Force
$taskXml = [Security.SecurityElement]::Escape($taskAssembly)
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Reference Include="SemiconductorCsvAnalyzer"><HintPath>$taskXml</HintPath></Reference><PackageReference Include="ClosedXML" Version="0.105.1"/></ItemGroup></Project>
"@
[IO.File]::WriteAllText((Join-Path $taskRoot 'Probe.csproj'), $taskProject)
$taskArguments = @((Join-Path $taskRoot 'results'))
if ($HistogramOnly) { $taskArguments += '--histogram' }
dotnet run --project (Join-Path $taskRoot 'Probe.csproj') -c Release -- @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Chart export regression checks failed.' }
