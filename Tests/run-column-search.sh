#!/usr/bin/env bash
set -euo pipefail
task_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
task_probe="$task_root/bin/ColumnSearchProbe"
mkdir -p "$task_probe"
cp "$task_root/Tests/ColumnSearchProbe.cs.txt" "$task_probe/Program.cs"
cat > "$task_probe/Probe.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><Compile Include="../../Services/ColumnSearchRule.cs"/><Compile Include="../../Models/ColumnSearchState.cs"/></ItemGroup>
</Project>
PROJECT
dotnet run --project "$task_probe/Probe.csproj" -c Release
