# Release Runbook

Exact commands from the project root.

## 1. Bump version
```powershell
# replace 2.0.1 with the next version
```

## 2. Build dev (if screenshots needed)
```powershell
dotnet publish src-v2/DshLauncher.App/DshLauncher.App.csproj -c Release -r win-x64 --self-contained true -o build-v2/win-x64 --no-restore
```

## 3. Capture screenshots
- Splash: wait 6s after launch, capture window to `assets/screenshots/splash-particle.png`.
- Main: send Enter, wait, capture to `assets/screenshots/main-console-dark.png`.

## 4. Build single-file package
```powershell
dotnet publish src-v2/DshLauncher.App/DshLauncher.App.csproj -c Release -r win-x64 --self-contained true -o build-v2/single-win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 5. Assemble package folder
```powershell
$pkg = "build-v2/package-win-x64"
Remove-Item $pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item $pkg -ItemType Directory | Out-Null
Copy-Item build-v2/single-win-x64/DSHWebLauncher.exe $pkg
Copy-Item installer/Install.ps1,installer/Uninstall.ps1,installer/Install-DshRuntime.ps1,installer/UpdatePackage.ps1,installer/Install.cmd $pkg
```

## 6. Zip and setup
```powershell
Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath "release-v2/DSH-Web-Launcher-<version>-clean-win-x64.zip" -CompressionLevel Optimal
Get-FileHash "release-v2/DSH-Web-Launcher-<version>-clean-win-x64.zip" -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $($_.Path)" } | Set-Content "release-v2/DSH-Web-Launcher-<version>-clean-win-x64.zip.sha256"
Copy-Item "release-v2/DSH-Web-Launcher-<version>-clean-win-x64.zip" "build-v2/payload-clean.zip" -Force
csc.exe /nologo /target:winexe /optimize+ "/win32icon:assets/dsh-whale.ico" "/out:release-v2/DSH-Web-Launcher-Setup-<version>.exe" /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:build-v2/payload-clean.zip,payload.zip" installer/Setup-V2.cs
```

## 7. Push
```powershell
git add README.md README.en.md installer/Install.ps1 installer/Setup-V2.cs installer/UpdatePackage.ps1 Build-V2.ps1 src-v2 assets/screenshots specs/v2-launcher-upgrade
git commit -m "release: v<version> ..."
git push origin main
# if reset:
git -c http.version=HTTP/1.1 push origin main
```

## 8. Release
```powershell
gh release create v<version> --repo LVSUGARS/dsh-web-launcher --title "DSH Web Launcher <version>" --notes "..." "release-v2/DSH-Web-Launcher-<version>-clean-win-x64.zip" "release-v2/DSH-Web-Launcher-<version>-clean-win-x64.zip.sha256" "release-v2/DSH-Web-Launcher-Setup-<version>.exe"
```