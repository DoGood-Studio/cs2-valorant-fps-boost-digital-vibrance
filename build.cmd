@echo off
setlocal
cd /d "%~dp0"
echo [%date% %time%] derleme basladi > build.log
dotnet publish GameColorGuard.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=none -o publish -nologo >> build.log 2>&1
echo EXIT=%errorlevel% >> build.log
if errorlevel 1 goto :eof
for %%S in (active visible boost apps pro system) do (
  start "" /wait "%~dp0publish\GameColorGuard.exe" --render "%~dp0render-%%S.png" %%S
)
echo [%date% %time%] bitti >> build.log
