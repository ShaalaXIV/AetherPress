Ever install one cute outfit and discover it brought enough oversized textures to fill a small moon?

AetherPress keeps an eye on new Penumbra installs and offers to slim down their textures before they start eating your drive space and VRAM. Pick a Quality, Balanced, or Maximum Savings preset—or fine-tune base, normal, and mask textures yourself.

It supports smart texture formats, adaptive resolution, optional skin-texture protection, and automatic optimization if you’re feeling brave. By default, it asks before touching anything.

Use /aetherpress to open the settings.

AetherPress modifies textures inside the Penumbra mod folder, so keep backups of anything you may want to restore later.

## Install

Add this URL under **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```text
https://aethercast.org/repo
```

Update the version in `AetherPress.csproj` and `repo.json`, update the download links and changelog in `repo.json`, then create a matching `vX.Y.Z` tag. The release workflow builds and attaches `AetherPress-X.Y.Z.zip` to the GitHub release.

AetherPress is licensed under the MIT License. The bundled `texconv.exe` is from Microsoft DirectXTex and is redistributed under its MIT License; see `THIRD_PARTY_NOTICES.txt`.
