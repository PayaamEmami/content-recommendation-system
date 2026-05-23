@echo off
setlocal
title CRS - feed generation job

echo ===============================================
echo Running CRS feed generation job
echo ===============================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-job.ps1" -JobName feed -Interactive
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo ===============================================
if "%EXIT_CODE%"=="0" (
    echo Job finished successfully ^(exit code 0^).
) else (
    echo Job FAILED with exit code %EXIT_CODE%.
)
echo ===============================================
echo.

pause
endlocal & exit /b %EXIT_CODE%
