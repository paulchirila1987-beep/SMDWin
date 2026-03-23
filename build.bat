@echo off
title SMDWin — Build ^& Run
color 0A
echo.
echo  ============================================
echo   SMDWin v3 — Build Script (Smart)
echo  ============================================
echo.

:: ── Verificare .NET SDK ───────────────────────────────────────────────────
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo  [EROARE] .NET SDK nu este instalat!
    echo  Descarca de la: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)
echo  [OK] .NET SDK:
dotnet --version
echo.

:: ── Cauta automat fisierul .csproj in subfolderele curente ───────────────
echo  [INFO] Caut WinDiag.csproj...
set CSPROJ=
for /r "%~dp0" %%f in (WinDiag.csproj) do (
    if not defined CSPROJ set CSPROJ=%%f
)

if not defined CSPROJ (
    echo.
    echo  [EROARE] Nu am gasit WinDiag.csproj in niciun subfolder!
    echo.
    echo  Structura asteptata (oricare varianta):
    echo    build.bat
    echo    SMDWin\WinDiag.csproj
    echo         - SAU -
    echo    build.bat
    echo    SMDWin\WinDiag.csproj
    echo.
    echo  Verifica ca fisierele proiectului sunt dezarhivate corect.
    pause
    exit /b 1
)

echo  [OK] Gasit: %CSPROJ%
echo.

:: ── Navigare la folderul cu .csproj ──────────────────────────────────────
set PROJDIR=%CSPROJ%\..
cd /d "%PROJDIR%"

:: ── Restore NuGet ─────────────────────────────────────────────────────────
echo  [1/3] Restaurare pachete NuGet...
dotnet restore
if %errorlevel% neq 0 (
    echo  [EROARE] Restaurare esuata. Verifica conexiunea la internet.
    pause
    exit /b 1
)
echo  [OK] Pachete restaurate.
echo.

:: ── Build ─────────────────────────────────────────────────────────────────
echo  [2/3] Compilare...
dotnet build -c Release --no-restore
if %errorlevel% neq 0 (
    echo  [EROARE] Compilare esuata. Vezi erorile de mai sus.
    pause
    exit /b 1
)
echo  [OK] Compilat cu succes.
echo.

:: ── Gasire .exe (cauta recursiv) ─────────────────────────────────────────
echo  [3/3] Caut executabilul...
set EXE=
for /r "%PROJDIR%\bin\Release" %%f in (SMDWin.exe) do (
    if not defined EXE set EXE=%%f
)

if not defined EXE (
    echo  [EROARE] SMDWin.exe nu a fost gasit in bin\Release\
    pause
    exit /b 1
)

echo  [OK] Executabil: %EXE%
echo.

:: ── Lansare cu drepturi admin ─────────────────────────────────────────────
echo  Lansare SMDWin ca Administrator...
powershell -Command "Start-Process '%EXE%' -Verb RunAs"

echo.
echo  [OK] SMDWin pornit!
echo.
pause
