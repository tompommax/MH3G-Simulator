# 単一ファイルの exe (ランタイム同梱) を publish/ に出力する
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet publish "$root/src/Mh3gSim" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$root/publish"
Write-Host "出力: $root\publish\MH3GSkillSim.exe"
