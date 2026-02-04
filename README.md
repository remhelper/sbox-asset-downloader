# sbox2gltf

Download an S&box package (by `author/asset`) and export the primary `.vmdl_c` to glTF using [ValveResourceFormat](https://github.com/ValveResourceFormat/ValveResourceFormat).

## What it does

1. Fetch package JSON:
   - `https://services.facepunch.com/sbox/package/get/{author}.{asset}`
2. Parse `Version.ManifestUrl`
3. Fetch the manifest JSON
4. Download every file in `manifest.Files[]` into a local folder preserving the manifest `path`
5. Convert the primary model (`Version.Meta.PrimaryAsset` + `_c`, or first `.vmdl_c` in the manifest) to `.glb` (default) or `.gltf`

## Usage

```bash
# Default: exports mesh-only to avoid VRF physics exporter crashes
dotnet run --project src/Sbox2Gltf -- kvien/old_table01 --out out --format glb

# If you explicitly want physics export too (may crash on some assets)
dotnet run --project src/Sbox2Gltf -- kvien/old_table01 --out out --format glb --with-physics
```

Outputs:
- `out/kvien.old_table01/` (downloaded assets)
- `out/kvien.old_table01/kvien.old_table01_mesh.glb` (default, mesh-only/no physics)
- `out/kvien.old_table01/kvien.old_table01.glb` (with `--with-physics`)

Texture note:
- For `.glb` export, textures are embedded (VRF `SatelliteImages` is disabled).

## Notes / caveats

- This repo assumes a working .NET SDK is installed.
- Some models reference additional resources not present in the manifest; export may be incomplete if dependencies are missing.
- This is a starting point; you may want caching, CRC verification, retries, and dependency discovery.

## License

MIT (add one if you plan to publish).
