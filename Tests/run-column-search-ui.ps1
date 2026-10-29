param([string]$Assembly = (Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\SemiconductorCsvAnalyzer.dll'))
$ErrorActionPreference = 'Stop'
$taskAssembly = (Resolve-Path -LiteralPath $Assembly).Path
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\ColumnSearchUiProbe'))
New-Item -ItemType Directory -Path $taskProjectRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ColumnSearchUiProbe.cs.txt') -Destination (Join-Path $taskProjectRoot 'Program.cs') -Force
$taskAssemblyXml = [Security.SecurityElement]::Escape($taskAssembly)
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><Reference Include="SemiconductorCsvAnalyzer"><HintPath>$taskAssemblyXml</HintPath></Reference><PackageReference Include="ClosedXML" Version="0.105.1"/></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $taskProjectRoot 'Probe.csproj'), $taskProject)
& dotnet run --project (Join-Path $taskProjectRoot 'Probe.csproj') -c Release -- (Join-Path $taskProjectRoot 'results')
if ($LASTEXITCODE -ne 0) { throw 'Column search UI regression checks failed.' }
