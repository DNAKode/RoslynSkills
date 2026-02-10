@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"

set "USE_PUBLISHED=%ROSCLI_USE_PUBLISHED%"
if /I "%USE_PUBLISHED%"=="1" goto :use_published
if /I "%USE_PUBLISHED%"=="true" goto :use_published
if /I "%USE_PUBLISHED%"=="yes" goto :use_published
if /I "%USE_PUBLISHED%"=="on" goto :use_published
goto :use_dotnet_run

:use_published
set "CACHE_DIR=%REPO_ROOT%\artifacts\roscli-cache"
set "CLI_DLL=%CACHE_DIR%\RoslynAgent.Cli.dll"
set "CACHE_STAMP=%CACHE_DIR%\publish.stamp"
set "CACHE_CHECK_MARKER=%CACHE_DIR%\stalecheck.stamp"
set "REFRESH_PUBLISHED=%ROSCLI_REFRESH_PUBLISHED%"
set "STALE_CHECK=%ROSCLI_STALE_CHECK%"
if "%STALE_CHECK%"=="" set "STALE_CHECK=0"
set "STALE_CHECK_INTERVAL=%ROSCLI_STALE_CHECK_INTERVAL_SECONDS%"
if "%STALE_CHECK_INTERVAL%"=="" set "STALE_CHECK_INTERVAL=10"
set "NEED_PUBLISH=0"
if not exist "%CLI_DLL%" set "NEED_PUBLISH=1"
if /I "%REFRESH_PUBLISHED%"=="1" set "NEED_PUBLISH=1"
if /I "%REFRESH_PUBLISHED%"=="true" set "NEED_PUBLISH=1"
if /I "%REFRESH_PUBLISHED%"=="yes" set "NEED_PUBLISH=1"
if /I "%REFRESH_PUBLISHED%"=="on" set "NEED_PUBLISH=1"

if "%NEED_PUBLISH%"=="0" (
    if /I not "%STALE_CHECK%"=="0" if /I not "%STALE_CHECK%"=="false" if /I not "%STALE_CHECK%"=="no" if /I not "%STALE_CHECK%"=="off" (
        call :should_skip_stale_check "%CACHE_CHECK_MARKER%" "%STALE_CHECK_INTERVAL%"
        if errorlevel 1 (
            call :check_cache_stale "%REPO_ROOT%" "%CACHE_STAMP%"
            if errorlevel 1 (
                set "NEED_PUBLISH=1"
            ) else (
                call :touch_cache_marker "%CACHE_CHECK_MARKER%"
            )
        )
    )
)

if "%NEED_PUBLISH%"=="1" (
    dotnet publish "%REPO_ROOT%\src\RoslynAgent.Cli" -c Release -o "%CACHE_DIR%" --nologo
    if errorlevel 1 (
        set "EXIT_CODE=%ERRORLEVEL%"
        goto :done
    )
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('o') | Set-Content -LiteralPath '%CACHE_STAMP%' -NoNewline" >nul 2>nul
    call :touch_cache_marker "%CACHE_CHECK_MARKER%"
)

dotnet "%CLI_DLL%" %*
set "EXIT_CODE=%ERRORLEVEL%"
goto :done

:use_dotnet_run
dotnet run --project "%REPO_ROOT%\src\RoslynAgent.Cli" -- %*
set "EXIT_CODE=%ERRORLEVEL%"

:done
endlocal & exit /b %EXIT_CODE%

:check_cache_stale
setlocal
set "CHECK_REPO_ROOT=%~1"
set "CHECK_STAMP=%~2"
if not exist "%CHECK_STAMP%" (
    endlocal & exit /b 1
)

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $repoRoot='%CHECK_REPO_ROOT%'; $stampPath='%CHECK_STAMP%'; $extensions=[System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase); @('.cs','.csproj','.props','.targets','.json') | ForEach-Object { [void]$extensions.Add($_) }; $watchPaths=@((Join-Path $repoRoot 'src')); foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json','NuGet.config','RoslynSkill.slnx')) { $watchPaths += (Join-Path $repoRoot $name) }; $stampTime=(Get-Item -LiteralPath $stampPath).LastWriteTimeUtc; foreach($watchPath in $watchPaths) { if (-not (Test-Path -LiteralPath $watchPath)) { continue }; $item=Get-Item -LiteralPath $watchPath; if ($item.PSIsContainer) { foreach($file in Get-ChildItem -LiteralPath $watchPath -Recurse -File -ErrorAction SilentlyContinue) { if (-not $extensions.Contains($file.Extension)) { continue }; if ($file.LastWriteTimeUtc -gt $stampTime) { exit 3 } } } elseif ($item.LastWriteTimeUtc -gt $stampTime) { exit 3 } }; exit 0" >nul 2>nul
set "STALE_CHECK_EXIT=%ERRORLEVEL%"
if "%STALE_CHECK_EXIT%"=="0" (
    endlocal & exit /b 0
)

endlocal & exit /b 1

:should_skip_stale_check
setlocal
set "CHECK_MARKER=%~1"
set "CHECK_INTERVAL=%~2"
if not exist "%CHECK_MARKER%" (
    endlocal & exit /b 1
)

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $marker='%CHECK_MARKER%'; $intervalRaw='%CHECK_INTERVAL%'; $interval=0.0; if(-not [double]::TryParse($intervalRaw, [ref]$interval)) { exit 1 }; if($interval -le 0) { exit 1 }; $age=((Get-Date).ToUniversalTime() - (Get-Item -LiteralPath $marker).LastWriteTimeUtc).TotalSeconds; if($age -lt $interval) { exit 0 } else { exit 1 }" >nul 2>nul
set "SKIP_EXIT=%ERRORLEVEL%"
if "%SKIP_EXIT%"=="0" (
    endlocal & exit /b 0
)

endlocal & exit /b 1

:touch_cache_marker
setlocal
set "CHECK_MARKER=%~1"
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('o') | Set-Content -LiteralPath '%CHECK_MARKER%' -NoNewline" >nul 2>nul
endlocal & exit /b 0
