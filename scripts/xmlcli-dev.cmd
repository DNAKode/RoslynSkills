@echo off
setlocal
set "SCRIPT_DIR=%~dp0"

set "XMLCLI_USE_PUBLISHED=0"
set "XMLCLI_REFRESH_PUBLISHED=0"
call "%SCRIPT_DIR%xmlcli.cmd" %*
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
