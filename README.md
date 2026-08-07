# AetherPress

AetherPress is a Dalamud plugin that detects completed Penumbra mod installations and offers to optimize their textures. It can resize and recompress Base/Diffuse, Normal, and Mask textures with configurable presets, smart format selection, and adaptive resolution.

## Install

Add this URL under **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```text
https://shaalaxiv.github.io/AetherPress/repo.json
```

Save the settings, open the Plugin Installer, and search for **AetherPress**. Use `/aetherpress` to open the plugin.

## Features

- Detects completed Penumbra mod installations through Penumbra IPC.
- Asks before optimizing by default; automatic optimization is opt-in.
- Includes Quality, Balanced, and Maximum Savings presets.
- Supports `.tex` and `.dds` Base/Diffuse, Normal, and Mask textures.
- Can protect skin texture redirects found in Penumbra metadata.
- Can adapt resolution to measured image detail.
- Tags processed mods so they are not optimized repeatedly.

## Important

AetherPress replaces eligible texture files inside the selected Penumbra mod folder. Keep backups of any mods whose original textures you may want to restore. The **Optimize Entire Penumbra Folder** action always asks for confirmation.

## Build

Building requires the Dalamud development environment and .NET 10:

```powershell
dotnet restore AetherPress.csproj --locked-mode
dotnet build AetherPress.csproj -c Release --no-restore
```

The packaged plugin is written to `bin/Release/AetherPress/latest.zip`.

## Releases

Update the version in `AetherPress.csproj` and `repo.json`, update the download links and changelog in `repo.json`, then create a matching `vX.Y.Z` tag. The release workflow builds and attaches `AetherPress-X.Y.Z.zip` to the GitHub release.

AetherPress is licensed under the MIT License. The bundled `texconv.exe` is from Microsoft DirectXTex and is redistributed under its MIT License; see `THIRD_PARTY_NOTICES.txt`.
