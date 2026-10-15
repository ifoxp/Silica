# Builds a release into .\artifacts:
#   Silica-<version>-portable.zip   single Silica.exe, no installation needed
#   Silica-<version>-uk.msi         installer, Ukrainian
#   Silica-<version>-en.msi         installer, English
#
# Requires the .NET 9 SDK. WiX is restored as a local tool (.config/dotnet-tools.json).

param([string]$Version = "1.2.0")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot "artifacts"
$publish = Join-Path $out "publish"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $publish | Out-Null

# Uncompressed single file: the runtime maps it from disk, which keeps memory use ~100 MB lower
# than a compressed bundle. The zip and the MSI compress it for download anyway.
dotnet publish src/Silica/Silica.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Compress-Archive -Path (Join-Path $publish "Silica.exe") -DestinationPath (Join-Path $out "Silica-$Version-portable.zip") -CompressionLevel Optimal

dotnet tool restore | Out-Null
dotnet wix extension add -g WixToolset.UI.wixext/5.0.2 | Out-Null
dotnet wix extension add -g WixToolset.Util.wixext/5.0.2 | Out-Null
python installer/make_license_rtf.py
foreach ($culture in @(@{ Name = "uk"; Culture = "uk-UA" }, @{ Name = "en"; Culture = "en-US" })) {
    dotnet wix build installer/Silica.wxs -arch x64 `
        -ext WixToolset.UI.wixext/5.0.2 -ext WixToolset.Util.wixext/5.0.2 `
        -loc "installer/$($culture.Name)-$($culture.Culture.Substring(3).ToLower()).wxl" -culture $culture.Culture `
        -d Version=$Version -d PublishDir=$publish -b installer `
        -o (Join-Path $out "Silica-$Version-$($culture.Name).msi")
    if ($LASTEXITCODE -ne 0) { throw "installer build failed ($($culture.Culture))" }
}

Remove-Item (Join-Path $out "*.wixpdb") -ErrorAction SilentlyContinue
Get-ChildItem $out -File | ForEach-Object { "{0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
