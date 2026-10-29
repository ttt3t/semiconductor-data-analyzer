#!/usr/bin/env bash
# Pure pixel-layer checks and timings; WPF interaction/copy probes still run on Windows.
set -euo pipefail
task_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
task_probe="$task_root/bin/ScatterRasterProbe"
mkdir -p "$task_probe"
cp "$task_root/Tests/ScatterRasterProbe.cs.txt" "$task_probe/Program.cs"
cat > "$task_probe/Probe.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../../Controls/ScatterRaster.cs" />
    <Compile Include="../../Models/*.cs" Exclude="../../Models/WaferDie.cs;../../Models/WaferMapPage.cs" />
    <Compile Include="../../Services/*.cs" Exclude="../../Services/HeatmapScale.cs;../../Services/IdleMemoryReclaimer.cs;../../Services/XlsxReportService.cs;../../Services/WindowLayoutPersistence.cs;../../Services/ManagementReportService.cs;../../Services/LayoutSettings.cs;../../Services/ManagementTrendService.cs;../../Services/MarkdownDocumentService.cs;../../Services/BinPalette.cs;../../Services/MapDataService.cs" />
    <PackageReference Include="ClosedXML" Version="0.105.1" />
  </ItemGroup>
</Project>
PROJECT
dotnet run --project "$task_probe/Probe.csproj" -c Release -- "$task_probe/results"
