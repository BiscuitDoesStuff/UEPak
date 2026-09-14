# Unreal Engine Package Extractor (UEPak)

[![build](https://github.com/BiscuitDoesStuff/AESKeyTool/actions/workflows/build.yml/badge.svg)](https://github.com/BiscuitDoesStuff/AESKeyTool/actions/workflows/build.yml)

Command-line extractor and converter for Unreal Engine `.pak` archives, built directly on
[CUE4Parse](https://github.com/FabianFG/CUE4Parse) — the same library that powers
[FModel](https://github.com/4sval/FModel). It does headless, scriptable bulk work that is slow or
awkward through a GUI: decrypt and dump an entire game's files, convert everything to open formats,
export every mesh as glTF, decompile every Blueprint, or walk a level's full streaming graph into JSON.

> **What it is not.** This tool does **not** find or ship AES keys. You supply the key for the game
> you are working with. Extracted assets remain the property of their copyright holders; use this
> for modding, research, and personal projects in line with the game's terms.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A game's decrypted `Content/Paks` directory
- If the paks are encrypted: the game's AES-256 key as 64 hex characters
- For UE5 games, to load actual objects (textures, meshes, Blueprints, levels) rather than raw bytes:
  a `.usmap` mappings file for that game build. These are typically generated with
  [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) (`DumpUSMAP()`) or the
  [Dumper-7](https://github.com/Encryqed/Dumper-7) SDK generator. `list`, `export`, and `exportall`
  work without one.

## Build

```bash
dotnet build -c Release
```

The executable is at `bin/Release/net10.0/uepak.exe`.

## Install

Prebuilt Windows binaries are on the [Releases](../../releases) page — a single `uepak.exe`
(framework-dependent, needs the [.NET 10 runtime](https://dotnet.microsoft.com/download) installed).

## Usage

```
uepak --paks <dir> [--key <hex>] [--usmap <file>] [--ue <ver>] <command> [args]
```

| Option | Env var | Meaning |
|---|---|---|
| `--paks <dir>` | `UEPAK_PAKS` | Directory containing the `.pak` files (required) |
| `--key <hex>` | `UEPAK_KEY` | AES-256 key, 64 hex chars. Omit for unencrypted games |
| `--usmap <file>` | `UEPAK_USMAP` | `.usmap` type mappings |
| `--ue <ver>` | `UEPAK_UE` | Engine version: `UE5_5` (default), `UE4_27`, `UE5_3`, … |

Set the env vars once and every command becomes short:

```bash
export UEPAK_PAKS="C:/Games/MyGame/MyGame/Content/Paks"
export UEPAK_KEY=0123...abcd
export UEPAK_USMAP="C:/Games/MyGame/MyGame.usmap"
uepak list Weapons/
```

On startup the tool prints `Mounted: Files.Count=… UnloadedVfs=…`. A non-zero `UnloadedVfs` with
`Files.Count=0` means the archives are encrypted and the key is missing or wrong.

### Commands

**Browse / raw extraction**

| Command | Description |
|---|---|
| `list [filter]` | List asset paths, optionally filtered by case-insensitive substring |
| `export <assetPath> <outFile>` | Save one asset's raw (decrypted, decompressed) bytes |
| `exportall [outDir] [filter]` | Save every mounted file, preserving folder structure (default `GAMEDecrypted/`), optionally filtered by case-insensitive substring |

**Conversion** — uses CUE4Parse-Conversion's exporters, so output matches FModel's bulk export

| Command | Description |
|---|---|
| `exportconverted <outDir> [filter]` | Every file to its native format: textures → PNG, meshes → `.psk`/`.pskx` (ActorX), audio → `.wav`/`.ogg`/`.binka`, data assets/Blueprints/tables → JSON property dumps, loose files → raw copy. Resumable: re-running skips anything already converted |
| `exportgltf <outDir> [filter]` | Every static/skeletal mesh as a self-contained `.glb` (readable by UE5's Interchange importer with no plugins) |
| `exporttexture <assetPath> <outDir>` | Decode one texture to PNG, with no preview-size cap |
| `decompileblueprints <outDir> [filter]` | Every Blueprint class to readable pseudo-C++ (`.cpp` next to the original path) |
| `fillgaps <decryptedDir> <convertedDir>` | After `exportall` + `exportconverted`, guarantee every archive path has *something* in `convertedDir` (raw copy, or raw pak bytes for entries CUE4Parse cannot decode) |

**Dependency graph**

| Command | Description |
|---|---|
| `manifest <out.json> [filter]` | Build a package → imported-packages graph from each package's import table (type-agnostic: meshes → materials → textures, Blueprints → components, worlds → actors) |
| `bundle <manifest> <convertedDir> <outDir>` | For every mesh in `convertedDir`, copy its full dependency closure (materials, textures, skeleton, physics asset…) into one self-contained folder |

**Levels**

| Command | Description |
|---|---|
| `dumpexports <pkgPath>` | List a package's exports with their classes |
| `dumplevel <mapPath> [maxActors] [classFilter]` | Print actor exports and their properties from a `.umap` |
| `dumpplacements <mapPath> <out.json>` | Every directly-placed `StaticMeshActor`: mesh path + transform |
| `dumpworld <mapPath> <out.json> [meshOutDir]` | Walk the full streaming-level graph, merge each Blueprint actor's SCS component templates with its per-instance overrides, compose attach chains into world transforms, and write one JSON (actors, components, ISM instances, splines). Optionally also export every referenced mesh as USD with materials |
| `exportworld <mapPath> <outDir>` | CUE4Parse's built-in USD world exporter |

**Localization**

| Command | Description |
|---|---|
| `dumplocres [filter]` | Print localized strings (`[namespace] key = "value"`) |

Asset paths are pak-relative, e.g. `MyGame/Content/Maps/City/City` (extension optional).
`[filter]` arguments are case-insensitive substrings matched against the full path.

### Typical pipeline

```bash
uepak exportall      out/raw          # 1. raw decrypted files
uepak exportconverted out/converted   # 2. everything in open formats
uepak fillgaps       out/raw out/converted
uepak manifest       out/manifest.json
uepak bundle         out/manifest.json out/converted out/bundles
uepak exportgltf     out/gltf         # meshes for re-import into UE5
uepak decompileblueprints out/blueprints
uepak dumpworld MyGame/Content/Maps/City/City out/city.json out/city_meshes
```

Long-running commands print `PROGRESS:` lines and a final `DONE:` summary with ok/fail counts;
per-file failures are logged and skipped rather than aborting the run.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Usage error (bad/missing arguments, unknown command) |
| `2` | Runtime failure (mount failed, asset not found, decode error, or a bulk command had per-file failures) |

## Notes

- Pak archives are mounted from the top level of `--paks` only (no recursion).
- Entries that are both AES-encrypted *and* Oodle-compressed can fail to decode in CUE4Parse for some
  games (typically a handful of `.ini` files). `fillgaps` writes their raw bytes so the path exists.
- `dumpworld` exists because the USD world exporter can emit Blueprint-placed actors as empty
  transforms: cooked levels store only per-instance deltas, while the mesh reference and baked
  `PerInstanceSMData` live on the Blueprint's SCS component template. `dumpworld` merges the two.

## Credits

- [CUE4Parse](https://github.com/FabianFG/CUE4Parse) and CUE4Parse-Conversion — all parsing, decryption, and conversion
- [FModel](https://github.com/4sval/FModel) — the reference for what "convert everything" should produce

## License

MIT — see [LICENSE](LICENSE).
