@echo off
setlocal
set "SCRIPT_DIR=%~dp0"

set "ROSCLI_USE_PUBLISHED=0"
set "ROSCLI_REFRESH_PUBLISHED=0"
call "%SCRIPT_DIR%roscli.cmd" %*
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
