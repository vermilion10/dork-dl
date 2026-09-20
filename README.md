# dork-dl

Asset downloader and extractor for a certain Unity game. Fan-made and not affiliated with the game's developers or publishers. For research and personal archival in accordance with game ToS.

Downloads bundles from CDN or extracts from local installs, deobfuscates and decompresses UnityFS archives, exports textures to PNG (with sprite atlas slicing) and text assets, and decodes CRI audio/video to WAV/MP4.

## Requirements

- .NET 8 SDK
- [uv](https://docs.astral.sh/uv/) (runs the Python CRI audio/video worker)
- [ffmpeg](https://ffmpeg.org/download.html) on `PATH` (for `video` MP4 remuxing)
- Game install (optional for `catalog`, `download`, and `extras`; required for local extraction)

## Usage

```sh
dotnet build DorkDl.slnx

dork-dl list --game-dir "D:/Games/Game" [filters...] [--nodes] [--stats]
dork-dl inspect --game-dir "D:/Games/Game" <bundle>
dork-dl catalog [--game-dir "D:/Games/Game"] [filters...] [--bundles] [--locations] [--stats] [--limit n]
dork-dl download [--game-dir "D:/Games/Game"] [filters...] [--output dir] [--dry-run] [--limit n]
dork-dl extract --game-dir "D:/Games/Game" [filters...] [--output dir] [--verify] [--limit n] [--concurrency n] [--kinds list]
dork-dl extras  [--game-dir "D:/Games/Game"] [filters...] [--output dir] [--groups] [--list]
dork-dl gff     --game-dir "D:/Games/Game" [filters...] [--verify] [--limit n]
dork-dl names   --game-dir "D:/Games/Game" [filters...] [--index path] [--rebuild] [--type T] [--bundles n]
dork-dl audio   --game-dir "D:/Games/Game" [filters...] [--output dir] [--list] [--limit n] [--assets-dir dir]
dork-dl video   --game-dir "D:/Games/Game" [filters...] [--output dir] [--list] [--limit n] [--assets-dir dir]
```

`--game-dir` accepts either the install root or its data directory inside it. It is optional for `catalog`, `download` and `extras`, which can fall back to the CDN. Every other command reads local files and needs it.

- `list`: walks downloaded bundles. Positional filters match bundle filenames and contained asset paths (AND-combined). `--nodes` lists internal files; `--stats` prints counts and byte totals.
- `inspect`: dumps archive headers, derived XOR keys, block tables, and file lists for a bundle (path or name).
- `catalog`: parses the Addressables binary catalog. `--bundles` lists downloadable bundles with hashes and sizes; `--locations` shows key-to-bundle mappings; `--stats` summarizes entry counts. Defaults to `--limit 50` (use `--limit 0` for all 267k+ keys).
- `extract`: deobfuscates, decompresses, and converts bundle assets into `<output>/<bundle>/`. Textures export to PNG, sprites are cropped, and text assets keep raw bytes.
  - `--raw`: stops after extracting raw Unity serialized files; `--keep-raw` writes both raw files and converted assets.
  - `--verify`: dry-run decompresses every block without writing to disk (fast install integrity check).
  - `--kinds`: comma-separated filter (`Texture2D`, `Sprite`, `TextAsset`) checked before reading (e.g. `--kinds TextAsset` extracts all text tables in megabytes without decoding 95 GiB of textures).
  - `--concurrency`: worker count (defaults to `Math.Clamp(cores, 1, 8)` to bound memory usage while decompressing archives in parallel).
- `extras`: downloads on-demand chapter voice packs and cutscenes (`Version_Ex1.bin`). `--groups` lists download groups; `--list` previews matching files; defaults to unpacking into the game's loose asset tree. Pass `--assets-dir` to `audio` or `video` to decode downloaded extras:
  ```sh
  dork-dl extras --game-dir "..." time_chapter_09 --output output/extras
  dork-dl video  --game-dir "..." --assets-dir output/extras --output output/mp4
  ```
- `gff`: parses the GFF index. `--verify` deobfuscates every bundle on disk and validates it against the pre-XOR MD5 in the index.
- `names`: indexes asset GUIDs from bundle `AssetBundle.m_Container` maps so assets named `CAB-<hash>` can be resolved by real name.

## Without an install

`catalog`, `download`, and `extras` can run without a local install by querying the CDN:

```sh
dork-dl catalog --stats
dork-dl extras time_chapter_09 --output output/extras
dork-dl download --output output/mirror
```

Version metadata comes from the unauthenticated launcher config endpoint:

```
https://cdn.invalid/flexible_config
```

The payload is double-encoded: `data.flexible_config.nexon_plug` is a JSON string containing an object with `ab_resource_version` (e.g. `2.7.4330.14393.27767`) and `ab_resource_addr` (CDN root). **The last two version components are the active CDN folder numbers.** The prefix (`2.7.4330`) is the client version.

Older bundles may reside in previous version folders not listed by the launcher (e.g. `v26669`). The full version mapping is stored in `Version.txt` on the CDN under the current folder (byte-identical to the local install's `Version_CustomDownload.txt`).

Downloads are cached in `--cache` (default `output/cache`) using HTTP ETags. `catalog_main_*.bin` uses a multipart ETag (`-7` suffix) and is re-downloaded if updated. Use `--launcher-url` and `--base-url` to target other regions (US is default).

## Asset layout

The game uses Unity 2019.4.29f1 (IL2CPP + Addressables). Downloaded bundles live outside `StreamingAssets/aa` in:

```
Game_Data/LocalCache/Data/AssetBundles_Windows/<md5>.bundle
```

The directory also contains `catalog_main_<n>.bin` and `Version_CustomDownload.txt`. A full install has ~7,900 bundles (~57 GiB).

CDN bundle URL structure:

```
https://cdn.invalid/prod/AssetBundles_Windows/<abVersion>/<md5>.bundle
```

## The GFF index

The bundle directory contains one 235 MiB non-Unity file named `<md5>.bundle` that begins with `GFF\0`. It indexes all 56,595 catalog bundles across three parallel arrays:

```
0x00  "GFF\0"
0x04  4-byte obfuscation key
0x08  entry count (repeated 3x)
0x14  count x 12   index table: (index, index + 3703, 40)
...   count x 256  name records: length byte (39) + XORed name + 216 padding bytes
...   count x 4096 hash blocks: 4 KiB block with two 16-byte hashes (offset 8 and 32)
EOF   8 zero bytes
```

In the hash block:
- Offset 8: binary representation of `<md5>.bundle`.
- **Offset 32: MD5 of the deobfuscated UnityFS archive** (matches the original archive before the publishing XOR step, not the file on disk or the catalog hash).

Running `gff --verify` deobfuscates local bundles and validates against this index:

```
indexed   56595
on disk   7654
matched   7654
mismatch  0
```

This confirms key derivation, bitwise inversion, the `0x8000` window, and multi-archive key restarts across all local files.

## On-demand downloads

`Version_Ex1.bin` manifests on-demand chapter content (2,360 files in 49 groups):

```
barrackanim                    142  142 .bundle
time_chapter_04                 13  13 .usm
vo_chapter_18                  132  66 .acb, 66 .awb
...
```

The manifest is JSON obfuscated with a repeating 15-byte ASCII XOR key matching its filename (`Version_Ex1.bin`):

```json
{
  "Criware":     { "vo_chapter_14": ["KR187b...c5.gf;Assets\\ArtsResource\\...\\VO_Chapter_14_EP9_KR.acb", ...] },
  "Assetbundle": { "barrackanim":   ["527c125df629d63c1b191cc9a3da5885.bundle", ...] }
}
```

- `Criware`: `<remote_name>;<relative_path>`. Voice files include language prefixes (`CN`, `JP`, `KR`) in the remote name.
- `.gf` files: standard ZIP archives containing a single deflate-compressed file placed at the manifest's relative path. Served from the bundle CDN path with the same version folder rules.

## Bundle obfuscation

Bundles are UnityFS archives obfuscated with a 16-byte repeating XOR key. Bytes 4..7 and 12..15 of the key are zero, leaving `yFS\0` visible at offset 4.

Key recovery from `UnityFS\0`:

```
mask = header[0..4] ^ "Unit"

K[0..3]   = mask
K[4..7]   = 00 00 00 00
K[8..11]  = mask[0] ^ 0x02, mask[1], mask[2], mask[3]
K[12..15] = 00 00 00 00
```

The `^ 0x02` on byte 8 is required: without it, the archive format version decodes as `0x02000007` instead of `7`, and engine revision as `2019.4,29f1` instead of `2019.4.29f1`.

Roughly half of all archives (28,223 of 56,453) are bitwise-inverted (`~byte`), showing signature `86 b9 ac ff` instead of `79 46 53 00`. Inversion is undone before deriving the mask. No external keys or GameAssembly extraction are needed.

### Multi-archive bundles

256 bundles contain multiple concatenated UnityFS archives (up to 201 archives in one 8.8 MiB bundle, expanding file count from 13,679 to 71,350). Detected when header declared size is smaller than total file size. Each sub-archive has its own key mask, inversion state, and resets XOR offsets to 0.

### Payload blocks

Obfuscation applies only to the first `0x8000` (32 KiB) of each archive. The remainder is plain data:

```
i <  0x8000 : plain[i] = (raw[i] ^ (inverted ? 0xFF : 0x00)) ^ K[i % 16]
i >= 0x8000 : plain[i] = raw[i]
```

The window covers headers and block tables (and small bundles entirely), but stops before large payloads. In multi-archive bundles, the `0x8000` window restarts at each archive boundary.

## Audio

Audio is stored as loose CRIWARE files outside bundles:

```
LocalCache/Data/AssetBundles_Windows/Assets/ArtsResource/Audio/CriWare/*.acb, *.awb
```

Totaling 1,188 cue sheets, 735 streaming banks, and 125 `.usm` cutscenes (~7.9 GiB).

Waveforms inside HCA are encrypted with the PC key from client.json. Override with `--key` for other platforms.

```sh
dork-dl audio --game-dir "D:/Games/Game" --list AVG_Chapter
dork-dl audio --game-dir "D:/Games/Game" --output ./out AVG_Chapter0
```

Decoded via `tools/cri_worker` (Python process over msgpack stdin/stdout). File paths are passed directly so decoded WAVs write to disk without streaming gigabytes over stdio pipes.

## Video

125 `.usm` cutscenes live under `Assets/Cutscene/Video/PC/` (2560x1440 H.264 @ 30 fps). Videos are unencrypted (`CRID` header).

`video` demuxes USM streams through the worker and remuxes into MP4 with ffmpeg (stream-copying H.264 and encoding audio to AAC):

```sh
dork-dl video --game-dir "D:/Games/Game" --list TIME_Chapter
dork-dl video --game-dir "D:/Games/Game" --output ./out TIME_Chapter_01
```

Muxing details:
- **Explicit stream mapping (`-map`)**: ffmpeg defaults drop extra audio streams; explicit mapping preserves multi-language cutscene audio tracks.
- **No `-framerate` override**: raw stream probe reports field rate (60 fps), but H.264 SPS metadata specifies the true 30 fps timing which ffmpeg respects automatically.

## Converting assets

Uses AssetsTools.NET with embedded type trees.

- **`.resS` external textures**: `m_StreamData.path` contains bundle-internal paths (`archive:/CAB-<hash>/CAB-<hash>.resS`) that fail after extraction. The extractor resolves sibling `.resS` files on disk and reads texture data ranges directly (DXT1, BC7, etc.).
- **Sprite slicing**: sprites are exported to `sprites/` subdirectories to prevent filename collisions with parent atlases.
  - Crops use `m_RD.textureRect` (bottom-up coordinates). Atlases are decoded, cropped, and flipped once.
  - Atlas decodes are cached per serialized file.
  - UI atlases use unrotated rectangle packing (`m_RD.settingsRaw` rotation handling is supported but unused in sampled game assets).

## Asset names

Catalog addresses use GUIDs (`id=0000d2c42ca422648b8fa6fd08e2879a`) without human-readable names. However, bundle `AssetBundle.m_Container` maps GUIDs to path IDs whose objects contain real `m_Name` values (e.g. `[Texture2D] s_slg_wallmetal_d01_n`).

`names` scans bundle headers and serialized metadata (skipping `.resS` streams) to build a JSON lookup index:

```sh
dork-dl names --game-dir "..." Icon_Rogue2 --type Sprite
```

Use `--bundles n` to index a subset for quick testing.

## Current coverage

From `list --stats` on a complete install:

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

The single unreadable file is the 235 MiB GFF index (handled by `gff`).

## Source layout

- `src/DorkDl.Core/Bundle`: bundle obfuscation, key derivation, UnityFS/GFF parsing, and extraction
- `src/DorkDl.Core/Catalog`: `BinaryStorageBuffer` reader and Addressables catalog parser
- `src/DorkDl.Core/Download`: CDN client, launcher config, resumable download index, and manifests
- `src/DorkDl.Core/Extraction`: parallel bundle unpacking and asset conversion
- `src/DorkDl.Core/Unity`: AssetsTools.NET interop, texture decoding, sprite slicing, and GUID name index
- `src/DorkDl.CriInterop`: msgpack client for Python worker and ffmpeg video muxing
- `tools/cri_worker`: Python CRI audio/video decoder run through `uv`
- `src/DorkDl.Core/GameInstall.cs`: install folder layout resolver
- `src/DorkDl.Cli`: System.CommandLine interface

## Not done yet

- **Full GUID name index**: indexing mechanism works; full run across all 7,912 bundles pending completion.
- **`MonoBehaviour` assets**: unextracted AVG story scripts (~140k dialogue lines needed by a separate story viewer).
- **Bulk video conversion**: end-to-end verified on sample cutscenes; bulk processing of all 125 files (~7.9 GiB) pending.
- **Additional regions**: currently tested against US endpoints.
