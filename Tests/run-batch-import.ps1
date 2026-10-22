param([string]$Assembly, [string]$Data, [switch]$Ui)
$ErrorActionPreference = 'Stop'
$taskAssembly = (Resolve-Path -LiteralPath $Assembly).Path
$taskData = [IO.Path]::GetFullPath($Data)
New-Item -ItemType Directory -Path $taskData -Force | Out-Null
$taskKind = if ($Ui) { 'BatchOpenWindowsProbe' } else { 'BatchImportBenchmark' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../bin/$taskKind"))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot "$taskKind.cs.txt") (Join-Path $taskRoot 'Program.cs') -Force
$taskXml = [Security.SecurityElement]::Escape($taskAssembly)
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Reference Include="SemiconductorCsvAnalyzer"><HintPath>$taskXml</HintPath></Reference><PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.32"/><PackageReference Include="ClosedXML" Version="0.105.1"/></ItemGroup></Project>
"@
[IO.File]::WriteAllText((Join-Path $taskRoot 'Probe.csproj'), $taskProject)
dotnet run --project (Join-Path $taskRoot 'Probe.csproj') -c Release -- $taskData 25 2000 120
if ($LASTEXITCODE -ne 0) { throw 'Batch import validation failed.' }
