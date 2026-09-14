@echo off
echo Reactor V collector lifetime check - OFFLINE ONLY
echo This does not start GTA, install files, capture memory, or upload anything.
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "%~dp0Start-OfflineCollectors.ps1" -Seconds 120
if errorlevel 1 (
  echo.
  echo The offline check could not start. Do not launch GTA for this test.
) else (
  echo.
  echo The PowerShell launcher has now exited. The two probes should run independently.
  echo Wait two minutes, then tell Adam/Codex the offline check is done.
  echo You can close this window. Both probes stop automatically.
)
echo.
pause
