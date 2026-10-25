using System.CommandLine;
using DorkDl.Core;
using DorkDl.Core.Bundle;
using DorkDl.Core.Catalog;
using DorkDl.Core.Download;
using DorkDl.Core.Extraction;
using DorkDl.Core.Unity;
using DorkDl.CriInterop;

namespace DorkDl.Cli;

// Targets System.CommandLine 3.0-preview.7 (SetAction, Options.Add, and Parse(args).InvokeAsync).
public static class Program
{
    // Stashed here so ResolveSourceAsync can read it without plumbing it through every subcommand.
    private static Option<string?>? LauncherUrlOption;

    public static async Task<int> Main(string[] args)
    {
        var gameDirOption = new Option<string>("--game-dir")
        {
            Description = "Path to the installed game, either its root directory or its data directory",
            // Recursive is needed so subcommands see it. Not required since catalog/download/extras can run off CDN.
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
        root.Subcommands.Add(BuildPacksCommand(gameDirOption));

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

        var hashesOption = new Option<bool>("--hashes")
        {
            Description = "Also print each archive's plain MD5, which is what the catalog calls the bundle's hash",
        };

        var command = new Command("inspect", "Show the archives, block tables and file lists of one bundle.");
        command.Arguments.Add(bundleArgument);
        command.Options.Add(hashesOption);

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
            var hashes = parse.GetValue(hashesOption)
                ? BundleExtractor.ComputeArchivePlainHashes(path, bundle)
                : null;

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
                if (hashes is not null)
                {
                    Console.WriteLine($"    plain md5  {Convert.ToHexString(hashes[index]).ToLowerInvariant()}");
                }

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

        var typeOption = TypeOption();

        var depsOption = new Option<bool>("--deps")
        {
            Description = "Show the bundles each key's locations load from",
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
        command.Options.Add(typeOption);
        command.Options.Add(depsOption);
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
            var showDeps = parse.GetValue(depsOption);
            var type = parse.GetValue(typeOption);

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

                if (type is not null && !catalog.Locations(key).Any(l => MatchesType(l, type)))
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

                if (showLocations || showDeps)
                {
                    foreach (var location in catalog.Locations(key))
                    {
                        if (showLocations)
                        {
                            Console.WriteLine($"    {location.ResourceType ?? "?"}  id={location.InternalId}");
                        }

                        if (showDeps)
                        {
                            foreach (var bundle in catalog.BundlesFor(location).Distinct())
                            {
                                Console.WriteLine($"      in {bundle}");
                            }
                        }
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
                            // Leaving a temp stream behind is not worth failing the run over.
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

    private static Option<string> PacksOption() => new("--packs")
    {
        Description = "Where the pack index lives; built on first use",
        DefaultValueFactory = _ => Path.Combine("output", "pack-index.json"),
    };

    // Loads pack index, building it on first use (hashes all pack sub-archives against GFF index).
    private static IReadOnlyDictionary<string, PackedBundle> LoadOrBuildPacks(
        GameInstall install, string indexPath, int concurrency, bool rebuild, CancellationToken cancellationToken)
    {
        if (rebuild || !File.Exists(indexPath))
        {
            var gffPath = install.FindGffIndex()
                ?? throw new FileNotFoundException($"No GFF container found in {install.BundleDirectory}");

            Console.Error.WriteLine($"building {indexPath} from {install.BundleDirectory}");
            var report = PackIndex.Build(
                install, GffContainer.Load(gffPath), indexPath, concurrency,
                new SynchronousProgress(Console.Error.WriteLine), cancellationToken);

            Console.Error.WriteLine($"files    {report.Files}");
            Console.Error.WriteLine($"packs    {report.Packs}, holding {report.Archives} archives");
            Console.Error.WriteLine($"resolved {report.Resolved} catalog bundles");
            Console.Error.WriteLine($"failures {report.Failures}");
            Console.Error.WriteLine();
        }

        return PackIndex.Load(indexPath);
    }

    private static Command BuildPacksCommand(Option<string> gameDirOption)
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the catalog bundle name or the file holding it, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var packsOption = PacksOption();

        var rebuildOption = new Option<bool>("--rebuild")
        {
            Description = "Rebuild the index even if it already exists",
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Stop after this many matches; 0 means no limit",
            DefaultValueFactory = _ => 50,
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Files to hash in parallel while building",
            DefaultValueFactory = _ => BundleExtraction.DefaultConcurrency,
        };

        var command = new Command("packs", "Resolve catalog bundle names to the installed files that hold them.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(packsOption);
        command.Options.Add(rebuildOption);
        command.Options.Add(limitOption);
        command.Options.Add(concurrencyOption);

        command.SetAction((parse, cancellationToken) =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var packs = LoadOrBuildPacks(
                install, parse.GetValue(packsOption)!, parse.GetValue(concurrencyOption),
                parse.GetValue(rebuildOption), cancellationToken);

            var filters = parse.GetValue(filtersArgument) ?? [];
            var limit = parse.GetValue(limitOption);
            var shown = 0;

            foreach (var entry in packs.Values.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (filters.Length > 0 && !filters.All(f =>
                        entry.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || entry.File.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Console.WriteLine(entry.File == entry.Name
                    ? $"{entry.Name}  own file, {FormatSize(entry.Size)}"
                    : $"{entry.Name}  in {entry.File}#{entry.Archive} at {entry.Offset}, {FormatSize(entry.Size)}");

                if (limit > 0 && ++shown >= limit)
                {
                    Console.Error.WriteLine($"stopped at --limit {limit}");
                    break;
                }
            }

            return Task.FromResult(0);
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

        var memoryOption = MemoryOption();
        var packsOption = PacksOption();

        var command = new Command("names", "Resolve asset GUIDs to the names the bundles give them.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(indexOption);
        command.Options.Add(rebuildOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(bundlesOption);
        command.Options.Add(memoryOption);
        command.Options.Add(packsOption);

        command.SetAction((parse, cancellationToken) =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var index = parse.GetValue(indexOption)!;
            var rebuild = parse.GetValue(rebuildOption);

            // --rebuild means from nothing; otherwise a run picks up where a killed one stopped.
            if (rebuild)
            {
                File.Delete(AssetNameIndex.ProgressPathFor(index));
            }

            if (rebuild || !File.Exists(index))
            {
                // The GFF index sits in the bundle directory with a .bundle name, and is not one.
                var gff = install.FindGffIndex();
                var sources = install.EnumerateBundles().Where(p => p != gff);
                var take = parse.GetValue(bundlesOption);
                if (take > 0)
                {
                    sources = sources.Take(take);
                }

                // With pack index, record entries under catalog bundle names instead of "<pack>#<i>".
                var packs = LoadOrBuildPacks(
                    install, parse.GetValue(packsOption)!, parse.GetValue(concurrencyOption), rebuild: false, cancellationToken);
                var byArchive = packs.Values.ToDictionary(e => (e.File, e.Archive), e => e.Name);
                string NameArchive(string file, int archive) =>
                    byArchive.TryGetValue((file, archive), out var name) ? name : archive == 0 ? file : $"{file}#{archive}";

                Console.Error.WriteLine($"building {index} from {install.BundleDirectory}");
                var log = new SynchronousProgress(Console.Error.WriteLine);
                var report = AssetNameIndex.Build(
                    sources,
                    index,
                    parse.GetValue(concurrencyOption),
                    MemoryLimitBytes(parse, memoryOption),
                    log,
                    NameArchive,
                    cancellationToken);

                Console.Error.WriteLine();
                Console.Error.WriteLine($"bundles  {report.Bundles}");
                if (report.Skipped > 0)
                {
                    Console.Error.WriteLine($"resumed  {report.Skipped}");
                }

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

        var fileOption = new Option<string?>("--file")
        {
            Description = "A GFF container to read instead of the install's bundle index",
        };

        var command = new Command("gff", "Read the GFF bundle index and check it against the install.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(fileOption);
        command.Options.Add(verifyOption);
        command.Options.Add(limitOption);
        command.Options.Add(concurrencyOption);

        command.SetAction((parse, cancellationToken) =>
        {
            var install = RequireInstall(parse, gameDirOption);
            var path = parse.GetValue(fileOption) ?? install.FindGffIndex();
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
            Description = "Case-insensitive substring filters on the file name or the catalog bundle name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write extracted files into",
            DefaultValueFactory = _ => "output",
        };

        var typeOption = TypeOption();
        var packsOption = PacksOption();

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
            Description = "Comma-separated asset kinds to write: Texture2D, Sprite, TextAsset, MonoBehaviour",
        };

        var memoryOption = MemoryOption();

        var command = new Command("extract", "Decompress bundles and convert the assets inside them.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(verifyOption);
        command.Options.Add(rawOption);
        command.Options.Add(keepRawOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(kindsOption);
        command.Options.Add(memoryOption);
        command.Options.Add(typeOption);
        command.Options.Add(packsOption);

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
                Kinds: kinds,
                MemoryLimit: MemoryLimitBytes(parse, memoryOption));

            var targets = SelectExtractTargets(
                install, filters, parse.GetValue(typeOption), parse.GetValue(packsOption)!,
                options.Concurrency, cancellationToken);

            var budget = options.MemoryLimit > 0 ? options.MemoryLimit : MemoryBudget.DefaultLimit;
            Console.Error.WriteLine($"unpacking with {options.Concurrency} workers, {FormatSize(budget)} of payload in flight");

            // Avoid Progress<T> here: it bounces to threadpool so lines land after summary. Console.Error writes whole lines without tearing.
            var log = new SynchronousProgress(Console.Error.WriteLine);
            var progress = BundleExtraction.Run(targets, options, log, cancellationToken);

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

        var typeOption = TypeOption();

        var command = new Command("download", "Mirror bundles from the CDN.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(typeOption);
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

            // --type filters by asset resource type (since bundle and asset keys are hashes).
            var type = parse.GetValue(typeOption);
            var wanted = type is null ? null : BundlesHoldingType(catalog, type);
            if (wanted is not null)
            {
                Console.Error.WriteLine($"{wanted.Count} bundles hold assets of type *{type}*");
            }

            var requests = catalog.Keys()
                .Where(k => k.IsBundleKey)
                .Where(k => wanted is null || wanted.Contains(k.Name))
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

    // Determines extraction targets: whole bundles or individual pack sub-archives (filtered by filename or --type).
    private static IEnumerable<ExtractTarget> SelectExtractTargets(
        GameInstall install,
        string[] filters,
        string? type,
        string packsPath,
        int concurrency,
        CancellationToken cancellationToken)
    {
        bool Matches(string name) =>
            filters.Length == 0 || filters.All(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

        if (type is null)
        {
            // The GFF index shares the .bundle extension but holds no assets.
            var gff = filters.Length == 0 ? install.FindGffIndex() : null;
            var byFile = install.EnumerateBundles()
                .Where(p => p != gff && Matches(Path.GetFileName(p)))
                .Select(ExtractTarget.WholeFile)
                .ToList();

            // A filter that matched nothing on disk may be a catalog name inside a pack.
            if (byFile.Count > 0 || filters.Length == 0 || !File.Exists(packsPath))
            {
                return byFile;
            }

            var packs = PackIndex.Load(packsPath);
            return packs.Values
                .Where(e => e.File != e.Name && Matches(e.Name))
                .Select(e => new ExtractTarget(
                    Path.Combine(install.BundleDirectory, e.File), Path.GetFileNameWithoutExtension(e.Name), e.Archive))
                .ToList();
        }

        var catalogPath = install.FindCatalog()
            ?? throw new FileNotFoundException($"No catalog_main_*.bin in {install.BundleDirectory}");
        var wanted = BundlesHoldingType(ContentCatalog.Load(catalogPath), type);
        Console.Error.WriteLine($"{wanted.Count} catalog bundles hold assets of type *{type}*");

        var index = LoadOrBuildPacks(install, packsPath, concurrency, rebuild: false, cancellationToken);
        var targets = new List<ExtractTarget>();
        var missing = 0;
        foreach (var name in wanted.Where(Matches).Order(StringComparer.Ordinal))
        {
            if (index.TryGetValue(name, out var entry))
            {
                targets.Add(new ExtractTarget(
                    Path.Combine(install.BundleDirectory, entry.File),
                    Path.GetFileNameWithoutExtension(entry.Name),
                    entry.File == entry.Name ? null : entry.Archive));
            }
            else
            {
                missing++;
            }
        }

        if (missing > 0)
        {
            Console.Error.WriteLine($"{missing} of them are not installed");
        }

        return targets;
    }

    private static Option<string?> TypeOption() => new("--type")
    {
        Description = "Only keys with a location of this resource type, by case-insensitive substring: AdvImportBook, Texture2D",
    };

    private static bool MatchesType(CatalogLocation location, string type) =>
        location.ResourceType?.Contains(type, StringComparison.OrdinalIgnoreCase) == true;

    // Returns bundle names providing assets of the specified resource type.
    private static HashSet<string> BundlesHoldingType(ContentCatalog catalog, string type)
    {
        var bundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in catalog.Keys())
        {
            if (key.IsBundleKey)
            {
                continue;
            }

            foreach (var location in catalog.Locations(key))
            {
                if (MatchesType(location, type))
                {
                    bundles.UnionWith(catalog.BundlesFor(location));
                }
            }
        }

        return bundles;
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

    // In-flight payload budget in MiB (0 defaults to 1/4 available RAM).
    private static Option<int> MemoryOption() => new("--memory")
    {
        Description = $"Payload bytes to hold in memory at once, in MiB (default: {MemoryBudget.DefaultLimit >> 20})",
        DefaultValueFactory = _ => 0,
    };

    private static long MemoryLimitBytes(ParseResult parse, Option<int> option) =>
        Math.Max(0, parse.GetValue(option)) << 20;

    // Fires progress synchronously on calling thread.
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
