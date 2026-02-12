@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "SERVER_JS=%SCRIPT_DIR%..\tools\csharp-lsp-mcp\server.js"
node "%SERVER_JS%"
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%