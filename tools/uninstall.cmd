@echo off
rem Uninstalls Kobold for the current user. Double-click, or run from a console.
rem When double-clicked (no arguments) a failure pauses so the message stays on
rem screen; scripted runs are never blocked.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
set CODE=%ERRORLEVEL%
if not "%CODE%"=="0" (
    echo.
    echo Uninstall failed - see the messages above.
    if "%~1"=="" pause
)
exit /b %CODE%
