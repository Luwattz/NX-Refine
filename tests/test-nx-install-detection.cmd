@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Windows-only checks. Empty marker files stand in for NXOpen.dll; no NX
rem code is loaded and no registry or user environment settings are changed.
set "FINDER=%~dp0..\scripts\find-nx2512.cmd"
set "TEST_ROOT=%TEMP%\NX Refine detection %RANDOM% (test!)"
set "TEST_NX=%TEST_ROOT%\NX2512"
mkdir "%TEST_NX%\NXBIN\managed" || goto failed
mkdir "%TEST_NX%\UGII" || goto failed
type nul > "%TEST_NX%\NXBIN\managed\NXOpen.dll"

call :reset_environment
set "NX_INSTALL_DIR=%TEST_ROOT%\missing"
call "%FINDER%" "%TEST_NX%"
if errorlevel 1 goto failed
call :assert_root
if errorlevel 1 goto failed
echo PASS: explicit argument overrides environment and accepts spaces and punctuation.

call "%FINDER%" "%TEST_ROOT%\missing" 2>nul
if not errorlevel 1 goto failed
echo PASS: invalid explicit argument fails even with a valid environment path.

call :reset_environment
set "NX_INSTALL_DIR=%TEST_NX%"
call "%FINDER%"
if errorlevel 1 goto failed
call :assert_root
if errorlevel 1 goto failed
echo PASS: NX_INSTALL_DIR override.

set "NX_INSTALL_DIR=%TEST_ROOT%\missing"
set "UGII_BASE_DIR=%TEST_NX%"
call "%FINDER%" 2>nul
if not errorlevel 1 goto failed
echo PASS: invalid NX_INSTALL_DIR is not silently replaced.

call :reset_environment
set "UGII_BASE_DIR=%TEST_NX%"
call "%FINDER%"
if errorlevel 1 goto failed
call :assert_root
if errorlevel 1 goto failed
echo PASS: UGII_BASE_DIR.

call :reset_environment
set "UGII_ROOT_DIR=%TEST_NX%"
call "%FINDER%"
if errorlevel 1 goto failed
call :assert_root
if errorlevel 1 goto failed
echo PASS: UGII_ROOT_DIR points to installation root.

call :reset_environment
set "UGII_BASE_DIR=%TEST_ROOT%\missing"
set "UGII_ROOT_DIR=%TEST_NX%\NXBIN\"
call "%FINDER%"
if errorlevel 1 goto failed
call :assert_root
if errorlevel 1 goto failed
echo PASS: stale UGII_BASE_DIR and trailing-slash NXBIN path.

call :reset_environment
set "UGII_ROOT_DIR=%TEST_NX%\UGII\"
call "%FINDER%"
if errorlevel 1 goto failed
call :assert_root
if errorlevel 1 goto failed
echo PASS: trailing-slash UGII path.

rmdir /s /q "%TEST_ROOT%"
echo All NX installation environment-path checks passed.
exit /b 0

:reset_environment
set "NX_INSTALL_DIR="
set "UGII_BASE_DIR="
set "UGII_ROOT_DIR="
exit /b 0

:assert_root
if /i "%NX_INSTALL_DIR%"=="%TEST_NX%" exit /b 0
>&2 echo Expected "%TEST_NX%", got "%NX_INSTALL_DIR%".
exit /b 1

:failed
>&2 echo FAIL: NX installation detection check failed.
if exist "%TEST_ROOT%" rmdir /s /q "%TEST_ROOT%"
exit /b 1
