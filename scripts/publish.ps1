# 単一ファイルの exe (ランタイム同梱) を publish/ に出力する
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# データ (src/Mh3gSim.Core/Data/*.json) を手で直した後に壊れたまま exe にしないよう、先に整合チェック
dotnet run --project "$root/tools/Mh3gSim.Bench" -c Release -- --check-data
if ($LASTEXITCODE -ne 0) { throw "データチェックで問題が見つかりました (上の check-data の内容を確認)" }
dotnet publish "$root/src/Mh3gSim" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$root/publish"
Write-Host "出力: $root\publish\MH3GSkillSim.exe"
