# Release build: dist\clipdeck.exe and dist\clipdeck-kurulum.exe (single file, needs .NET 10 Desktop Runtime)
# The setup file is the same program; a file name containing "kurulum" makes it start in setup mode.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet publish "$root\clipdeck.csproj" -c Release -r win-x64 -p:SelfContained=false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o "$root\dist"
Copy-Item "$root\dist\clipdeck.exe" "$root\dist\clipdeck-kurulum.exe" -Force
Write-Host "Hazir: $root\dist\clipdeck.exe ve clipdeck-kurulum.exe"
