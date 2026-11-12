#!/usr/bin/env bash
set -euo pipefail
task_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
task_probe="$task_root/bin/UnitMergeProbe"
mkdir -p "$task_probe"
cp "$task_root/Tests/UnitMergeProbe.cs.txt" "$task_probe/Program.cs"
cat > "$task_probe/UnitMergeProbe.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup>
    <Compile Include="../../Models/*.cs" Exclude="../../Models/WaferDie.cs;../../Models/WaferMapPage.cs" />
    <Compile Include="../../Services/*.cs" Exclude="../../Services/HeatmapScale.cs;../../Services/IdleMemoryReclaimer.cs;../../Services/XlsxReportService.cs;../../Services/WindowLayoutPersistence.cs;../../Services/ManagementReportService.cs;../../Services/LayoutSettings.cs;../../Services/ManagementTrendService.cs;../../Services/MarkdownDocumentService.cs;../../Services/BinPalette.cs;../../Services/MapDataService.cs;../../Services/CumulativeMapService.cs;../../Services/ManagementAnalytics.cs;../../Services/TriStateGridSorting.cs;../../Services/IndependentWindow.cs" />
    <PackageReference Include="ClosedXML" Version="0.105.1" />
  </ItemGroup>
</Project>
PROJECT
dotnet run --project "$task_probe/UnitMergeProbe.csproj" -c Release -- "$task_probe/results"
