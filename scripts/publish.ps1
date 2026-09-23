# 単一ファイルの exe (ランタイム同梱) を publish/ (-OutputDir を指定したらそこ) に出力する
param([string]$OutputDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'publish' }
# データ (src/Mh3gSim.Core/Data/*.json) を手で直した後に壊れたまま exe にしないよう、先に整合チェック
dotnet run --project "$root/tools/Mh3gSim.Bench" -c Release -- --check-data
if ($LASTEXITCODE -ne 0) { throw "データチェックで問題が見つかりました (上の check-data の内容を確認)" }
dotnet publish "$root/src/Mh3gSim" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $OutputDir
# 出力先の exe を起動したままだと上書きできずに失敗する。失敗したのに「出力:」を表示しない
if ($LASTEXITCODE -ne 0) { throw "exe の作成に失敗しました (出力先の exe を起動中なら閉じてから再実行するか、-OutputDir で別のフォルダを指定)" }
Write-Host "出力: $(Join-Path $OutputDir 'MH3GSkillSim.exe')"
