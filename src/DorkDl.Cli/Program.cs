using System.CommandLine;
using DorkDl.Core;
using DorkDl.Core.Bundle;
using DorkDl.Core.Catalog;
using DorkDl.Core.Download;
using DorkDl.Core.Extraction;
using DorkDl.Core.Unity;
using DorkDl.CriInterop;

namespace DorkDl.Cli;

// Written for System.CommandLine 3.0-preview.7 (uses SetAction, Options.Add, and Parse(args).InvokeAsync instead of 2.0 beta patterns).
public static class Program
{
    // Stashed here so ResolveSourceAsync can grab it without plumbing it through every subcommand.
    private static Option<string?>? LauncherUrlOption;

    public static async Task<int> Main(string[] args)
    {
        var gameDirOption = new Option<string>("--game-dir")
        {
            Description = "Path to the installed game, either its root directory or its data directory",
            // Needs to be recursive or subcommands crash when reading it. Not required since catalog/download/extras can run off CDN alone.
            Recursive = true,
        };

        var cacheOption = new Option<string>("--cache")
        {
            Description = "Where to keep the catalog and manifest downloaded when there is no install",
            DefaultValueFactory = _ => Path.Combine("output", "cache"),
            Recursive = true,
        };

        var launcherOption = new Option<string?>("--launcher-url")
        {
            Description = $"Launcher config endpoint, which is per-region (default: {LauncherSource.DefaultConfigUrl})",
            Recursive = true,
        };

        var root = new RootCommand("Download and extract game assets.");
        root.Options.Add(gameDirOption);
        root.Options.Add(cacheOption);
        root.Options.Add(launcherOption);
        LauncherUrlOption = launcherOption;
        root.Subcommands.Add(BuildListCommand(gameDirOption));
        root.Subcommands.Add(BuildInspectCommand(gameDirOption));
        root.Subcommands.Add(BuildCatalogCommand(gameDirOption, cacheOption));
        root.Subcommands.Add(BuildDownloadCommand(gameDirOption, cacheOption));
        root.Subcommands.Add(BuildExtractCommand(gameDirOption));
        root.Subcommands.Add(BuildAudioCommand(gameDirOption));
        root.Subcommands.Add(BuildVideoCommand(gameDirOption));
        root.Subcommands.Add(BuildExtrasCommand(gameDirOption, cacheOption));
        root.Subcommands.Add(BuildGffCommand(gameDirOption));
        root.Subcommands.Add(BuildNamesCommand(gameDirOption));

        return await root.Parse(args).InvokeAsync();
    }

