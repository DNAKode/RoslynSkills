@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"

set "CACHE_DIR=%REPO_ROOT%\artifacts\roscli-cache"
set "CACHE_STAMP=%CACHE_DIR%\publish.stamp"
dotnet publish "%REPO_ROOT%\src\RoslynSkills.Cli" -c Release -o "%CACHE_DIR%" --nologo
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" (
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('o') | Set-Content -LiteralPath '%CACHE_STAMP%' -NoNewline" >nul 2>nul
    echo Roscli cache warmed: %CACHE_DIR%
)
endlocal & exit /b %EXIT_CODE%

