param([string]$Assembly = (Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\SemiconductorCsvAnalyzer.dll'))
$ErrorActionPreference = 'Stop'
$taskAssembly = [Security.SecurityElement]::Escape((Resolve-Path -LiteralPath $Assembly).Path)
$taskProbe = Join-Path $PSScriptRoot '..\bin\IniLayoutUiProbe'
New-Item -ItemType Directory -Path $taskProbe -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'IniLayoutUiProbe.cs.txt') -Destination (Join-Path $taskProbe 'Program.cs') -Force
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><Reference Include="SemiconductorCsvAnalyzer"><HintPath>$taskAssembly</HintPath></Reference><PackageReference Include="ClosedXML" Version="0.105.1"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $taskProbe 'Probe.csproj') -Encoding UTF8
& dotnet run --project (Join-Path $taskProbe 'Probe.csproj') -c Release -- (Join-Path $taskProbe 'results')
if ($LASTEXITCODE -ne 0) { throw 'INI layout UI checks failed.' }
