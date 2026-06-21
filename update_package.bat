@echo off
REM ===========================================================================
REM Builds a Release single-file exe and assembles ZDPS_UpdatePackage, an
REM overwrite-on-top-of-release update bundle (exe + localized AppStrings).
REM Extract ZDPS_UpdatePackage.zip over an existing install to apply changes.
REM ===========================================================================

dotnet publish "BPSR-ZDPS/BPSR-ZDPS.csproj" -r win-x64 -c Release -o ./publish_new /p:PublishSingleFile=true /p:PublishTrimmed=false /p:TrimMode=Link /p:IncludeAllContentForSelfExtract=false --self-contained false
if errorlevel 1 (
    echo Publish failed.
    exit /b 1
)

if exist ZDPS_UpdatePackage rmdir /s /q ZDPS_UpdatePackage
mkdir ZDPS_UpdatePackage\Data
copy /Y publish_new\BPSR-ZDPS.exe ZDPS_UpdatePackage\BPSR-ZDPS.exe
copy /Y BPSR-ZDPS\Data\AppStrings.en.json ZDPS_UpdatePackage\Data\AppStrings.en.json
copy /Y BPSR-ZDPS\Data\AppStrings.ja.json ZDPS_UpdatePackage\Data\AppStrings.ja.json

if exist ZDPS_UpdatePackage.zip del /q ZDPS_UpdatePackage.zip
powershell -NoProfile -Command "Compress-Archive -Path 'ZDPS_UpdatePackage\*' -DestinationPath 'ZDPS_UpdatePackage.zip' -Force"

echo Done. Overwrite your install with ZDPS_UpdatePackage\ (or extract ZDPS_UpdatePackage.zip).
