---
name: dsh-web-launcher-release
description: Publish DSH Web Launcher releases to GitHub. Use when the user asks to bump the version, update README screenshots, build a clean x64 package, create the setup installer, push the repo, or create/update a GitHub release for this project.
---

# DSH Web Launcher GitHub Release

Project root: `D:\Documents\ChatGPT\TXX-CHECK\DSH-WEB-MANAGER`
GitHub repo: `https://github.com/LVSUGARS/dsh-web-launcher`
Remote branch: `main`

## Safety Contract

- Do not modify/touch `.dsh`, workspace data, user project files, or `secrets.json`.
- Do not modify the formal installed version under `%LOCALAPPDATA%\Programs\DSH Web Launcher`.
- Only stop dev build processes whose path matches `build-v2\win-x64` when files are locked.
- Build outputs (`build/`, `build-v2/`, `release/`, `release-v2/`) are git-ignored; do not add them to a commit.
- Never push without the user's clear release request. If authentication/network fails, retry with `-c http.version=HTTP/1.1`.

## Workflow

1. **Determine version.** Usually bump patch: `2.0.1` -> `2.0.2`.
2. **Update version strings** in:
   - `src-v2/DshLauncher.App/DshLauncher.App.csproj` (`Version`, `AssemblyVersion`, `FileVersion`)
   - `Build-V2.ps1` (`$version = '...'`)
   - `installer/Setup-V2.cs` (`AssemblyVersion`, `AssemblyFileVersion`)
   - `src-v2/DshLauncher.App/MainWindow.xaml` (splash version text `v2.0.x · LVSUGARS`)
   - `installer/Install.ps1` default `$ProductVersion` (optional)
3. **Refresh README screenshots** if visuals changed:
   - Launch dev build: `build-v2\win-x64\DSHWebLauncher.exe`.
   - Wait for particles to settle (~6s), capture splash to `assets/screenshots/splash-particle.png`.
   - Send `Enter`, wait, capture main to `assets/screenshots/main-console-dark.png`.
   - Keep `README.md` and `README.en.md` links pointing to these files.
4. **Build clean x64 package** (single-file self-contained, no PDB, direct run on x64):
   - `dotnet publish src-v2/DshLauncher.App/DshLauncher.App.csproj -c Release -r win-x64 --self-contained true -o build-v2/single-win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
   - Recreate `build-v2/package-win-x64` with the exe + scripts from `installer/`:
     - `Install.ps1`, `Uninstall.ps1`, `Install-DshRuntime.ps1`, `UpdatePackage.ps1`, `Install.cmd`
5. **Create release artifacts**:
   - Compress `build-v2/package-win-x64` to `release-v2/DSH-Web-Launcher-<version>-clean-win-x64.zip`.
   - Create `.sha256`.
   - Compile setup:
     - Copy zip payload to `build-v2/payload-clean.zip`.
     - Use `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` against `installer/Setup-V2.cs` with `/resource:build-v2\payload-clean.zip,payload.zip` and output `release-v2/DSH-Web-Launcher-Setup-<version>.exe`.
6. **Run package self-test**:
   - `build-v2/package-win-x64/DSHWebLauncher.exe --self-test` -> exit 0.
7. **Commit and push**:
   - `git add` source/docs/assets (not build outputs).
   - `git commit -m "release: v<version> ..."`
   - `git push origin main`; if connection reset, use `git -c http.version=HTTP/1.1 push origin main`.
8. **Create GitHub Release**:
   - `gh release create v<version> --repo LVSUGARS/dsh-web-launcher --title "DSH Web Launcher <version>" --notes "..." <zip> <sha256> <setup>`
   - If multi-file upload fails, create the release first with one asset, then `gh release upload v<version> --repo LVSUGARS/dsh-web-launcher <remaining files>`.
9. **Verify** the release page and assets exist:
   - `gh release view v<version> --repo LVSUGARS/dsh-web-launcher --json name,tagName,assets`.

## Reference

- See `references/release-runbook.md` for exact commands and copy-paste snippets.