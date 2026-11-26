@echo off
setlocal
cd /d "%~dp0"
echo Starting standard LOCAL Monergy...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\uat\Invoke-MonergyUat.ps1" -Action Start
if errorlevel 1 (
  echo.
  echo Monergy could not start. Existing profile credentials and volumes were not intentionally reset.
  pause
  exit /b 1
)
echo Monergy is ready at http://127.0.0.1:4173/access
start "" "http://127.0.0.1:4173/access"
exit /b 0
