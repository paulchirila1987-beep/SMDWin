@echo off
title SMDWin — Publish Standalone
color 0B
echo.
echo  ============================================
echo   SMDWin v3 — Publish Standalone EXE
echo   Rezultat: un singur .exe fara dependinte
echo  ============================================
echo.

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo  [EROARE] .NET SDK nu este instalat!
    echo  https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

cd /d "%~dp0"

echo  [0/3] Verific daca SMDWin ruleaza deja...
tasklist /FI "IMAGENAME eq SMDWin.exe" 2>nul | find /I "SMDWin.exe" >nul
if %errorlevel% equ 0 (
    echo  [INFO] SMDWin.exe este pornit. Il inchid automat pentru publish...
    taskkill /F /IM SMDWin.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
    echo  [OK] Proces inchis.
) else (
    echo  [OK] SMDWin nu ruleaza.
)
echo.

echo  [1/3] Curatare cache vechi (obj + bin + publish)...
if exist obj rmdir /s /q obj
if exist bin rmdir /s /q bin
if exist publish rmdir /s /q publish
echo  [OK] Cache curatat.
echo.

echo  [2/3] Restaurare dependinte NuGet...
dotnet restore
if %errorlevel% neq 0 (
    echo.
    echo  [EROARE] Restaurare NuGet esuata.
    pause
    exit /b 1
)
echo.

echo  [3/3] Publicare self-contained (poate dura 1-2 minute)...
echo.

dotnet publish -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "publish"

if %errorlevel% neq 0 (
    echo.
    echo  [EROARE] Publicare esuata.
    pause
    exit /b 1
)

echo.
echo  ============================================
echo   [OK] Fisierele sunt in: publish\
echo   Copiaza folderul publish\ pe orice PC
echo   Windows 10/11 x64 — fara .NET necesar!
echo  ============================================
echo.

:: Deschide folderul publish
explorer "%~dp0publish"
pause
