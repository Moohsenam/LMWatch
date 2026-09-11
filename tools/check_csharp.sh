#!/usr/bin/env bash
# Type-checks the WPF app's C# against stub UI types, so the code can be verified
# on a machine that cannot restore the Windows Desktop reference pack.
# On Windows just build the real solution instead: dotnet build -c Release.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${TMPDIR:-/tmp}/claude-watch-typecheck"

rm -rf "$WORK"
mkdir -p "$WORK/src"

cp "$ROOT/nuget.config" "$WORK/"
cp -r "$ROOT/src/ClaudeWatch.App/"*.cs "$WORK/src/"
mkdir -p "$WORK/src/Views"
cp "$ROOT/src/ClaudeWatch.App/Views/"*.cs "$WORK/src/Views/"
cp "$ROOT/tools/wpf-stubs/"*.cs "$WORK/src/"

cat > "$WORK/check.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NoWarn>$(NoWarn);CA1416;CS0067;CS0169;CS0649;CS8618;CS0108</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.cs" />
    <ProjectReference Include="CORE_PROJECT" />
  </ItemGroup>
</Project>
PROJECT

sed -i "s|CORE_PROJECT|$ROOT/src/ClaudeWatch.Core/ClaudeWatch.Core.csproj|" "$WORK/check.csproj"

cd "$WORK"
dotnet build --nologo -v quiet 2>&1 | grep -E 'error|Build succeeded' || true
