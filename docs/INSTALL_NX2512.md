# Installing on Siemens NX 2512

## Prerequisites

- Siemens NX 2512 is installed.
- The project has been built against the local NX 2512 managed assemblies.
- NX is closed while the startup configuration is changed.

## Build

Run PowerShell from the repository root:

```powershell
.\scripts\build.ps1 -Configuration Release -NXInstallDir "C:\Program Files\Siemens\DesigncenterNX2512"
```

The build creates the core library and one lightweight command-entry library for each ribbon button under `deploy\application`.

If PowerShell scripts are blocked, run `scripts\update-nxrefine-nx2512.cmd` from Command Prompt or double-click it. Keep `scripts\find-nx2512.cmd` beside it. No PowerShell script is required for this workflow. It builds the project and registers the `deploy` and bitmap paths for the current user without administrator rights. When `UGII_CUSTOM_DIRECTORY_FILE` is not already set, it also sets the user variable to the custom directory file it creates. The registration steps below describe the equivalent manual setup.

The updater resolves the NX installation in this order:

1. An installation root passed as the first argument, or the `NX_INSTALL_DIR` environment variable. An invalid explicit path fails instead of silently selecting another installation.
2. The current NX environment: `UGII_BASE_DIR`, then `UGII_ROOT_DIR` (including an `NXBIN` or `UGII` subdirectory). These variables select the active installation; ensure it is NX 2512.
3. NX 2512 installation keys and NX 2512 uninstall records in both Windows registry views.
4. Directories named `NX*2512*` or `Designcenter*2512*` under each mounted drive letter's root, `Siemens`, `Program Files\Siemens`, and `Program Files`. This is a bounded search, not a recursive disk scan.

Every candidate must contain `NXBIN\managed\NXOpen.dll`. The selected root is printed before the build. An arbitrary custom directory that is absent from the environment and registry can be supplied explicitly:

```bat
scripts\update-nxrefine-nx2512.cmd "D:\Engineering Tools\NX2512"
```

The window pauses on success or failure so build errors remain visible. Set `NXREFINE_NO_PAUSE=1` before running the updater in automation. Edit `CUSTOM_DIRECTORY_FILE` at the top of the updater only if you need a different default configuration file. After the first registration, sign out and back in before starting NX so the new user environment variable is available to its launcher.

On Windows, run `tests\test-nx-install-detection.cmd` for isolated argument and NX environment-path checks, including spaces, parentheses, exclamation marks, and trailing separators. These checks use temporary marker files and do not require NX or change the registry. Registry discovery and drive-directory discovery still require verification on the target installation.

## Register the custom application

NX reads custom application roots from the text file named by `UGII_CUSTOM_DIRECTORY_FILE`. Add the absolute repository `deploy` directory to that file on its own line. Preserve every existing entry because other integrations may use the same file.

The helper script performs the append operation without duplicating the entry:

```powershell
.\scripts\install-local.ps1 -CustomDirectoryFile "C:\NXCustom\custom_dirs.dat"
```

If the environment variable is not already configured, set the user-level `UGII_CUSTOM_DIRECTORY_FILE` variable to the absolute path of that file. Sign out and back in, or start NX from a new process, after changing an environment variable.

The custom button bitmaps are stored directly in `deploy\application`, beside the DLLs. The menu references each bitmap by its stem, so NX can select the `.sc`, `.lc`, `.2s`, `.2l`, or `.8s` variant. These match the 16/24/32/48/128-pixel NX bitmap convention and use magenta transparency. The update workflows also register this application directory in the current user's `UGII_BITMAP_PATH` as a fallback; preserve any existing bitmap path entries.

## Verify

1. Restart NX.
2. Confirm that the **Cleanup** tab is available in the ribbon.
3. If it is hidden by the active role, right-click the ribbon and enable **Cleanup**.
4. Open a disposable part copy and run **Cleanup > Analyze**.
5. Confirm that the Listing Window reports the analysis summary.
6. Open **Cleanup > Clear Cavities**, select a solid body containing a known fully enclosed void, and confirm that the cavity face group is highlighted before testing Apply/OK on a disposable copy.
7. Open **Cleanup > Repair Unattached Faces**, select bodies containing a known small gap, and verify both sides of the gap are highlighted before testing Apply/OK on a disposable copy.

The `startup\nxrefine.men` file defines the commands, `startup\nxrefine_main.rtb` defines the ribbon layout, and `application\NXRefine*.dll` contains the command implementations.

## Uninstall

Close NX and remove only the NX Refine `deploy` path from the custom directory file. Do not delete entries belonging to other integrations. The repository directory can then be removed.

