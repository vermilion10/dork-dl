# dork-dl

Asset downloader and extractor for a certain Unity game. Fan-made tool for research and personal archival.

End-to-end pipeline: resolves catalog bundles, mirrors from CDN with checksums/resuming, handles XOR deobfuscation and decompression, converts assets (PNGs, sliced sprites, MonoBehaviour JSON, raw text), and decodes CRI audio/video (WAV, MP4).

## Requirements

- .NET 8 SDK
- [uv](https://docs.astral.sh/uv/) for `audio` and `video` (spawns Python worker in a cached venv automatically, no manual pip step)
- [ffmpeg](https://ffmpeg.org/download.html) on `PATH` for `video` (muxes demuxed streams into MP4)
- Installed game for commands reading local files (`catalog`, `download`, and `extras` can run without one via CDN)

## Usage

```sh
dotnet build DorkDl.slnx

dork-dl list     --game-dir "D:/Games/Game" [filters...] [--nodes] [--stats]
dork-dl inspect  --game-dir "D:/Games/Game" <bundle> [--hashes]
dork-dl catalog  [--game-dir "D:/Games/Game"] [filters...] [--bundles] [--locations] [--deps] [--type T] [--stats] [--limit n]
dork-dl download [--game-dir "D:/Games/Game"] [filters...] [--type T] [--output dir] [--dry-run] [--limit n]
dork-dl extract --game-dir "D:/Games/Game" [filters...] [--type T] [--output dir] [--verify] [--limit n] [--concurrency n] [--memory MiB] [--kinds list]
dork-dl packs   --game-dir "D:/Games/Game" [filters...] [--rebuild] [--limit n]
dork-dl extras  [--game-dir "D:/Games/Game"] [filters...] [--output dir] [--groups] [--list]
dork-dl gff     --game-dir "D:/Games/Game" [filters...] [--file path] [--verify] [--limit n]
dork-dl names   --game-dir "D:/Games/Game" [filters...] [--index path] [--rebuild] [--type T] [--bundles n] [--memory MiB]
dork-dl audio   --game-dir "D:/Games/Game" [filters...] [--output dir] [--list] [--limit n] [--assets-dir dir]
dork-dl video   --game-dir "D:/Games/Game" [filters...] [--output dir] [--list] [--limit n] [--assets-dir dir]
```

- `--game-dir`: Accepts the install root or its data folder. Optional for `catalog`, `download`, `extras` (falls back to CDN); required for commands reading local files.
- `list`: Walks downloaded bundles. Positional filters are case-insensitive AND substring matches against bundle filenames and contained node names. `--nodes` lists internal files; `--stats` prints dataset totals.
- `inspect`: Dumps archives, keys, block tables, and file lists for one bundle (by filename or path). `--hashes` prints plain MD5 per archive.
- `catalog`: Reads Addressables `catalog_main_*.bin`. `--bundles` shows downloadable bundle hashes/sizes, `--locations` resolves asset keys, `--deps` shows source bundles, and `--stats` summarizes the catalog. Defaults to `--limit 50` (catalog has 267k keys; pass `--limit 0` for all).
- `extract`: Unpacks and converts bundles into `<output>/<bundle>/`. Textures become PNGs, text assets keep raw bytes, MonoBehaviours dump to JSON. `--raw` stops at Unity serialized files, `--keep-raw` writes both, and `--verify` decompresses blocks in memory without writing (fast install integrity check).
- `--kinds`: Narrows conversion to a comma-separated list (`Texture2D`, `Sprite`, `TextAsset`, `MonoBehaviour`). Checked before reading asset fields: `--kinds TextAsset` writes a few megabytes instead of writing ~95 GiB of textures.
- Concurrency & Parallelism: Defaults to 8 workers, bounded by disk and memory. `--memory` caps in-flight payload bytes (see [Memory](#memory)). On 400 bundles: 4m37s parallel vs ~45m single-threaded.
- `extras`: Downloads on-demand chapter voice packs and story cutscenes (see [On-demand downloads](#on-demand-downloads)). Unpacks into the loose asset layout expected by `--assets-dir` on `audio` and `video`:
  ```sh
  dork-dl extras --game-dir "..." time_chapter_09 --output output/extras
  dork-dl video  --game-dir "..." --assets-dir output/extras --output output/mp4
  ```
- `packs`: Resolves multi-archive pack files back to catalog bundle names using the GFF index (see [Packs](#packs)).
- `gff`: Inspects the GFF index; `--verify` deobfuscates every disk bundle to check against index plain hashes (see [The GFF index](#the-gff-index)).
- `names`: Resolves GUIDs to asset names (`m_Name`) via `AssetBundle` container maps (see [Asset names](#asset-names)).

Note: Contained bundle files are named `CAB-<hash>`, so `list` filters won't find assets by game name. Use `names` instead.

## Without an install

`catalog`, `download`, and `extras` need three things: the content catalog, the extras manifest, and the list of CDN version folders. The CDN serves all three, so running without `--game-dir` works directly:

```sh
dork-dl catalog --stats
dork-dl extras time_chapter_09 --output output/extras
dork-dl download --output output/mirror
```

- Version lookup: Unauthenticated launcher config endpoint at `https://cdn.invalid/flexible_config`. The payload is doubly encoded: `data.flexible_config.nexon_plug` is a JSON string holding another JSON object containing `ab_resource_version` (`2.7.4330.14393.27767`) and `ab_resource_addr` (CDN root).
- **The last two segments of `ab_resource_version` are the folder numbers**. Everything before is client version (e.g. `2.7.4330` can be 4 digits, so digit counts cannot distinguish them).
- Probing older folders: The launcher only names the current build's folders, but older bundles live in older folders (e.g. `v26669`). The full version column lives in `Version.txt` on the CDN under the current folder (byte-identical to local `Version_CustomDownload.txt`).
- Dry run test: Over a 60-bundle test, 6 were available standalone and 54 were not (28.13 MiB either way). The 54 are not missing; they live in [packs](#packs) served under the pack's name.
- Caching: Download metadata is cached in `--cache` (default `output/cache`) using HTTP ETags. Note: `catalog_main_*.bin` uses multipart ETags with a `-7` suffix, treated as a cache miss.
- Endpoints: Override with `--launcher-url` and `--base-url` (US region tested).

## Asset layout

Unity 2019.4.29f1, IL2CPP, Addressables. Downloaded bundles land in:

```
Game_Data/LocalCache/Data/AssetBundles_Windows/<md5>.bundle
```

alongside `catalog_main_<n>.bin` and `Version_CustomDownload.txt`. Complete install: ~7,900 bundles, 57 GiB.

CDN root format:

```
https://cdn.invalid/prod/AssetBundles_Windows/<abVersion>/<md5>.bundle
```

## The GFF index

One 235 MiB file in the bundle directory has a `.bundle` extension but starts with `GFF\0`. It indexes all 56,595 catalog bundles across three parallel arrays:

```
0x00  "GFF\0"
0x04  4-byte obfuscation key
0x08  entry count (repeated 3 times)
0x14  count x 12   index table: (index, index + 3703, 40) per row (dummy data)
...   count x 256  name records: 1-byte len (39), <md5>.bundle XORed with 4-byte key, 216 bytes random padding
...   count x 4096 hash blocks: 4 KiB zero block with two 16-byte hashes at offsets 8 and 32
EOF   8 zero bytes
```

Padding is why the file is 235 MiB just to store 2.2 MiB of names.
- Offset 8: bundle name parsed back to binary.
- Offset 32: **MD5 of the plain UnityFS bundle with obfuscation undone** (pre-XOR state).

`gff --verify` deobfuscates every indexed bundle on disk and compares hashes:

```
indexed   56595
on disk   7654
matched   7654
mismatch  0
```

7,654 / 7,654 matched, proving key derivation, bitwise inversion, the 0x8000 window, and per-archive restarts are completely accurate.

## Packs

The catalog names 56,595 bundles, but disk only has 7,912 files, and only 7,654 match catalog names. Asking the CDN for any of the other 48,941 returns 403.

The remaining 258 files on disk are **packs**: concatenated chains of complete UnityFS archives (186, 201+ per file). Small bundles (story scripts, UI prefabs, 3 KiB materials) are never shipped alone; they ride in packs. These 258 packs hold 48,799 catalog bundles, bringing total installed catalog bundles to 56,453 / 56,595.

Packs do not store catalog names internally. We match them using the GFF index's second hash: the plain archive MD5. `packs` hashes each sub-archive and matches:

```
files    7912
packs    258, holding 48799 archives
resolved 56453 catalog bundles
```

Building the index takes ~12s (writes 7.7 MB JSON). Afterwards, `packs <name>` locates where a bundle lives, `extract <name>` extracts just that archive into a catalog-named folder, and `names` maps entries under catalog names instead of `<pack>#<i>`.

`inspect --hashes` prints plain MD5s per archive (e.g. first archive of a 6 MB pack was 41.8 KiB, matching a 41.8 KiB story script bundle in the catalog).

### Selecting by type

Bundle names and asset keys are hashes, so resource type is the main human-readable identifier in the catalog. Pass a substring to `--type` on `catalog`, `download`, and `extract`:

```sh
dork-dl extract --game-dir "..." --type AdvImportBook --kinds MonoBehaviour
```

```
214 catalog bundles hold assets of type *AdvImportBook*
bundles  214
assets   2854
  MonoBehaviour 2854
```

`catalog --deps` shows the bundles a key's locations depend on.

## On-demand downloads

`Version_Ex1.bin` manifests assets fetched on demand: 2,360 entries in 49 groups (one per chapter).

Format: JSON encrypted with repeating XOR using **its own ASCII file name** (`Version_Ex1.bin`, 15 bytes) as the key. Plaintext is 7-bit ASCII, so all cipher bytes stay below `0x80`.

```json
{
  "Criware":     { "vo_chapter_14": ["KR187b…c5.gf;Assets\\ArtsResource\\…\\VO_Chapter_14_EP9_KR.acb", ...] },
  "Assetbundle": { "barrackanim":   ["527c125df629d63c1b191cc9a3da5885.bundle", ...] }
}
```

- `Criware` entry: `<remote name>;<relative path>`. Voice files use language prefixes (`CN`, `JP`, `KR`) rather than content hashes (manifest is required to locate them). Cutscenes use plain hashes.
- `.gf` files: standard zip holding one deflate-compressed file. Downloaded from normal bundle URL using version folder probing. Manifest covers 2,218 CRI files.

## Bundle obfuscation

UnityFS archive XORed with a 16-byte repeating key. Bytes 4..7 and 12..15 are zero, leaving `yFS\0` plaintext at offset 4.

Key derivation from the first 4 bytes (`header[0..4]` decrypts to `Unit`):

```
mask = header[0..4] ^ "Unit"

K[0..3]   = mask
K[4..7]   = 00 00 00 00
K[8..11]  = mask[0] ^ 0x02, mask[1], mask[2], mask[3]
K[12..15] = 00 00 00 00
```

`mask[0] ^ 0x02` on the second half-key is critical: without it, format version decodes as `0x02000007` instead of 7, and engine version as `2019.4,29f1` instead of `2019.4.29f1`.

- Bitwise inversion: Half of shipped archives are bitwise-inverted (`86 b9 ac ff` signature instead of `79 46 53 00`). Invert first (`^ 0xFF`), then derive mask. Split: 28,230 plain vs 28,223 inverted.
- No static secrets: Every archive provides its key in its first 4 bytes. Not derivable from filename.
- Multi-archive bundles: 256 files are concatenated archive chains (up to 201 archives in 8.8 MiB). Recognized by header size != file size. Repeating key and offsets restart at each archive boundary.
- Obfuscated Window (32 KiB): **Obfuscation covers only the first `0x8000` (32,768) bytes of an archive.** Everything past that is stored verbatim:

```
i <  0x8000 : plain[i] = (raw[i] ^ 0xFF if inverted) ^ K[i % 16]
i >= 0x8000 : plain[i] = raw[i]
```

Both inversion and XOR stop at `0x8000`. Small bundles (< 32 KiB) are fully obfuscated; large bundles only have header/block table scrambled. In multi-archive bundles, the window resets per archive.

## Audio

CRIWARE loose files in `LocalCache/Data/AssetBundles_Windows/Assets/ArtsResource/Audio/CriWare/*.acb, *.awb` (1,188 cue sheets, 735 streaming banks, ~7.9 GiB). Not obfuscated, readable names.

- HCA key: PC key `0x0` (vgmstream database). Override with `--key` for Android.
- `audio` decodes cue sheets to WAV via `tools/cri_worker` (PyCriCodecsEx via `uv`, msgpack IPC over stdio). Worker runs once per session and writes directly to disk to avoid piping gigabytes of audio through stdio.

```sh
dork-dl audio --game-dir "D:/Games/Game" --list AVG_Chapter
dork-dl audio --game-dir "D:/Games/Game" --output ./out AVG_Chapter0
```

## Video

125 `.usm` cutscenes in `Assets/Cutscene/Video/PC/` (2560x1440 H.264 @ 30 fps). Unencrypted `CRID` container and unencrypted HCA audio.

- `video` demuxes with cri_worker and muxes to MP4 using ffmpeg (copies H.264 stream, converts audio to AAC).
- Explicit stream mapping: Cutscenes carry multiple language tracks; ffmpeg requires explicit `-map` or it silently drops secondary tracks.
- No forced framerate: ffprobe reports `60/1` field rate, but H.264 SPS has true 30 fps timing which ffmpeg respects automatically.

```sh
dork-dl video --game-dir "D:/Games/Game" --list TIME_Chapter
dork-dl video --game-dir "D:/Games/Game" --output ./out TIME_Chapter_01
```

## Converting assets

AssetsTools.NET handles conversion using embedded type trees (no external class database needed).

- Textures: `m_StreamData.path` contains internal `archive:/CAB-<hash>/CAB-<hash>.resS` paths that fail on disk. The converter resolves sibling `.resS` files in the directory and reads raw pixel slices (DXT1, BC7, etc.). Asset `m_Name` survives and is used for output files.
- MonoBehaviours: Dumps field tree to JSON under `monobehaviours/`. Folds Unity's `Array`/`size` wrappers into clean JSON arrays, preserves `PPtr`s as `m_FileID`/`m_PathID`, decodes byte arrays as text (or base64 if binary), and formats NaN/Infinity as strings.
  - Utage 2D dialogue: Scenes are `AdvImportBook` spreadsheets (`Command`, `Arg1` to `Arg8`, `Text`, `Voice` columns) with rows for `Bg`, `Bgm`, `Character`, `Text`, `CharacterOff`, `EndScenario`. Text IDs link to `AvgContentData`. 2,850 scripts live in packs, parsed by a separate story viewer.
- Sprites: Sliced from texture atlases into `sprites/` subfolder using `m_RD.textureRect` coordinates. Subfolder avoids collisions with parent atlas textures. Atlases are decoded once per serialized file; bottom-up row order handled so only the cropped sprite is flipped.

## Memory

Parallel extraction memory footprint is kept low through two mechanisms:

- Targeted payload decompression: Buffer sizes are capped to blocks covering the requested header files, skipping trailing `.resS` textures. Prevents 300 MB allocations when indexing small headers in 270 MB bundles.
- In-flight memory budget: `extract` and `names` cap concurrent payload bytes under `--memory MiB` (default: 1/4 available RAM, clamped between 512 MiB and 4 GiB). Bundles reserve expected bytes before decompressing; oversized bundles wait for in-flight jobs to clear and run solo. Prevents out-of-memory errors on large bundles regardless of worker count.

## Asset names

Addressables uses opaque GUIDs (`id=0000d2c4...`). `names` indexes `AssetBundle` object `m_Container` maps (GUID -> `PPtr`) to fetch each asset's `m_Name`:

```
m_Container:
  55ec50041c7b6c74f901195a9fa4a940 -> pathId -8781556160229320458
                                       [Texture2D] s_slg_wallmetal_d01_n
```

```sh
dork-dl names --game-dir "..." Icon_Rogue2 --type Sprite
```

- Fast reader: Checks type tree and reads `m_Name` string directly from the asset stream without full deserialization. Decompression stops once the serialized file header is read.
- Full run over 7,912 files (56,453 catalog bundles): ~8 min with 4 workers, `--memory 1024`, 1.6 GiB peak RAM, finding 272,803 GUIDs across 379,570 assets.
- Resumable: Progress lines flush immediately to `asset-names.json.progress.jsonl`; reruns skip completed files.
- Benchmark: 400-file sample ran in 152s / 4.6 GiB before memory optimizations; 14s / 608 MiB after.

## Current coverage

From `list --stats` over a complete install:

```
bundle files read   7912
  multi-archive     256
  not fully parsed  0
unreadable files    1
archives            56453
files inside        71350
bytes on disk       57.26 GiB
bytes uncompressed  94.79 GiB
```

The single unreadable file is the GFF index (handled by `gff`). The pack index resolves 56,453 catalog bundles to files on disk. The name index covers all of them: 272,803 GUIDs naming 379,570 assets.

## Source layout

- `src/DorkDl.Core/Bundle`: Obfuscation, key derivation, UnityFS/GFF parsing, pack index, extraction
- `src/DorkDl.Core/Catalog`: Binary storage reader and Addressables catalog
- `src/DorkDl.Core/Download`: CDN client, launcher config, manifests, resumable downloads
- `src/DorkDl.Core/Extraction`: Parallel extraction pipeline and memory budget
- `src/DorkDl.Core/Unity`: AssetsTools.NET conversion, sprite slicer, MonoBehaviour JSON, GUID name index
- `src/DorkDl.CriInterop`: msgpack client for CRI worker, ffmpeg mux
- `tools/cri_worker`: Python CRI decoder (via `uv`)
- `src/DorkDl.Core/GameInstall.cs`: Local install file resolution
- `src/DorkDl.Cli`: Command line interface

## Not done yet

- **Packed bundles without an install.** The pack index requires local packs to hash; the CDN serves packs by pack names rather than catalog names. Install-free mode currently reaches only the 7,654 standalone bundles.
- **Bulk `video`.** Select cutscenes tested end to end; bulk conversion of all 125 files (7.9 GiB) has not been run.
- **Other regions.** Only the Global launcher endpoint and CDN have been tested.
