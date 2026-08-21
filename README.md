# DSH Web Manager

Windows desktop manager for the official DeepSeek Harness (DSH) Web UI.

The application and installer use the black whale from the installed official DSH Web `favicon.svg` as their Windows icon.

## Install

Run `release\DSH-Web-Manager-Setup-1.1.1.exe`. Installation is per user and does not require administrator rights. A portable ZIP is provided beside it.

When the official DSH CLI is missing, the manager offers one-click installation of a checksum-verified portable Node.js runtime and the official `@deepseek-ai/dsh` npm package. It never bundles another user's `%USERPROFILE%\.dsh`, credentials, or conversations.

## First Run

1. Select a workspace folder.
2. Keep the default port `3080` unless it conflicts with another program.
3. Select **启动**, then **打开网页**.
4. Enable Windows sign-in startup only when desired.

The manager checks npm's official latest DSH version asynchronously. Managed installations can be updated in place; external installations are reported but never modified.

Closing the manager window does not stop DSH Web. Use **停止** to stop the verified instance.

## Data Boundaries

- Configuration/state/logs: `%LOCALAPPDATA%\DSHWebManager`
- Program files: `%LOCALAPPDATA%\Programs\DSH Web Manager`
- DSH user data: `%USERPROFILE%\.dsh` (never deleted by this app)
- Selected workspaces: never deleted by this app

The installer is unsigned, so Windows SmartScreen may display an unknown-publisher warning. A trusted code-signing certificate is required to remove that warning for public distribution.
