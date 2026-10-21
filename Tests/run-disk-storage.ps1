param([string]$Assembly = (Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\SemiconductorCsvAnalyzer.dll'), [string]$Exe = '')
$ErrorActionPreference = 'Stop'
$taskAssembly = (Resolve-Path -LiteralPath $Assembly).Path
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\DiskStorageWindowsProbe'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DiskStorageWindowsProbe.cs.txt') -Destination (Join-Path $taskRoot 'Program.cs') -Force
$taskAssemblyXml = [Security.SecurityElement]::Escape($taskAssembly)
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><Reference Include="SemiconductorCsvAnalyzer"><HintPath>$taskAssemblyXml</HintPath></Reference><PackageReference Include="ClosedXML" Version="0.105.1"/><PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.32"/></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $taskRoot 'Probe.csproj'), $taskProject)
$taskArgs = @((Join-Path $taskRoot 'results'))
if ($Exe) { $taskArgs += (Resolve-Path -LiteralPath $Exe).Path }
& dotnet build (Join-Path $taskRoot 'Probe.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Disk probe build failed.' }
# A referenced executable DLL needs its own host metadata when the probe starts it via dotnet.
$taskHost = Join-Path $taskRoot 'bin/Release/net8.0-windows'
foreach ($taskName in @('SemiconductorCsvAnalyzer.runtimeconfig.json', 'SemiconductorCsvAnalyzer.deps.json')) {
    Copy-Item -LiteralPath (Join-Path (Split-Path $taskAssembly) $taskName) -Destination $taskHost -Force
}
& dotnet run --project (Join-Path $taskRoot 'Probe.csproj') -c Release --no-build -- @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Disk storage Windows checks failed.' }
