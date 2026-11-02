@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem Return the detected installation root in the caller's NX_INSTALL_DIR.
rem An argument or NX_INSTALL_DIR is authoritative: do not silently replace it.
set "NX_FOUND="
if not "%~1"=="" (
  call :try_directory "%~1"
  if not defined NX_FOUND (
    >&2 echo ERROR: NXOpen.dll was not found under "%~1\NXBIN\managed".
    exit /b 1
  )
  goto found
)
if defined NX_INSTALL_DIR (
  call :try_directory "%NX_INSTALL_DIR%"
  if not defined NX_FOUND (
    >&2 echo ERROR: NX_INSTALL_DIR does not contain NXBIN\managed\NXOpen.dll.
    >&2 echo        "%NX_INSTALL_DIR%"
    exit /b 1
  )
  goto found
)

rem Prefer the NX environment already selected by the user or NX launcher.
if defined UGII_BASE_DIR call :try_directory "%UGII_BASE_DIR%"
if defined NX_FOUND goto found
if defined UGII_ROOT_DIR call :try_directory "%UGII_ROOT_DIR%"
if defined NX_FOUND goto found
if defined UGII_ROOT_DIR call :try_directory "%UGII_ROOT_DIR%\.."
if defined NX_FOUND goto found

rem Check both registry views, including installs outside standard directories.
for %%V in (64 32) do (
  for %%K in ("HKLM\SOFTWARE\Siemens\NX\2512" "HKLM\SOFTWARE\Unigraphics Solutions\NX\2512") do (
    for %%N in (UGII_BASE_DIR InstallDir InstallLocation InstallPath) do call :registry_directory "%%~K" "%%N" %%V
  )
)
if defined NX_FOUND goto found
for %%V in (64 32) do (
  for %%H in (HKLM HKCU) do (
    for /f "delims=" %%K in ('reg query "%%H\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" /reg:%%V 2^>nul') do call :uninstall_directory "%%K" %%V
  )
)
if defined NX_FOUND goto found

rem Bounded directory search on mounted drive letters, including D:.
rem Do not recursively traverse disks or select a different NX release by name.
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
  if exist "%%D:\" (
    for %%P in ("%%D:" "%%D:\Siemens" "%%D:\Program Files\Siemens" "%%D:\Program Files") do (
      for /d %%I in ("%%~P\NX*2512*" "%%~P\Designcenter*2512*") do call :try_directory "%%~fI"
      if defined NX_FOUND goto found
    )
  )
)
if defined NX_FOUND goto found

>&2 echo ERROR: No NX installation containing NXBIN\managed\NXOpen.dll was found.
>&2 echo Run update-nxrefine-nx2512.cmd "D:\path\to\NX2512" to specify it.
>&2 echo You can also set NX_INSTALL_DIR or start from an NX command prompt.
exit /b 1

:try_directory
if defined NX_FOUND exit /b 0
if "%~1"=="" exit /b 0
for %%I in ("%~1\.") do if exist "%%~fI\NXBIN\managed\NXOpen.dll" set "NX_FOUND=%%~fI"
exit /b 0

:registry_directory
if defined NX_FOUND exit /b 0
for /f "tokens=1,2,*" %%A in ('reg query "%~1" /v "%~2" /reg:%3 2^>nul') do (
  if /i "%%B"=="REG_SZ" call :try_directory "%%C"
)
exit /b 0

:uninstall_directory
if defined NX_FOUND exit /b 0
rem Only NX 2512 uninstall records may supply a directory.
reg query "%~1" /v DisplayName /reg:%2 2>nul | findstr /i /r /c:"NX.*2512" >nul
if errorlevel 1 exit /b 0
call :registry_directory "%~1" InstallLocation %2
exit /b 0

:found
endlocal & set "NX_INSTALL_DIR=%NX_FOUND%" & exit /b 0
