@echo off
setlocal
cd /d "%~dp0"
set "DST=%USERPROFILE%\OneDrive\Documents\Game Color Guard"
echo [%date% %time%] kurulum basladi > install.log

if not exist "eski-surum" mkdir "eski-surum"
if exist "%DST%\GameColorGuard.old.exe" if not exist "eski-surum\GameColorGuard.old.exe" copy /y "%DST%\GameColorGuard.old.exe" "eski-surum\" >> install.log 2>&1
if exist "%DST%\KULLANIM.txt" copy /y "%DST%\KULLANIM.txt" "eski-surum\" >> install.log 2>&1

echo Game Color Guard 3.1 derleniyor...
call "%~dp0build.cmd"
findstr /c:"EXIT=0" build.log > nul
if errorlevel 1 (
  echo HATA: derleme basarisiz, kurulu surume dokunulmadi >> install.log
  echo Derleme basarisiz oldu. Kurulu surume dokunulmadi.
  pause
  exit /b 1
)

echo Eski surum kapatiliyor ve yedekleniyor...
if not exist "%DST%" mkdir "%DST%"
taskkill /IM GameColorGuard.exe /F >> install.log 2>&1
taskkill /IM CS2ColorGuard.exe /F >> install.log 2>&1
timeout /t 2 /nobreak > nul
if exist "%DST%\GameColorGuard.exe" if not exist "%DST%\GameColorGuard.old.exe" (
  copy /y "%DST%\GameColorGuard.exe" "%DST%\GameColorGuard.old.exe" >> install.log 2>&1
)
copy /y "publish\GameColorGuard.exe" "%DST%\GameColorGuard.exe" >> install.log 2>&1
echo COPY=%errorlevel% >> install.log
dir "%DST%" >> install.log 2>&1
start "" "%DST%\GameColorGuard.exe"
echo [%date% %time%] bitti >> install.log
