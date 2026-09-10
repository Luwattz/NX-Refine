# Installing on Siemens NX 2312

## Prerequisites

- Siemens NX 2312 is installed.
- The project has been built against the local NX 2312 managed assemblies.
- NX is closed while the startup configuration is changed.

## Build

Run PowerShell from the repository root:

```powershell
.\scripts\build.ps1 -Configuration Release -NXInstallDir "C:\Program Files\Siemens\NX2312"
```

The build creates the core library and one lightweight command-entry library for each ribbon button under `deploy\application`.

## Register the custom application

NX reads custom application roots from the text file named by `UGII_CUSTOM_DIRECTORY_FILE`. Add the absolute repository `deploy` directory to that file on its own line. Preserve every existing entry because other integrations may use the same file.

The helper script performs the append operation without duplicating the entry:

```powershell
.\scripts\install-local.ps1 -CustomDirectoryFile "C:\NXCustom\custom_dirs.dat"
```

If the environment variable is not already configured, set the user-level `UGII_CUSTOM_DIRECTORY_FILE` variable to the absolute path of that file. Sign out and back in, or start NX from a new process, after changing an environment variable.

## Verify

1. Restart NX.
2. Confirm that the **Geometry Cleanup** tab is available in the ribbon.
3. If it is hidden by the active role, right-click the ribbon and enable **Geometry Cleanup**.
4. Open a disposable part copy and run **Geometry Cleanup > Analyze**.
5. Confirm that the Listing Window reports the analysis summary.

The `startup\nxrefine.men` file defines the commands, `startup\nxrefine_main.rtb` defines the ribbon layout, and `application\NXRefine*.dll` contains the command implementations.

## Uninstall

Close NX and remove only the NX Refine `deploy` path from the custom directory file. Do not delete entries belonging to other integrations. The repository directory can then be removed.
