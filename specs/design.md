# DSH Web Launcher Design

## Architecture

- `DSHWebLauncher.exe`: .NET Framework 4.8 WinForms application built with the Windows inbox C# compiler.
- Per-user configuration: `%LOCALAPPDATA%\DSHWebManager\config.json`.
- Runtime state and logs: `%LOCALAPPDATA%\DSHWebManager\state.json` and `logs\`.
- Managed runtime: `%LOCALAPPDATA%\DSHWebManager\runtime`, containing checksum-verified Node.js and the official npm DSH package.
- DSH discovery: managed runtime first, then PATH entries, standard npm global locations, and WinGet Node package paths.
- Update source: npm official registry metadata for `@deepseek-ai/dsh/latest`; external runtimes are never modified.
- Installation: per-user PowerShell installer copies the published files and creates Start menu/Desktop shortcuts. The uninstaller removes app files and shortcuts but preserves configuration, logs, `.dsh`, and workspaces by default.

## Safety

- Bind only to `127.0.0.1`.
- Start with an explicit workspace working directory and port.
- Stop only a Node process whose PID, start time, CLI path, and command line match persisted state.
- Never kill an unknown listener occupying the configured port.

## Distribution

The release ZIP is the canonical portable artifact. `Install.cmd` installs it per user. An optional self-extracting setup EXE may wrap the same files; SmartScreen can warn until the binary is code-signed.
