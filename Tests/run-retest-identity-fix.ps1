param([string]$Assembly = (Join-Path $PSScriptRoot '../bin/Release/net8.0-windows/SemiconductorCsvAnalyzer.dll'))
$ErrorActionPreference = 'Stop'
$taskAssembly = [Security.SecurityElement]::Escape((Resolve-Path -LiteralPath $Assembly).Path)
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../bin/RetestIdentityFixProbe'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'RetestIdentityFixProbe.cs.txt') (Join-Path $taskRoot 'Program.cs') -Force
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Reference Include="SemiconductorCsvAnalyzer"><HintPath>$taskAssembly</HintPath></Reference><PackageReference Include="ClosedXML" Version="0.105.1"/></ItemGroup></Project>
"@
[IO.File]::WriteAllText((Join-Path $taskRoot 'Probe.csproj'), $taskProject)
& dotnet run --project (Join-Path $taskRoot 'Probe.csproj') -c Release -- (Join-Path $taskRoot 'results')
if ($LASTEXITCODE -ne 0) { throw 'Retest identity fixes failed' }
