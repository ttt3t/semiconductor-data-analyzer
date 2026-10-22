#!/usr/bin/env bash
set -euo pipefail
task_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
task_probe="$task_root/bin/BatchImportBenchmark"
mkdir -p "$task_probe"
cp "$task_root/Tests/BatchImportBenchmark.cs.txt" "$task_probe/Program.cs"
cat > "$task_probe/BatchImportBenchmark.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <Compile Include="../../Models/*.cs" Exclude="../../Models/WaferDie.cs;../../Models/WaferMapPage.cs" />
    <Compile Include="../../Services/*.cs" Exclude="../../Services/ExportWorker.cs;../../Services/HeatmapScale.cs;../../Services/IdleMemoryReclaimer.cs;../../Services/XlsxReportService.cs;../../Services/WindowLayoutPersistence.cs;../../Services/ManagementReportService.cs;../../Services/LayoutSettings.cs;../../Services/ManagementTrendService.cs;../../Services/MarkdownDocumentService.cs;../../Services/BinPalette.cs;../../Services/MapDataService.cs" />
    <PackageReference Include="ClosedXML" Version="0.105.1"/><PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.32"/>
  </ItemGroup>
</Project>
PROJECT
dotnet run --project "$task_probe/BatchImportBenchmark.csproj" -c Release -- "${1:-$task_probe/data}" "${2:-25}" "${3:-2000}" "${4:-120}"