    private static Command BuildListCommand(Option<string> gameDirOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the bundle or contained file names, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var nodesOption = new Option<bool>("--nodes")
        {
            Description = "List the files inside each bundle, not just the bundle itself",
        };

        var statsOption = new Option<bool>("--stats")
        {
            Description = "Print a summary of the whole bundle set instead of listing bundles",
        };

        var command = new Command("list", "List downloaded bundles and the files they contain.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(nodesOption);
        command.Options.Add(statsOption);

        command.SetAction(parse =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var filters = parse.GetValue(filtersArgument) ?? [];
            var showNodes = parse.GetValue(nodesOption);
            var showStats = parse.GetValue(statsOption);

            var paths = install.EnumerateBundles().ToList();
            if (paths.Count == 0)
            {
                Console.Error.WriteLine($"No bundles found in {install.BundleDirectory}");
                return 1;
            }

            var stats = new ScanStatistics();
            var matched = 0;

            foreach (var path in paths)
            {
                BundleFile bundle;
                try
                {
                    bundle = BundleReader.Read(path);
                }
                catch (BundleFormatException e)
                {
                    stats.RecordFailure(e.Message);
                    continue;
                }

                stats.Record(bundle);

                if (showStats || !Matches(bundle, filters))
                {
                    continue;
                }

                matched++;
                Console.WriteLine(Describe(bundle));
                if (showNodes)
                {
                    foreach (var node in bundle.Nodes)
                    {
                        Console.WriteLine($"    {node.Path}  {FormatSize(node.Size)}");
                    }
                }
            }

            if (showStats)
            {
                stats.Print(Console.Out);
            }
            else
            {
                Console.Error.WriteLine($"{matched} of {paths.Count} bundles matched.");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildInspectCommand(Option<string> gameDirOption)
    {
        var bundleArgument = new Argument<string>("bundle")
        {
            Description = "Bundle file name, with or without the .bundle suffix, or a full path",
        };

        var command = new Command("inspect", "Show the archives, block tables and file lists of one bundle.");
        command.Arguments.Add(bundleArgument);

        command.SetAction(parse =>
        {
            var name = parse.GetRequiredValue(bundleArgument);
            var path = ResolveBundlePath(parse.GetValue(gameDirOption), name);
            if (path is null)
            {
                Console.Error.WriteLine($"Could not find bundle '{name}'");
                return 1;
            }

            var bundle = BundleReader.Read(path);

            Console.WriteLine(bundle.FileName);
            Console.WriteLine($"  on disk      {FormatSize(bundle.FileSize)}");
            Console.WriteLine($"  archives     {bundle.Archives.Count}"
                              + (bundle.FullyParsed
                                  ? ", accounting for the whole file"
                                  : $", accounting for {FormatSize(bundle.BytesAccountedFor)} of {FormatSize(bundle.FileSize)}"));
            if (bundle.TrailingDataReason is { } reason)
            {
                Console.WriteLine($"  stopped at   {reason}");
            }

            foreach (var (archive, index) in bundle.Archives.Select((a, i) => (a, i)))
            {
                var header = archive.Header;
                Console.WriteLine();
                Console.WriteLine($"  archive {index} at offset {archive.ArchiveOffset}, {FormatSize(archive.ArchiveSize)}");
                Console.WriteLine($"    key        mask {archive.Mask:x8}{(archive.Inverted ? ", inverted" : "")}");
                Console.WriteLine($"    unity      {header.UnityRevision} (format {header.FormatVersion}, {header.UnityVersion})");
                Console.WriteLine($"    flags      0x{header.Flags:x}, block table {header.BlocksInfoCompression}"
                                  + (header.BlocksInfoAtEnd ? ", stored at end" : ""));
                Console.WriteLine($"    payload    {archive.Blocks.Count} blocks at offset {archive.PayloadOffset}, "
                                  + $"{FormatSize(archive.UncompressedPayloadSize)} uncompressed");

                Console.WriteLine($"    files ({archive.Nodes.Count})");
                foreach (var node in archive.Nodes)
                {
                    Console.WriteLine($"      {node.Path}  offset {node.Offset}  {FormatSize(node.Size)}");
                }
            }

            return 0;
        });

        return command;
    }

    private static Command BuildCatalogCommand(Option<string> gameDirOption, Option<string> cacheOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the key name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var bundlesOption = new Option<bool>("--bundles")
        {
            Description = "List the downloadable bundles with their content hash and size",
        };

        var statsOption = new Option<bool>("--stats")
        {
            Description = "Summarise the catalog instead of listing it",
        };

        var locationsOption = new Option<bool>("--locations")
        {
            Description = "Show each key's locations, with resource type and provider",
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Stop after this many matches; 0 means no limit",
            DefaultValueFactory = _ => 50,
        };

        var baseUrlOption = new Option<string?>("--base-url")
        {
            Description = $"CDN base URL when there is no install (default: from the launcher)",
        };

        var command = new Command("catalog", "Read the Addressables content catalog.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(bundlesOption);
        command.Options.Add(statsOption);
        command.Options.Add(locationsOption);
        command.Options.Add(limitOption);
        command.Options.Add(baseUrlOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var source = await ResolveSourceAsync(
                parse, gameDirOption, cacheOption, baseUrlOption, http, cancellationToken);

            var path = source.CatalogPath;
            var catalog = ContentCatalog.Load(path);
            var filters = parse.GetValue(filtersArgument) ?? [];
            var limit = parse.GetValue(limitOption);
            var showBundles = parse.GetValue(bundlesOption);
            var showLocations = parse.GetValue(locationsOption);

            Console.Error.WriteLine($"{Path.GetFileName(path)}: {catalog.LocatorId}, {catalog.KeyCount} keys");

            if (parse.GetValue(statsOption))
            {
                PrintCatalogStats(catalog, parse.GetValue(gameDirOption));
                return 0;
            }

            var shown = 0;
            foreach (var key in catalog.Keys())
            {
                if (showBundles && !key.IsBundleKey)
                {
                    continue;
                }

                if (filters.Length > 0
                    && !filters.All(f => key.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (showBundles)
                {
                    var options = catalog.Locations(key).Select(l => l.BundleOptions).FirstOrDefault(o => o is not null);
                    Console.WriteLine(options is null
                        ? key.Name
                        : $"{key.Name}  {FormatSize(options.BundleSize),10}  hash {options.Hash}  crc {options.Crc}");
                }
                else
                {
                    Console.WriteLine(key.Name);
                }

                if (showLocations)
                {
                    foreach (var location in catalog.Locations(key))
                    {
                        Console.WriteLine($"    {location.ResourceType ?? "?"}  id={location.InternalId}");
                    }
                }

                if (limit > 0 && ++shown >= limit)
                {
                    Console.Error.WriteLine($"stopped at --limit {limit}");
                    break;
                }
            }

            return 0;
        });

        return command;
    }

    private static Command BuildVideoCommand(Option<string> gameDirOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the video name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write MP4s into",
            DefaultValueFactory = _ => "output",
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Stop after this many videos; 0 means no limit",
            DefaultValueFactory = _ => 0,
        };

        var listOption = new Option<bool>("--list")
        {
            Description = "List the videos that would be converted, without converting them",
        };

        var keepStreamsOption = new Option<bool>("--keep-streams")
        {
            Description = "Keep the demuxed elementary streams beside the MP4",
        };

        var assetsDirOption = new Option<string?>("--assets-dir")
        {
            Description = "Read videos from this tree instead of the install, such as an `extras` download",
        };

        var command = new Command("video", "Convert CRI USM cutscenes to MP4.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(listOption);
        command.Options.Add(keepStreamsOption);
        command.Options.Add(assetsDirOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var filters = parse.GetValue(filtersArgument) ?? [];
            var output = parse.GetValue(outputOption)!;
            var limit = parse.GetValue(limitOption);
            var keepStreams = parse.GetValue(keepStreamsOption);

            var assetsDirectory = parse.GetValue(assetsDirOption);
            var videos = install.EnumerateVideos(assetsDirectory)
                .Where(p => filters.Length == 0
                            || filters.All(f => Path.GetFileName(p).Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (videos.Count == 0)
            {
                Console.Error.WriteLine($"No .usm found under {assetsDirectory ?? install.LooseAssetDirectory}");
                return 1;
            }

            if (parse.GetValue(listOption))
            {
                foreach (var video in videos)
                {
                    Console.WriteLine($"{Path.GetFileName(video),-56} {FormatSize(new FileInfo(video).Length),10}");
                }

                Console.Error.WriteLine($"{videos.Count} videos");
                return 0;
            }

            if (!VideoMuxer.IsAvailable())
            {
                Console.Error.WriteLine("ffmpeg was not found on PATH; it is required to mux the MP4s.");
                return 1;
            }

            Console.Error.WriteLine($"{videos.Count} videos, writing to {output}");
            using var worker = CriWorkerClient.Start();

            var done = 0;
            var failures = 0;
            long bytes = 0;

            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(video);
                var streamDirectory = Path.Combine(output, ".streams", name);
                var target = Path.Combine(output, name + ".mp4");

                try
                {
                    var demuxed = await worker.DemuxUsmAsync(video, streamDirectory, cancellationToken);
                    VideoMuxer.Mux(demuxed, target);
                    done++;
                    bytes += new FileInfo(target).Length;
                    Console.Error.WriteLine(
                        $"  {name,-46} {demuxed.Width}x{demuxed.Height}  "
                        + $"{(demuxed.AudioPaths?.Count ?? 0)} audio  {FormatSize(new FileInfo(target).Length)}");
                }
                catch (Exception e) when (e is CriWorkerException or InvalidOperationException)
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL {name}: {e.Message}");
                }
                finally
                {
                    if (!keepStreams && Directory.Exists(streamDirectory))
                    {
                        try
                        {
                            Directory.Delete(streamDirectory, recursive: true);
                        }
                        catch (IOException)
                        {
                            // Don't fail the whole run just because a temp file wouldn't delete.
                        }
                    }
                }

                if (limit > 0 && done + failures >= limit)
                {
                    break;
                }
            }

            if (!keepStreams)
            {
                var streamRoot = Path.Combine(output, ".streams");
                if (Directory.Exists(streamRoot) && !Directory.EnumerateFileSystemEntries(streamRoot).Any())
                {
                    Directory.Delete(streamRoot);
                }
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine($"videos   {done}");
            Console.Error.WriteLine($"bytes    {FormatSize(bytes)}");
            Console.Error.WriteLine($"failures {failures}");
            return failures == 0 ? 0 : 1;
        });

        return command;
    }

    private static Command BuildNamesCommand(Option<string> gameDirOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the GUID or the asset name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var indexOption = new Option<string>("--index")
        {
            Description = "Where the index lives",
            DefaultValueFactory = _ => Path.Combine("output", "asset-names.json"),
        };

        var rebuildOption = new Option<bool>("--rebuild")
        {
            Description = "Rebuild the index even if it already exists",
        };

        var typeOption = new Option<string?>("--type")
        {
            Description = "Only show assets of this Unity class, such as Texture2D",
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Stop after this many matches; 0 means no limit",
            DefaultValueFactory = _ => 50,
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Bundles to read in parallel while building",
            DefaultValueFactory = _ => BundleExtraction.DefaultConcurrency,
        };

        var bundlesOption = new Option<int>("--bundles")
        {
            Description = "Only index this many bundles, for a quick look; 0 means all of them",
            DefaultValueFactory = _ => 0,
        };

        var command = new Command("names", "Resolve asset GUIDs to the names the bundles give them.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(indexOption);
        command.Options.Add(rebuildOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(bundlesOption);

        command.SetAction((parse, cancellationToken) =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var index = parse.GetValue(indexOption)!;

            if (parse.GetValue(rebuildOption) || !File.Exists(index))
            {
                var take = parse.GetValue(bundlesOption);
                var sources = take > 0 ? install.EnumerateBundles().Take(take) : install.EnumerateBundles();

                Console.Error.WriteLine($"building {index} from {install.BundleDirectory}");
                var log = new SynchronousProgress(Console.Error.WriteLine);
                var report = AssetNameIndex.Build(
                    sources,
                    index,
                    parse.GetValue(concurrencyOption),
                    log,
                    cancellationToken);

                Console.Error.WriteLine();
                Console.Error.WriteLine($"bundles  {report.Bundles}");
                Console.Error.WriteLine($"guids    {report.Guids}");
                Console.Error.WriteLine($"assets   {report.Assets}");
                Console.Error.WriteLine($"failures {report.Failures}");
                Console.Error.WriteLine();
            }

            var names = AssetNameIndex.Load(index);
            var filters = parse.GetValue(filtersArgument) ?? [];
            var type = parse.GetValue(typeOption);
            var limit = parse.GetValue(limitOption);

            var shown = 0;
            foreach (var (guid, assets) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                foreach (var asset in assets)
                {
                    if (type is not null && !asset.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (filters.Length > 0
                        && !filters.All(f => guid.Contains(f, StringComparison.OrdinalIgnoreCase)
                                             || asset.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    Console.WriteLine($"{guid}  {asset.Type,-24} {asset.Name}  [{asset.Bundle}]");
                    if (limit > 0 && ++shown >= limit)
                    {
                        Console.Error.WriteLine($"stopped at --limit {limit}");
                        return Task.FromResult(0);
                    }
                }
            }

            Console.Error.WriteLine($"{shown} matches of {names.Count} GUIDs");
            return Task.FromResult(0);
        });

        return command;
    }

    private static Command BuildGffCommand(Option<string> gameDirOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the bundle name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var verifyOption = new Option<bool>("--verify")
        {
            Description = "Deobfuscate every bundle present on disk and check it against the index hash",
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Stop after this many entries; 0 means no limit",
            DefaultValueFactory = _ => 0,
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Bundles to hash in parallel when verifying",
            DefaultValueFactory = _ => BundleExtraction.DefaultConcurrency,
        };

        var command = new Command("gff", "Read the GFF bundle index and check it against the install.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(verifyOption);
        command.Options.Add(limitOption);
        command.Options.Add(concurrencyOption);

        command.SetAction((parse, cancellationToken) =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var path = install.FindGffIndex();
            if (path is null)
            {
                Console.Error.WriteLine($"No GFF container found in {install.BundleDirectory}");
                return Task.FromResult(1);
            }

            var gff = GffContainer.Load(path);
            var filters = parse.GetValue(filtersArgument) ?? [];
            var limit = parse.GetValue(limitOption);

            var entries = gff.Entries
                .Where(e => filters.Length == 0
                            || filters.All(f => e.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
            if (limit > 0)
            {
                entries = entries.Take(limit);
            }

            var selected = entries.ToList();
            Console.Error.WriteLine($"{Path.GetFileName(path)}: {gff.Entries.Count} bundles indexed");

            if (!parse.GetValue(verifyOption))
            {
                foreach (var entry in selected)
                {
                    Console.WriteLine($"{entry.Name}  plain {Convert.ToHexString(entry.PlainHash).ToLowerInvariant()}");
                }

                Console.Error.WriteLine($"{selected.Count} entries");
                return Task.FromResult(0);
            }

            var present = 0;
            var matched = 0;
            var failed = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parse.GetValue(concurrencyOption)),
                CancellationToken = cancellationToken,
            };

            Parallel.ForEach(selected, parallelOptions, entry =>
            {
                var bundlePath = Path.Combine(install.BundleDirectory, entry.Name);
                if (!File.Exists(bundlePath))
                {
                    return;
                }

                Interlocked.Increment(ref present);
                try
                {
                    if (BundleExtractor.ComputePlainHash(bundlePath).AsSpan().SequenceEqual(entry.PlainHash))
                    {
                        Interlocked.Increment(ref matched);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        Console.Error.WriteLine($"MISMATCH {entry.Name}");
                    }
                }
                catch (BundleFormatException e)
                {
                    Interlocked.Increment(ref failed);
                    Console.Error.WriteLine($"FAIL {entry.Name}: {e.Message}");
                }
            });

            Console.Error.WriteLine();
            Console.Error.WriteLine($"indexed   {selected.Count}");
            Console.Error.WriteLine($"on disk   {present}");
            Console.Error.WriteLine($"matched   {matched}");
            Console.Error.WriteLine($"mismatch  {failed}");
            return Task.FromResult(failed == 0 ? 0 : 1);
        });

        return command;
    }

    private static Command BuildExtrasCommand(Option<string> gameDirOption, Option<string> cacheOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the group or file name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to unpack into, laid out like the game's own loose asset tree",
            DefaultValueFactory = _ => Path.Combine("output", "extras"),
        };

        var listOption = new Option<bool>("--list")
        {
            Description = "List the matching entries without downloading them",
        };

        var groupsOption = new Option<bool>("--groups")
        {
            Description = "List the download groups and their sizes",
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Parallel requests",
            DefaultValueFactory = _ => 4,
        };

        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Re-download files that are already present",
        };

        var keepPackagesOption = new Option<bool>("--keep-packages")
        {
            Description = "Keep the downloaded .gf zips beside the unpacked files",
        };

        var baseUrlOption = new Option<string?>("--base-url")
        {
            Description = $"CDN base URL (default: {CdnSource.DefaultBaseUrl})",
        };

        var command = new Command(
            "extras",
            "Download the on-demand voice packs and cutscenes listed in Version_Ex1.bin.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(outputOption);
        command.Options.Add(listOption);
        command.Options.Add(groupsOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(overwriteOption);
        command.Options.Add(keepPackagesOption);
        command.Options.Add(baseUrlOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var assets = await ResolveSourceAsync(
                parse, gameDirOption, cacheOption, baseUrlOption, http, cancellationToken);

            if (assets.ExtraManifestPath is null)
            {
                Console.Error.WriteLine($"No {ExtraManifest.FileName} available from {assets.Description}");
                return 1;
            }

            var manifest = ExtraManifest.Load(assets.ExtraManifestPath);
            var filters = parse.GetValue(filtersArgument) ?? [];

            if (parse.GetValue(groupsOption))
            {
                foreach (var group in manifest.Assets.GroupBy(a => a.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var kinds = group
                        .GroupBy(a => Path.GetExtension(a.RelativePath ?? a.RemoteName))
                        .OrderByDescending(k => k.Count())
                        .Select(k => $"{k.Count()} {k.Key}");
                    Console.WriteLine($"{group.Key,-28} {group.Count(),5}  {string.Join(", ", kinds)}");
                }

                Console.Error.WriteLine($"{manifest.Assets.Count} entries in {manifest.Groups.Count()} groups");
                return 0;
            }

            var selected = manifest.Assets
                .Where(a => filters.Length == 0
                            || filters.All(f => a.Group.Contains(f, StringComparison.OrdinalIgnoreCase)
                                                || (a.RelativePath ?? a.RemoteName)
                                                    .Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (selected.Count == 0)
            {
                Console.Error.WriteLine("No manifest entries matched.");
                return 1;
            }

            if (parse.GetValue(listOption))
            {
                foreach (var asset in selected)
                {
                    Console.WriteLine($"{asset.Group,-28} {asset.RemoteName,-40} {asset.RelativePath}");
                }

                Console.Error.WriteLine($"{selected.Count} entries");
                return 0;
            }

            var source = assets.Cdn;
            if (source.Versions.Count == 0)
            {
                Console.Error.WriteLine("Could not work out any CDN version folders to try.");
                return 1;
            }

            var options = new ExtraDownloadOptions(
                OutputDirectory: parse.GetValue(outputOption)!,
                Concurrency: parse.GetValue(concurrencyOption),
                Overwrite: parse.GetValue(overwriteOption),
                KeepPackages: parse.GetValue(keepPackagesOption));

            Console.Error.WriteLine($"CDN {source.BaseUrl}");
            Console.Error.WriteLine($"versions to try: {string.Join(", ", source.Versions)}");
            Console.Error.WriteLine($"{selected.Count} entries, unpacking into {options.OutputDirectory}");

            var downloader = new ExtraDownloader(http, source);
            var log = new SynchronousProgress(line =>
            {
                if (!line.StartsWith("OK", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(line);
                }
            });

            var progress = await downloader.DownloadAsync(selected, options, log, cancellationToken);

            Console.Error.WriteLine();
            Console.Error.WriteLine($"downloaded  {progress.Downloaded}");
            Console.Error.WriteLine($"already had {progress.Skipped}");
            Console.Error.WriteLine($"unavailable {progress.Unavailable}");
            Console.Error.WriteLine($"bytes       {FormatSize(progress.Bytes)}");
            return progress.Downloaded > 0 || progress.Skipped > 0 ? 0 : 1;
        });

        return command;
    }

    private static Command BuildAudioCommand(Option<string> gameDirOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the cue sheet name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write WAVs into, one folder per cue sheet",
            DefaultValueFactory = _ => "output",
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Stop after this many cue sheets; 0 means no limit",
            DefaultValueFactory = _ => 0,
        };

        var listOption = new Option<bool>("--list")
        {
            Description = "List the cue sheets that would be decoded, without decoding them",
        };

        var keyOption = new Option<ulong>("--key")
        {
            Description = "HCA decryption key; defaults to the configured PC key",
            DefaultValueFactory = _ => CriWorkerClient.PcHcaKey,
        };

        var assetsDirOption = new Option<string?>("--assets-dir")
        {
            Description = "Read cue sheets from this tree instead of the install, such as an `extras` download",
        };

        var command = new Command("audio", "Decode CRI cue sheets to WAV.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(listOption);
        command.Options.Add(keyOption);
        command.Options.Add(assetsDirOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var filters = parse.GetValue(filtersArgument) ?? [];
            var output = parse.GetValue(outputOption)!;
            var limit = parse.GetValue(limitOption);
            var key = parse.GetValue(keyOption);

            var assetsDirectory = parse.GetValue(assetsDirOption);
            var sheets = install.EnumerateCueSheets(assetsDirectory)
                .Where(p => filters.Length == 0
                            || filters.All(f => Path.GetFileName(p).Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (sheets.Count == 0)
            {
                Console.Error.WriteLine($"No .acb found under {assetsDirectory ?? install.LooseAssetDirectory}");
                return 1;
            }

            if (parse.GetValue(listOption))
            {
                foreach (var sheet in sheets)
                {
                    var bank = GameInstall.FindStreamingBank(sheet);
                    Console.WriteLine($"{Path.GetFileName(sheet),-52} {FormatSize(new FileInfo(sheet).Length),10}"
                                      + (bank is null ? "" : "  +awb"));
                }

                Console.Error.WriteLine($"{sheets.Count} cue sheets");
                return 0;
            }

            Console.Error.WriteLine($"{sheets.Count} cue sheets, writing to {output}");
            Console.Error.WriteLine("starting CRI worker (first run resolves Python deps via uv)...");

            using var worker = CriWorkerClient.Start();

            var decoded = 0;
            var tracks = 0;
            var failures = 0;
            long bytes = 0;

            foreach (var sheet in sheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(sheet);
                var target = Path.Combine(output, name);

                try
                {
                    var result = await worker.DecodeAcbAsync(
                        sheet, GameInstall.FindStreamingBank(sheet), target, key, cancellationToken);
                    decoded++;
                    tracks += result.Count;
                    bytes += result.Sum(t => t.Size);
                    if (result.Count > 0)
                    {
                        Console.Error.WriteLine($"  {name,-46} {result.Count,4} tracks");
                    }
                }
                catch (CriWorkerException e)
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL {name}: {e.Message}");
                }

                if (limit > 0 && decoded + failures >= limit)
                {
                    break;
                }
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine($"cue sheets {decoded}");
            Console.Error.WriteLine($"tracks     {tracks}");
            Console.Error.WriteLine($"bytes      {FormatSize(bytes)}");
            Console.Error.WriteLine($"failures   {failures}");
            return failures == 0 ? 0 : 1;
        });

        return command;
    }

    private static Command BuildExtractCommand(Option<string> gameDirOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the bundle name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write extracted files into",
            DefaultValueFactory = _ => "output",
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Stop after this many bundles; 0 means no limit",
            DefaultValueFactory = _ => 0,
        };

        var verifyOption = new Option<bool>("--verify")
        {
            Description = "Decompress every block without writing anything, and report failures",
        };

        var rawOption = new Option<bool>("--raw")
        {
            Description = "Write the Unity serialized files as-is instead of converting to PNG and text",
        };

        var keepRawOption = new Option<bool>("--keep-raw")
        {
            Description = "Keep the serialized files alongside the converted assets",
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Bundles to unpack in parallel",
            DefaultValueFactory = _ => BundleExtraction.DefaultConcurrency,
        };

        var kindsOption = new Option<string?>("--kinds")
        {
            Description = "Comma-separated asset kinds to write: Texture2D, Sprite, TextAsset",
        };

        var command = new Command("extract", "Decompress bundles and convert the assets inside them.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(verifyOption);
        command.Options.Add(rawOption);
        command.Options.Add(keepRawOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(kindsOption);

        command.SetAction((parse, cancellationToken) =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var filters = parse.GetValue(filtersArgument) ?? [];
            var raw = parse.GetValue(rawOption);
            var verify = parse.GetValue(verifyOption);

            var kindList = parse.GetValue(kindsOption);
            var kinds = string.IsNullOrWhiteSpace(kindList)
                ? null
                : kindList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var options = new ExtractOptions(
                OutputDirectory: parse.GetValue(outputOption)!,
                Concurrency: parse.GetValue(concurrencyOption),
                Limit: parse.GetValue(limitOption),
                Verify: verify,
                Raw: raw,
                KeepRaw: parse.GetValue(keepRawOption),
                Kinds: kinds);

            var paths = install.EnumerateBundles()
                .Where(p => filters.Length == 0
                            || filters.All(f => Path.GetFileName(p)
                                .Contains(f, StringComparison.OrdinalIgnoreCase)));

            Console.Error.WriteLine($"unpacking with {options.Concurrency} workers");

            // Avoid Progress<T> here: it bounces to the thread pool and lines land after the summary.
            // Console.Error is synchronized per-line anyway, so worker output won't tear.
            var log = new SynchronousProgress(Console.Error.WriteLine);
            var progress = BundleExtraction.Run(paths, options, log, cancellationToken);

            Console.Error.WriteLine();
            Console.Error.WriteLine($"bundles  {progress.Bundles}");
            Console.Error.WriteLine($"files    {progress.Files}");
            Console.Error.WriteLine($"bytes    {FormatSize(progress.Bytes)}");
            if (!raw && !verify)
            {
                Console.Error.WriteLine($"assets   {progress.Converted}");
                foreach (var (kind, count) in progress.Kinds.OrderByDescending(kv => kv.Value))
                {
                    Console.Error.WriteLine($"  {kind,-12} {count}");
                }
            }

            Console.Error.WriteLine($"failures {progress.Failures}");
            return Task.FromResult(progress.Failures == 0 ? 0 : 1);
        });

        return command;
    }

    private static Command BuildDownloadCommand(Option<string> gameDirOption, Option<string> cacheOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the bundle name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to mirror bundles into",
            DefaultValueFactory = _ => "output",
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Parallel requests",
            DefaultValueFactory = _ => 4,
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Stop after this many bundles; 0 means no limit",
            DefaultValueFactory = _ => 0,
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Resolve versions and sizes without writing any file",
        };

        var retryOption = new Option<bool>("--retry-unavailable")
        {
            Description = "Retry bundles previously recorded as not served",
        };

        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Re-download bundles already recorded as complete",
        };

        var baseUrlOption = new Option<string?>("--base-url")
        {
            Description = $"CDN base URL (default: {CdnSource.DefaultBaseUrl})",
        };

        var command = new Command("download", "Mirror bundles from the CDN.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(outputOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(limitOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(retryOption);
        command.Options.Add(overwriteOption);
        command.Options.Add(baseUrlOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var assets = await ResolveSourceAsync(
                parse, gameDirOption, cacheOption, baseUrlOption, http, cancellationToken);

            var catalog = ContentCatalog.Load(assets.CatalogPath);
            var source = assets.Cdn;
            if (source.Versions.Count == 0)
            {
                Console.Error.WriteLine("Could not work out any CDN version folders to try.");
                return 1;
            }

            var filters = parse.GetValue(filtersArgument) ?? [];
            var output = parse.GetValue(outputOption)!;
            var options = new DownloadOptions(
                OutputDirectory: Path.Combine(output, "raw"),
                Concurrency: parse.GetValue(concurrencyOption),
                DryRun: parse.GetValue(dryRunOption),
                RetryUnavailable: parse.GetValue(retryOption),
                Overwrite: parse.GetValue(overwriteOption),
                Limit: parse.GetValue(limitOption));

            var requests = catalog.Keys()
                .Where(k => k.IsBundleKey)
                .Where(k => filters.Length == 0
                            || filters.All(f => k.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .Select(k => new BundleRequest(
                    k.Name,
                    catalog.Locations(k).Select(l => l.BundleOptions).FirstOrDefault(o => o is not null)?.BundleSize ?? 0));

            Console.Error.WriteLine($"CDN {source.BaseUrl}");
            Console.Error.WriteLine($"versions to try: {string.Join(", ", source.Versions)}");
            Console.Error.WriteLine($"output {options.OutputDirectory}{(options.DryRun ? " (dry run)" : "")}");

            var index = DownloadIndex.Load(Path.Combine(output, "index.json"));
            var downloader = new BundleDownloader(http, source, index);
            var log = new Progress<string>(line =>
            {
                if (!line.StartsWith("OK", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(line);
                }
            });

            var progress = await downloader.DownloadAsync(requests, options, log, cancellationToken);

            Console.Error.WriteLine();
            Console.Error.WriteLine($"complete    {progress.Completed}");
            Console.Error.WriteLine($"unavailable {progress.Unavailable}");
            Console.Error.WriteLine($"failed      {progress.Failed}");
            Console.Error.WriteLine($"downloaded  {FormatSize(progress.BytesDownloaded)}");
            return 0;
        });

        return command;
    }

    // Dumps catalog stats. Skips "on disk" when running off CDN rather than showing a bogus zero.
    private static void PrintCatalogStats(ContentCatalog catalog, string? gameDir)
    {
        var keys = 0;
        var bundleKeys = 0;
        long declaredBytes = 0;
        var withOptions = 0;
        var onDisk = 0;
        var bundleDirectory = string.IsNullOrWhiteSpace(gameDir)
            ? null
            : GameInstall.Open(gameDir).BundleDirectory;

        foreach (var key in catalog.Keys())
        {
            keys++;
            if (!key.IsBundleKey)
            {
                continue;
            }

            bundleKeys++;
            if (bundleDirectory is not null && File.Exists(Path.Combine(bundleDirectory, key.Name)))
            {
                onDisk++;
            }

            var options = catalog.Locations(key).Select(l => l.BundleOptions).FirstOrDefault(o => o is not null);
            if (options is not null)
            {
                withOptions++;
                declaredBytes += options.BundleSize;
            }
        }

        Console.WriteLine($"locator             {catalog.LocatorId}");
        Console.WriteLine($"keys                {keys}");
        Console.WriteLine($"  bundle keys       {bundleKeys}");
        Console.WriteLine($"  asset keys        {keys - bundleKeys}");
        if (bundleDirectory is not null)
        {
            Console.WriteLine($"bundles on disk     {onDisk} of {bundleKeys}");
        }

        Console.WriteLine($"with download info  {withOptions}");
        Console.WriteLine($"declared total size {FormatSize(declaredBytes)}");
    }

    private static string? ResolveBundlePath(string? gameDir, string name)
    {
        if (File.Exists(name))
        {
            return name;
        }

        if (gameDir is null)
        {
            return null;
        }

        var fileName = name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ? name : name + ".bundle";
        var candidate = Path.Combine(GameInstall.Open(gameDir).BundleDirectory, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool Matches(BundleFile bundle, string[] filters)
    {
        if (filters.Length == 0)
        {
            return true;
        }

        return filters.All(filter =>
            bundle.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || bundle.Nodes.Any(n => n.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Describe(BundleFile bundle)
    {
        var primary = bundle.Archives.SelectMany(a => a.Nodes).FirstOrDefault()?.Path ?? "(no files)";
        var archives = bundle.IsMultiArchive ? $"{bundle.Archives.Count,4} archives" : "            ";
        return $"{bundle.FileName}  {FormatSize(bundle.FileSize),10}  {archives}  {primary}";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.00} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KiB",
        _ => $"{bytes} B",
    };

    // Bails out if --game-dir is missing for commands that can't run off CDN.
    // Done manually so CDN-only commands aren't forced to require it.
    private static GameInstall RequireInstall(ParseResult parse, Option<string> gameDirOption)
    {
        var dir = parse.GetValue(gameDirOption);
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new DirectoryNotFoundException("this command needs --game-dir; only catalog, download and extras can run without an install");
        }

        return GameInstall.Open(dir);
    }

    // Resolves catalog/manifest from local install if provided, otherwise grabs them from CDN.
    private static async Task<AssetSource> ResolveSourceAsync(
        ParseResult parse,
        Option<string> gameDirOption,
        Option<string> cacheOption,
        Option<string?> baseUrlOption,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var dir = parse.GetValue(gameDirOption);
        var baseUrl = parse.GetValue(baseUrlOption);

        if (!string.IsNullOrWhiteSpace(dir))
        {
            return AssetSource.FromInstall(GameInstall.Open(dir), baseUrl);
        }

        var log = new SynchronousProgress(Console.Error.WriteLine);
        return await AssetSource.FromCdnAsync(
            http,
            parse.GetValue(cacheOption)!,
            parse.GetValue(LauncherUrlOption!),
            baseUrl,
            log,
            cancellationToken);
    }

    // Fires progress synchronously on the calling thread.
    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    // Running counters for `list --stats`.
    private sealed class ScanStatistics
    {
        private readonly Dictionary<string, int> _revisions = [];
        private readonly List<string> _errors = [];

        public int Files { get; private set; }

        public int Archives { get; private set; }

        public int MultiArchiveFiles { get; private set; }

        public int InvertedArchives { get; private set; }

        public int Unreadable { get; private set; }

        public int Incomplete { get; private set; }

        public long BytesOnDisk { get; private set; }

        public long BytesUncompressed { get; private set; }

        public int Nodes { get; private set; }

        public void RecordFailure(string message)
        {
            Unreadable++;
            _errors.Add(message);
        }

        public void Record(BundleFile bundle)
        {
            Files++;
            BytesOnDisk += bundle.FileSize;
            BytesUncompressed += bundle.UncompressedPayloadSize;
            Archives += bundle.Archives.Count;

            if (bundle.IsMultiArchive)
            {
                MultiArchiveFiles++;
            }

            if (!bundle.FullyParsed)
            {
                Incomplete++;
                if (bundle.TrailingDataReason is { } reason)
                {
                    _errors.Add($"{bundle.FileName}: {reason}");
                }
            }

            foreach (var archive in bundle.Archives)
            {
                if (archive.Inverted)
                {
                    InvertedArchives++;
                }

                Nodes += archive.Nodes.Count;
                var revision = archive.Header.UnityRevision;
                _revisions[revision] = _revisions.GetValueOrDefault(revision) + 1;
            }
        }

        public void Print(TextWriter output)
        {
            output.WriteLine($"bundle files read   {Files}");
            output.WriteLine($"  multi-archive     {MultiArchiveFiles}");
            output.WriteLine($"  not fully parsed  {Incomplete}");
            output.WriteLine($"unreadable files    {Unreadable}");
            output.WriteLine($"archives            {Archives}");
            output.WriteLine($"  plain             {Archives - InvertedArchives}");
            output.WriteLine($"  inverted          {InvertedArchives}");
            output.WriteLine($"files inside        {Nodes}");
            output.WriteLine($"bytes on disk       {FormatSize(BytesOnDisk)}");
            output.WriteLine($"bytes uncompressed  {FormatSize(BytesUncompressed)}");
            output.WriteLine("unity revisions");
            foreach (var (revision, count) in _revisions.OrderByDescending(kv => kv.Value))
            {
                output.WriteLine($"  {revision}  {count}");
            }

            if (_errors.Count > 0)
            {
                output.WriteLine($"issues ({_errors.Count})");
                foreach (var error in _errors.Take(10))
                {
                    output.WriteLine($"  {error}");
                }
            }
        }
    }
}
