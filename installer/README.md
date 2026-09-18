# Grid Stage 1 installer

`Grid.iss` produces the temporary, unsigned Stage 1 per-user installer. It is
compiled from the complete directory-based `win-x64` publish output; Grid is
not published as a single file.

The installer:

- installs to `%LOCALAPPDATA%\Programs\Grid` without elevation;
- creates a Start menu shortcut named **Grid** that launches `Grid.exe`;
- registers an uninstaller in Windows Installed Apps;
- reuses a fixed application ID so an upgrade replaces application files;
- never installs into or deletes an MO2, Skyrim, mod, profile, save, download,
  output, or game directory; and
- does not remove `%LOCALAPPDATA%\Grid`, which contains Grid's legitimate local
  connection state.

The `G` icon referenced by the installer is a temporary recovery-stage identity,
not final branding.

Run `eng/Build-Distribution.ps1` from PowerShell to publish Grid and compile
`artifacts/installer/GridSetup.exe`. The script requires the official signed
Inno Setup 6.7.3 compiler. Set `INNO_SETUP_ISCC` or pass
`-InnoCompilerPath` when it is not installed at a standard location.

Artifacts are unsigned and may trigger Microsoft Defender SmartScreen. A
successful local build does not establish clean-machine compatibility.

For source-development testing before the distribution provenance gate is
cleared, `eng/Refresh-GridDevelopmentInstall.ps1` refreshes only the fixed
per-user installation at `%LOCALAPPDATA%\Programs\Grid`. It requires the
explicit `-AuthorizeInstalledRefresh` switch, refuses while the installed app
is running, uses the same Release publish configuration as the installer,
retains the previous installation as a rollback directory, keeps
the Start-menu shortcut bound to the installed `Grid.exe`, and does not modify
`%LOCALAPPDATA%\Grid` connection state. This local workflow does not produce or
approve a distributable installer.
