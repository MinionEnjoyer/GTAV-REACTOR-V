@echo off
echo Reactor V - backed-up STORY MODE diagnostic preparation
echo Keep GTA closed until this window shows READY.
echo This installs the approved diagnostic build and starts LOCAL collectors.
echo It does not start GTA or upload data. Snapshots may contain private data.
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "%~dp0Start-GameCapture.ps1"
if errorlevel 1 (
  echo.
  echo PREPARATION FAILED. Do not launch GTA for this test. Share the error text with Adam/Codex.
) else (
  echo.
  echo The desktop launcher has finished. Collectors run independently.
  echo Launch promptly after READY. If you delay, ask for a fresh readiness check first.
  echo Do not run this installer a second time while the test is active.
)
echo.
pause
