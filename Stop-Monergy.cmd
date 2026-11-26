@echo off
setlocal
cd /d "%~dp0"
echo Stopping standard LOCAL Monergy (retaining credentials and volumes)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\uat\Invoke-MonergyUat.ps1" -Action Stop
if errorlevel 1 (
  echo.
  echo Monergy stop did not complete; see the message above.
  pause
  exit /b 1
)
echo Monergy stopped. Retained profile and Docker volumes were preserved.
pause
exit /b 0
