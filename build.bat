@echo off
setlocal
cd /d "%~dp0"
echo.
echo ===== Chalkak build =====
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [!] .NET SDK was not found.
  echo     Install it with:  winget install Microsoft.DotNet.SDK.8
  echo     Then run this file again.
  echo.
  pause
  exit /b 1
)

if exist dist rmdir /s /q dist

set ARGS=-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist

dotnet publish Chalkak.csproj %ARGS%
if errorlevel 1 (
  echo.
  echo [i] Build failed. Retrying without the Windows "Share" feature ...
  dotnet publish Chalkak.csproj %ARGS% -p:NoShare=true
  if errorlevel 1 (
    echo.
    echo [!] Build failed. Please send the messages above to the developer.
    pause
    exit /b 1
  )
)

rem Make a copy with the Korean file name (U+CC30 U+CE75)
powershell -NoProfile -Command "Copy-Item -LiteralPath 'dist\Chalkak.exe' -Destination ('dist\' + [string][char]0xCC30 + [char]0xCE75 + '.exe') -Force"

echo.
echo ===== Done =====
echo   Output folder: dist
echo   (Chalkak.exe and the Korean-named copy are identical)
echo.
pause
