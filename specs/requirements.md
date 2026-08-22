# DSH Web Launcher Requirements

## Scope

Build a distributable Windows desktop manager that can install and manage the official DSH CLI. It must not package, inspect, or remove user conversations, credentials, or workspace data.

## Acceptance Criteria

1. When the app starts for the first time, it shall ask the user to select a workspace before starting DSH Web.
2. When a valid workspace and DSH CLI are available, the app shall start DSH Web on `127.0.0.1` and the configured port, then report healthy only after HTTP responds.
3. When DSH Web was started by the app, the app shall persist its PID and process start time and stop only that verified process.
4. When the configured port belongs to an unrelated process, the app shall refuse to stop or replace it.
5. When DSH CLI is missing, the app shall show an installation page and install a checksum-verified Node.js runtime plus the official `@deepseek-ai/dsh` npm package on explicit user action.
6. When the user enables start at sign-in, the app shall create a per-user startup shortcut; disabling it shall remove only that shortcut.
7. When the installer or uninstaller runs, it shall not require administrator rights and shall not modify `%USERPROFILE%\.dsh` or any selected workspace.
8. The app shall expose the current logs and allow opening the Web UI, log directory, and workspace.
9. When the app opens with DSH installed, it shall asynchronously compare the installed CLI version with npm's official latest version without blocking DSH startup.
10. When a newer version exists, the app shall allow one-click update only for its managed runtime; external installations shall remain read-only.

## Non-goals

- Bundling user DSH data or credentials.
- Code signing, online updates, Windows service mode, or multi-instance orchestration in version 1.
