@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem NX Refine update script for computers where PowerShell scripts are blocked.
rem NX is detected automatically. To override it, pass the installation root as
rem the first argument or set NX_INSTALL_DIR before running this script.
rem Set NXREFINE_NO_PAUSE=1 for unattended use.
set "CUSTOM_DIRECTORY_FILE=%USERPROFILE%\Documents\NXCustom\custom_dirs.dat"
if not "%UGII_CUSTOM_DIRECTORY_FILE%"=="" set "CUSTOM_DIRECTORY_FILE=%UGII_CUSTOM_DIRECTORY_FILE%"

set "REPO_ROOT=%~dp0.."
for %%I in ("%REPO_ROOT%") do set "REPO_ROOT=%%~fI"
set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
set "BITMAP_DIRECTORY=%REPO_ROOT%\deploy\application"

call "%~dp0find-nx2512.cmd" "%~1"
if errorlevel 1 goto failed
echo Using NX installation: "%NX_INSTALL_DIR%"

if not exist "%MSBUILD%" (
  echo ERROR: MSBuild was not found:
  echo        %MSBUILD%
  echo Install Visual Studio Build Tools with .NET Framework targeting tools.
  goto failed
)

echo Building NX Refine against NX 2512...
"%MSBUILD%" "%REPO_ROOT%\NXRefine.sln" /t:Rebuild /p:Configuration=Release /p:NXInstallDir="%NX_INSTALL_DIR%" /m
if errorlevel 1 goto failed

for %%C in (Analyze AutoSimplify SmallFaces RemoveBlends FillHoles ClearCavities RepairUnattached RemoveMarkings RepairSheets PatchOpenings Settings About) do (
  echo Building command: %%C
  "%MSBUILD%" "%REPO_ROOT%\src\NXRefine.Command\NXRefine.Command.csproj" /t:Rebuild /p:Configuration=Release /p:NXInstallDir="%NX_INSTALL_DIR%" /p:AssemblyName=NXRefine.%%C /m
  if errorlevel 1 goto failed
)

if not exist "%CUSTOM_DIRECTORY_FILE%" (
  for %%I in ("%CUSTOM_DIRECTORY_FILE%") do mkdir "%%~dpI" 2>nul
  type nul > "%CUSTOM_DIRECTORY_FILE%"
  if errorlevel 1 goto failed
)

findstr /l /i /x /c:"%REPO_ROOT%\deploy" "%CUSTOM_DIRECTORY_FILE%" >nul 2>nul
if not errorlevel 1 goto custom_directory_ready
ver >nul
for %%I in ("%REPO_ROOT%\deploy") do >>"%CUSTOM_DIRECTORY_FILE%" echo %%~I
if errorlevel 1 goto failed

:custom_directory_ready
rem A newly created custom directory file must also be selected by NX.
if not defined UGII_CUSTOM_DIRECTORY_FILE (
  setx UGII_CUSTOM_DIRECTORY_FILE "%CUSTOM_DIRECTORY_FILE%" >nul
  if errorlevel 1 goto failed
)

rem Register the custom bitmap directory for the current Windows user.
set "NEW_BITMAP_PATH=%BITMAP_DIRECTORY%"
if not "%UGII_BITMAP_PATH%"=="" (
  echo ;%UGII_BITMAP_PATH%; | findstr /i /c:";%BITMAP_DIRECTORY%;" >nul
  if errorlevel 1 (set "NEW_BITMAP_PATH=%BITMAP_DIRECTORY%;%UGII_BITMAP_PATH%") else (set "NEW_BITMAP_PATH=%UGII_BITMAP_PATH%")
)
setx UGII_BITMAP_PATH "%NEW_BITMAP_PATH%" >nul
if errorlevel 1 echo WARNING: Could not persist UGII_BITMAP_PATH. Set it manually before starting NX.

echo.
echo Update complete.
echo Custom directory file: "%CUSTOM_DIRECTORY_FILE%"
echo Registered deploy path: "%REPO_ROOT%\deploy"
echo Registered bitmap path: "%BITMAP_DIRECTORY%"
echo Restart NX 2512 to load the new version.
if not "%NXREFINE_NO_PAUSE%"=="1" pause
exit /b 0

:failed
echo.
echo ERROR: Update failed. Review the error above; installation is incomplete.
if not "%NXREFINE_NO_PAUSE%"=="1" pause
exit /b 1

