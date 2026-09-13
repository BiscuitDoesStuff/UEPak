using CUE4Parse.FileProvider;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;

// Global options (any order, before the command) or env vars:
//   --paks <dir>    UEPAK_PAKS   directory containing the game's .pak files (required)
//   --key <hex>     UEPAK_KEY    AES-256 key as 64 hex chars (omit for unencrypted paks)
//   --usmap <file>  UEPAK_USMAP  .usmap mappings (needed to load UE5 unversioned-property assets)
//   --ue <ver>      UEPAK_UE     engine version, e.g. UE5_5 (default), UE4_27, UE5_3
string? paksDir = Environment.GetEnvironmentVariable("UEPAK_PAKS");
string? aesKey = Environment.GetEnvironmentVariable("UEPAK_KEY");
string? usmapPath = Environment.GetEnvironmentVariable("UEPAK_USMAP");
string ueVersion = Environment.GetEnvironmentVariable("UEPAK_UE") ?? "UE5_5";
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--paks": paksDir = Next(); break;
        case "--key": aesKey = Next(); break;
        case "--usmap": usmapPath = Next(); break;
        case "--ue": ueVersion = Next(); break;
        default: rest.Add(args[i]); break;
    }
}
args = rest.ToArray();

var isHelp = args.Length > 0 && args[0] is "--help" or "-h" or "help";
if (args.Length == 0 || paksDir == null || isHelp)
{
    Console.WriteLine("""
        Usage: uepak --paks <dir> [--key <hex>] [--usmap <file>] [--ue <ver>] <command> [args]

        Options may also be given as env vars UEPAK_PAKS / _KEY / _USMAP / _UE.

          --help, -h, help                       print this usage and exit

        Browse / raw extraction
          list [filter]                          list asset paths (case-insensitive substring filter)
          export <assetPath> <outFile>           save one asset's raw bytes
          exportall [outDir] [filter]            save every mounted file, preserving folder structure

        Conversion (same exporters FModel uses)
          exportconverted <outDir> [filter]      every file to its native format: textures->PNG, meshes->.psk/.pskx,
                                                 audio->.wav/.ogg/.binka, everything else->JSON properties or raw
          exportgltf <outDir> [filter]           every mesh as self-contained .glb with materials/textures
          exporttexture <assetPath> <outDir>     decode one texture to PNG (no preview size cap)
          decompileblueprints <outDir> [filter]  every Blueprint class to readable pseudo-C++
          fillgaps <decryptedDir> <convertedDir> ensure every archive path has a file in convertedDir

        Dependency graph
          manifest <out.json> [filter]           package -> imported packages graph
          bundle <manifest> <convertedDir> <out> copy each mesh's full dependency closure into its own folder

        Levels
          dumpexports <pkgPath>                  list a package's exports with types
          dumplevel <mapPath> [max] [class]      print actor exports + properties from a .umap
          dumpplacements <mapPath> <out.json>    every StaticMeshActor's mesh + transform
          dumpworld <mapPath> <out.json> [meshDir]  full streaming-level graph incl. Blueprint ISM instances -> JSON
          exportworld <mapPath> <outDir>         CUE4Parse's built-in USD world export

        Localization
          dumplocres [filter]                    print localized strings

        Exit codes: 0 ok, 1 usage, 2 failure
        """);
    return isHelp ? 0 : 1;
}

if (!Directory.Exists(paksDir))
{
    Console.Error.WriteLine($"ERROR: --paks directory not found: {paksDir}");
    return 1;
}

if (aesKey != null && !(aesKey.Length == 64 && aesKey.All(Uri.IsHexDigit)))
{
    Console.Error.WriteLine("ERROR: --key must be 64 hex characters");
    return 1;
}

if (usmapPath != null && !File.Exists(usmapPath))
{
    Console.Error.WriteLine($"ERROR: --usmap file not found: {usmapPath}");
    return 1;
}

if (!Enum.TryParse<EGame>("GAME_" + ueVersion, out var game))
{
    Console.WriteLine($"Unknown --ue version '{ueVersion}'. Valid: " + string.Join(", ", Enum.GetNames<EGame>().Where(n => n.StartsWith("GAME_UE")).Select(n => n[5..])));
    return 1;
}

try
{

var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, new VersionContainer(game));
provider.Initialize();
if (aesKey != null)
    provider.SubmitKey(new FGuid(), new FAesKey(Convert.FromHexString(aesKey)));
if (usmapPath != null)
    provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath, StringComparer.Ordinal);

Console.WriteLine($"Mounted: Files.Count={provider.Files.Count}, UnloadedVfs={provider.UnloadedVfs.Count}" +
    (provider.UnloadedVfs.Count > 0 && aesKey == null ? "  (some archives are encrypted -- pass --key)" : ""));

if (provider.Files.Count == 0)
{
    Console.Error.WriteLine("ERROR: no files mounted" + (provider.UnloadedVfs.Count > 0 && aesKey == null ? " (archives are encrypted -- pass --key)" : ""));
    return 2;
}

switch (args[0])
{
    case "list":
    {
        var filter = args.Length > 1 ? args[1] : null;
        var paths = provider.Files.Keys
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p)
            .ToList();
        foreach (var p in paths) Console.WriteLine(p);
        Console.WriteLine($"-- {paths.Count} matching path(s)");
        break;
    }
    case "export":
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: export <assetPath> <outFile>");
            return 1;
        }
        var assetPath = args[1];
        var outFile = args[2];
        var data = provider.SaveAsset(assetPath);
        File.WriteAllBytes(outFile, data);
        Console.WriteLine($"Wrote {data.Length} bytes to {outFile}");
        break;
    }
    case "exportall":
    {
        var outDir = args.Length > 1 ? args[1] : "GAMEDecrypted";
        var filter = args.Length > 2 ? args[2] : null;
        Directory.CreateDirectory(outDir);
        var all = provider.Files.Where(kv => filter == null || kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"Exporting {all.Count} files to {Path.GetFullPath(outDir)} ...");
        int ok = 0, fail = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (path, entry) in all)
        {
            try
            {
                var data = provider.SaveAsset(path);
                var outPath = SafeJoin(outDir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, data);
                ok++;
            }
            catch (Exception e)
            {
                fail++;
                Console.WriteLine($"  FAIL {path} -> {e.GetType().Name}: {e.Message}");
            }
            if ((ok + fail) % 1000 == 0)
            {
                Console.WriteLine($"PROGRESS: {ok + fail}/{all.Count} (ok={ok} fail={fail}) elapsed={sw.Elapsed:mm\\:ss}");
                Console.Out.Flush();
            }
        }
        Console.WriteLine($"DONE: ok={ok} fail={fail} of {all.Count} in {sw.Elapsed:mm\\:ss}");
        return fail > 0 ? 2 : 0;
    }
    case "bundle":
    {
        // Build self-contained per-mesh bundles using the cross-reference
        // manifest: for every mesh (anything that exported as .psk/.pskx),
        // walk its manifest dependency closure (materials, parent
        // materials, textures, skeleton, physics asset, ...) and copy every
        // file belonging to each reached package into one bundle folder,
        // preserving relative paths to avoid name collisions and keep it
        // navigable. Manifest references are UE object paths ("/Game/...",
        // "/Engine/...", or plugin-mounted paths); resolved to our on-disk
        // relative path via LoadPackage(ref).Name (authoritative) with a
        // FixPath()-based best-effort fallback for the rare plugin mount
        // points CUE4Parse can't resolve standalone.
        if (args.Length < 4) { Console.WriteLine("Usage: bundle <manifestFile> <convertedDir> <bundleOutDir>"); return 1; }
        var manifestFile = args[1];
        var convertedDir = args[2];
        var bundleOutDir = args[3];
        var manifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(manifestFile))!;

        string? ResolveRefToRelPath(string reference)
        {
            try { return provider.LoadPackage(reference).Name; }
            catch
            {
                try
                {
                    var fixedPath = provider.FixPath(reference);
                    return Path.Combine(Path.GetDirectoryName(fixedPath) ?? "", Path.GetFileNameWithoutExtension(fixedPath)).Replace('\\', '/');
                }
                catch { return null; }
            }
        }

        void CopyAllFilesFor(string relPath, string destBundleDir)
        {
            var srcDir = Path.Combine(convertedDir, Path.GetDirectoryName(relPath) ?? "");
            var stem = Path.GetFileName(relPath);
            if (!Directory.Exists(srcDir)) return;
            foreach (var f in Directory.EnumerateFiles(srcDir, stem + ".*"))
            {
                var relToConverted = Path.GetRelativePath(convertedDir, f);
                var dest = Path.Combine(destBundleDir, relToConverted);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) File.Copy(f, dest);
            }
        }

        var meshRoots = manifest.Keys.Where(k =>
        {
            var dir = Path.Combine(convertedDir, Path.GetDirectoryName(k) ?? "");
            var stem = Path.GetFileName(k);
            return Directory.Exists(dir) && (Directory.EnumerateFiles(dir, stem + ".psk").Any() || Directory.EnumerateFiles(dir, stem + ".pskx").Any());
        }).ToList();

        Console.WriteLine($"Found {meshRoots.Count} mesh roots to bundle.");
        int done = 0, resolveFail = 0, capped = 0;
        var usedBundleNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var root in meshRoots)
        {
            var baseName = Path.GetFileName(root);
            // Different meshes in different folders can share a base filename
            // (e.g. multiple "SM_Wall"); disambiguate so bundles never collide.
            var bundleName = baseName;
            if (usedBundleNames.TryGetValue(baseName, out var seenCount))
            {
                usedBundleNames[baseName] = seenCount + 1;
                bundleName = $"{baseName}_{seenCount + 1}";
            }
            else usedBundleNames[baseName] = 1;
            var thisBundleDir = Path.Combine(bundleOutDir, bundleName);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(root);
            visited.Add(root);
            CopyAllFilesFor(root, thisBundleDir);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!manifest.TryGetValue(current, out var refs)) continue;
                foreach (var reference in refs)
                {
                    var relPath = ResolveRefToRelPath(reference);
                    if (relPath == null) { resolveFail++; continue; }
                    if (!visited.Add(relPath)) continue;
                    CopyAllFilesFor(relPath, thisBundleDir);
                    if (visited.Count < 200) queue.Enqueue(relPath); // depth/size safety cap per bundle
                    else capped++;
                }
            }
            done++;
            if (done % 500 == 0)
                Console.WriteLine($"PROGRESS: {done}/{meshRoots.Count} bundles built (resolveFail={resolveFail}) elapsed={sw.Elapsed:mm\\:ss}");
        }
        Console.WriteLine($"DONE: {done} bundles built to {bundleOutDir} (resolveFail={resolveFail} capped={capped}) in {sw.Elapsed:mm\\:ss}");
        return resolveFail > 0 ? 2 : 0;
    }
    case "manifest":
    {
        // Cross-reference manifest: for every real UE package, read its own
        // Import Table (the same mechanism UE itself uses to know which
        // other packages a package depends on) to build a package-level
        // reference graph. This is type-agnostic by construction -- a mesh
        // importing its materials, a material importing its textures, a
        // blueprint importing its component classes/meshes/sounds, a world
        // importing its actor blueprints -- all show up the same way, as
        // import-table entries, so one generic pass covers meshes,
        // materials, blueprints, actors, worlds, everything, without
        // needing bespoke per-type property parsing. /Script/* imports are
        // class/type definitions (not content dependencies) and excluded.
        if (args.Length < 2) { Console.WriteLine("Usage: manifest <outFile.json> [pathFilter]"); return 1; }
        var outFile = args[1];
        var filter = args.Length > 2 ? args[2] : null;
        var graph = new Dictionary<string, List<string>>();
        int processed = 0, packagesWithRefs = 0, failCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (path, entry) in provider.Files)
        {
            if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.IsUePackagePayload) continue;
            if (!entry.IsUePackage) continue;
            processed++;
            try
            {
                if (provider.LoadPackage(entry) is not CUE4Parse.UE4.Assets.Package pkg) continue;
                var refs = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var imp in pkg.ImportMap)
                {
                    // PackageName is only populated on some import kinds; the
                    // reliable way to get the real source package (matching
                    // CUE4Parse's own ResolveImport) is to walk OuterIndex up
                    // to the outermost import and read ITS ObjectName.
                    var current = imp;
                    var steps = 0;
                    while (current is { OuterIndex.IsNull: false, OuterIndex.IsImport: true } && steps++ < 64)
                    {
                        var idx = -current.OuterIndex.Index - 1;
                        if (idx < 0 || idx >= pkg.ImportMap.Length) break;
                        current = pkg.ImportMap[idx];
                    }
                    var pn = current?.ObjectName.Text;
                    if (string.IsNullOrEmpty(pn) || pn == "None" || pn.StartsWith("/Script/")) continue;
                    refs.Add(pn);
                }
                if (refs.Count > 0)
                {
                    var key = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path)).Replace('\\', '/');
                    graph[key] = refs.ToList();
                    packagesWithRefs++;
                }
            }
            catch (Exception) { failCount++; }
            if (processed % 10000 == 0)
                Console.WriteLine($"PROGRESS: {processed} scanned (withRefs={packagesWithRefs} fail={failCount}) elapsed={sw.Elapsed:mm\\:ss}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, System.Text.Json.JsonSerializer.Serialize(graph, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE: {packagesWithRefs} packages with references (of {processed} scanned, {failCount} failed) written to {outFile} in {sw.Elapsed:mm\\:ss}");
        return failCount > 0 ? 2 : 0;
    }
    case "fillgaps":
    {
        // Guarantee every file in the archive has SOMETHING at its path in
        // <convertedDir>, no matter what: if exportconverted already put a
        // real conversion (or even a raw copy) there, leave it alone. If
        // nothing is there, copy the raw bytes from <decryptedDir> if that
        // succeeded for this file. If even THAT doesn't exist (the small
        // set of entries that are both AES-encrypted and Oodle-compressed,
        // where CUE4Parse can't produce usable bytes), read the still-encrypted,
        // still-compressed bytes directly off the raw pak file and place
        // those instead, so the path exists even though the content isn't
        // real game data.
        if (args.Length < 3) { Console.WriteLine("Usage: fillgaps <decryptedDir> <convertedDir>"); return 1; }
        var decryptedDir = args[1];
        var convertedDir = args[2];
        int alreadyThere = 0, copiedFromDecrypted = 0, rawFromPak = 0, rawFromPakFail = 0, processed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (path, entry) in provider.Files)
        {
            processed++;
            var outStemDir = Path.Combine(convertedDir, Path.GetDirectoryName(path) ?? "");
            var outStem = Path.GetFileNameWithoutExtension(path);
            var hasAny = Directory.Exists(outStemDir) && Directory.EnumerateFiles(outStemDir, outStem + ".*").Any();
            if (hasAny) { alreadyThere++; }
            else
            {
                var decryptedPath = SafeJoin(decryptedDir, path);
                var outPath = SafeJoin(convertedDir, path);
                if (File.Exists(decryptedPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.Copy(decryptedPath, outPath, overwrite: true);
                    copiedFromDecrypted++;
                }
                else
                {
                    try
                    {
                        var fpe = (FPakEntry)entry;
                        var pakPath = fpe.Vfs.ToString()!;
                        using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        long readStart; int readLen;
                        if (fpe.CompressionBlocks.Length > 0)
                        {
                            readStart = fpe.CompressionBlocks[0].CompressedStart;
                            readLen = (int)fpe.CompressedSize;
                        }
                        else
                        {
                            readStart = fpe.Offset + fpe.StructSize;
                            readLen = (int)fpe.UncompressedSize;
                        }
                        var buf = new byte[readLen];
                        fs.Seek(readStart, SeekOrigin.Begin);
                        var readTotal = 0;
                        while (readTotal < readLen) { var n = fs.Read(buf, readTotal, readLen - readTotal); if (n == 0) break; readTotal += n; }
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                        File.WriteAllBytes(outPath, buf);
                        rawFromPak++;
                    }
                    catch (Exception e)
                    {
                        rawFromPakFail++;
                        Console.WriteLine($"  FAIL {path} -> {e.GetType().Name}: {e.Message}");
                    }
                }
            }
            if (processed % 10000 == 0)
                Console.WriteLine($"PROGRESS: {processed}/{provider.Files.Count} (alreadyThere={alreadyThere} copiedFromDecrypted={copiedFromDecrypted} rawFromPak={rawFromPak} rawFromPakFail={rawFromPakFail}) elapsed={sw.Elapsed:mm\\:ss}");
        }
        Console.WriteLine($"DONE: alreadyThere={alreadyThere} copiedFromDecrypted={copiedFromDecrypted} rawFromPak={rawFromPak} rawFromPakFail={rawFromPakFail} of {provider.Files.Count} in {sw.Elapsed:mm\\:ss}");
        return rawFromPakFail > 0 ? 2 : 0;
    }
    case "exportgltf":
    {
        // Export every mesh as self-contained glTF 2.0 (.glb) with real
        // materials/textures embedded -- unlike ActorX (.psk), glTF is a
        // format Unreal Engine 5's own Interchange import pipeline reads
        // natively, no third-party plugin needed. Scoped to mesh-bearing
        // packages only (StaticMesh/SkinnedAsset/GeometryCollection) since
        // this output exists specifically to be bulk-imported into a new
        // UE project, not as a general archive dump.
        if (args.Length < 2) { Console.WriteLine("Usage: exportgltf <outDir> [pathFilter]"); return 1; }
        var outDir = args[1];
        var filter = args.Length > 2 ? args[2] : null;
        var options = Exporting.Options(EMeshFormat.Gltf2);
        const int BatchSize = 250;
        var dop = Math.Min(4, Environment.ProcessorCount);

        int queuedMesh = 0, skippedExisting = 0, processed = 0, packageLoadFail = 0, exportedTotal = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var batch = new List<CUE4Parse.UE4.Assets.Exports.UObject>();
        async Task FlushBatchAsync()
        {
            if (batch.Count == 0) return;
            var session = new CUE4Parse_Conversion.ExportSession(null!) { MaxDegreeOfParallelism = dop };
            foreach (var export in batch)
            {
                try { session.Add(export); } catch (Exception) { /* skip this export, keep going */ }
            }
            var results = await session.RunAsync(outDir, options, null, CancellationToken.None);
            exportedTotal += results.Count;
            Console.WriteLine($"BATCH DONE: +{results.Count} file(s) (exportedTotal={exportedTotal}) elapsed={sw.Elapsed:mm\\:ss}");
            batch.Clear();
        }

        foreach (var (path, entry) in provider.Files)
        {
            if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.IsUePackagePayload) continue;
            if (!entry.IsUePackage) continue;
            processed++;

            var outStemDir = Path.Combine(outDir, Path.GetDirectoryName(path) ?? "");
            var outStem = Path.GetFileNameWithoutExtension(path);
            if (Directory.Exists(outStemDir) && Directory.EnumerateFiles(outStemDir, outStem + ".glb").Any())
            {
                skippedExisting++;
                continue;
            }

            try
            {
                var pkg = provider.LoadPackage(entry);
                foreach (var export in pkg.GetExports())
                {
                    if (!IsMeshExport(export)) continue;
                    batch.Add(export);
                    queuedMesh++;
                }
            }
            catch (Exception e)
            {
                packageLoadFail++;
                Console.WriteLine($"  PACKAGE LOAD FAIL {path} -> {e.GetType().Name}: {e.Message}");
            }

            if (batch.Count >= BatchSize) await FlushBatchAsync();

            if (processed % 5000 == 0)
                Console.WriteLine($"QUEUE PROGRESS: {processed}/{provider.Files.Count} scanned (queuedMesh={queuedMesh} loadFail={packageLoadFail} skipped={skippedExisting} exportedTotal={exportedTotal}) elapsed={sw.Elapsed:mm\\:ss}");
        }
        await FlushBatchAsync();
        Console.WriteLine($"DONE: exported {exportedTotal} file(s) to {outDir} ({queuedMesh} meshes queued, {processed} scanned, {packageLoadFail} loadFail, {skippedExisting} skipped) in {sw.Elapsed:mm\\:ss}");
        return packageLoadFail > 0 ? 2 : 0;
    }
    case "exportconverted":
    {
        // Export EVERY mounted file, converting each into its correct
        // UE-native / commonly-accepted format depending on its actual type
        // -- exactly what FModel's own bulk "Export Folder" does under the
        // hood: real UE package exports (UTexture -> PNG, UStaticMesh/
        // USkinnedAsset -> FBX via ActorX, USkeleton, UAnimationAsset,
        // UWorld, landscapes, splines, materials, DNA, pose assets) go
        // through CUE4Parse-Conversion's typed exporters; anything with no
        // dedicated exporter (sounds, blueprints, data tables, curves, loose
        // non-package files like .ini/.uplugin) falls back to a raw-bytes
        // copy via RawDataExporter, same as the "raw" export FModel itself
        // uses for unsupported types. See ExportSession.Add(UObject) in
        // CUE4Parse-Conversion/ExportSession.cs for the exact dispatch table
        // this mirrors.
        if (args.Length < 2) { Console.WriteLine("Usage: exportconverted <outDir> [pathFilter]"); return 1; }
        var outDir = args[1];
        var filter = args.Length > 2 ? args[2] : null;
        var options = Exporting.Options(EMeshFormat.ActorX);
        var session = new CUE4Parse_Conversion.ExportSession(null!)
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        int queuedTyped = 0, queuedRaw = 0, queuedAudio = 0, queuedJson = 0, packageLoadFail = 0, processed = 0, skippedExisting = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (path, entry) in provider.Files)
        {
            if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            processed++;
            if (entry.IsUePackagePayload)
                continue; // .uexp/.ubulk/.uptnl siblings, pulled in automatically with their .uasset

            // Resume support: skip anything that already has REAL converted
            // output on disk from a prior run. A raw package dump always
            // consists of the standard UE payload extensions (.uasset/.uexp/
            // .ubulk/.uptnl) -- those don't count as "done" here, only a file
            // with some OTHER extension (a real conversion: .png/.psk/.json/
            // .ogg/etc) does, so previously-raw-dumped packages (sounds,
            // blueprints, data assets) get reprocessed into their proper
            // format instead of being skipped as already done. Loose
            // non-package files (.ini/.uplugin/etc) are different: raw IS
            // their correct final form, so any existing match means done.
            var isPkg = entry.IsUePackage;
            var outStemDir = Path.Combine(outDir, Path.GetDirectoryName(path) ?? "");
            var outStem = Path.GetFileNameWithoutExtension(path);
            var rawPackageExts = new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" };
            var alreadyDone = Directory.Exists(outStemDir) && Directory.EnumerateFiles(outStemDir, outStem + ".*")
                .Any(f => !isPkg || !rawPackageExts.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (alreadyDone)
            {
                skippedExisting++;
                if (processed % 5000 == 0)
                    Console.WriteLine($"QUEUE PROGRESS: {processed}/{provider.Files.Count} scanned (typed={queuedTyped} audio={queuedAudio} json={queuedJson} raw={queuedRaw} loadFail={packageLoadFail} skipped={skippedExisting}) elapsed={sw.Elapsed:mm\\:ss}");
                continue;
            }

            if (isPkg)
            {
                try
                {
                    var pkg = provider.LoadPackage(entry);
                    // Only fall back to a raw dump if NONE of this package's
                    // exports matched a typed converter -- ExportSession
                    // dedupes enqueued exporters by package path, so eagerly
                    // raw-dumping on the first unsupported sub-export (e.g. an
                    // AnimCurveMetaData sitting next to the real mesh export)
                    // would silently steal the slot and drop the real,
                    // convertible export instead of converting it.
                    var anyTyped = false;
                    foreach (var export in pkg.GetExports())
                    {
                        try { session.Add(export); anyTyped = true; queuedTyped++; continue; }
                        catch (NotSupportedException) { /* no dedicated mesh/texture/etc exporter -- try audio, then JSON properties, below */ }

                        if (export is CUE4Parse.UE4.Assets.Exports.Sound.USoundWave or CUE4Parse.UE4.Assets.Exports.Sound.Node.USoundNodeWave or CUE4Parse.UE4.Assets.Exports.Wwise.UAkMediaAssetData)
                        {
                            try
                            {
                                CUE4Parse_Conversion.Sounds.SoundDecoder.Decode(export, true, out var audioFormat, out var audioData);
                                if (audioData != null && audioData.Length > 0 && !string.IsNullOrEmpty(audioFormat))
                                {
                                    var audioOutPath = Path.Combine(outDir, Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "." + audioFormat.ToLowerInvariant());
                                    Directory.CreateDirectory(Path.GetDirectoryName(audioOutPath)!);
                                    File.WriteAllBytes(audioOutPath, audioData);
                                    anyTyped = true; queuedAudio++;
                                    continue;
                                }
                            }
                            catch (Exception) { /* fall through to JSON properties */ }
                        }

                        try { session.Add(new CUE4Parse_Conversion.Exporters.JsonPropertiesExporter(export)); anyTyped = true; queuedJson++; }
                        catch { /* genuinely unexportable; leave for the package-level raw fallback below */ }
                    }
                    if (!anyTyped)
                    {
                        try { session.Add(new CUE4Parse_Conversion.Exporters.RawDataExporter(entry, provider)); queuedRaw++; }
                        catch { }
                    }
                }
                catch (Exception e)
                {
                    packageLoadFail++;
                    Console.WriteLine($"  PACKAGE LOAD FAIL {path} -> {e.GetType().Name}: {e.Message}");
                    try { session.Add(new CUE4Parse_Conversion.Exporters.RawDataExporter(entry, provider)); queuedRaw++; }
                    catch { }
                }
            }
            else
            {
                try { session.Add(new CUE4Parse_Conversion.Exporters.RawDataExporter(entry, provider)); queuedRaw++; }
                catch { }
            }

            if (processed % 5000 == 0)
                Console.WriteLine($"QUEUE PROGRESS: {processed}/{provider.Files.Count} scanned (typed={queuedTyped} audio={queuedAudio} json={queuedJson} raw={queuedRaw} loadFail={packageLoadFail} skipped={skippedExisting}) elapsed={sw.Elapsed:mm\\:ss}");
        }
        Console.WriteLine($"Queued: typed={queuedTyped} audio={queuedAudio} json={queuedJson} raw={queuedRaw} packageLoadFail={packageLoadFail} skippedExisting={skippedExisting} of {provider.Files.Count} files scanned in {sw.Elapsed:mm\\:ss}");

        int lastReported = 0;
        var progress = new Progress<CUE4Parse_Conversion.ExportProgress>(p =>
        {
            if (p.Completed - lastReported >= 1000 || p.Completed == p.Total)
            {
                lastReported = p.Completed;
                Console.WriteLine($"EXPORT PROGRESS: {p.Completed}/{p.Total} elapsed={sw.Elapsed:mm\\:ss}");
            }
        });
        var results = await session.RunAsync(outDir, options, progress, CancellationToken.None);
        Console.WriteLine($"DONE: exported {results.Count} file(s) to {outDir} in {sw.Elapsed:mm\\:ss}");
        return packageLoadFail > 0 ? 2 : 0;
    }
    case "exporttexture":
    {
        // Decode a texture directly via CUE4Parse-Conversion (the same
        // decode library FModel itself uses under the hood) and save as
        // PNG. This is a headless decode-and-save with no interactive
        // render-target/preview size cap, so it works for textures too
        // large for FModel's own preview pane to display. Loads as the
        // base UTexture (not UTexture2D specifically) since TextureExporter
        // itself takes UTexture -- covers UTexture2D, UTextureCube,
        // UTexture2DArray, etc. without needing a separate code path per type.
        if (args.Length < 3) { Console.WriteLine("Usage: exporttexture <assetPath> <outDir>"); return 1; }
        var path = args[1];
        var outDir = args[2];
        var texture = provider.LoadPackageObject<CUE4Parse.UE4.Assets.Exports.Texture.UTexture>(path);
        var options = Exporting.Options(EMeshFormat.ActorX);
        var session = new CUE4Parse_Conversion.ExportSession(null!);
        session.Add(texture);
        var progress = new Progress<CUE4Parse_Conversion.ExportProgress>();
        var results = await session.RunAsync(outDir, options, progress, CancellationToken.None);
        Console.WriteLine($"Exported {results.Count} file(s) to {outDir}");
        break;
    }
    case "decompileblueprints":
    {
        // Decompile every Blueprint class in the archive to readable
        // pseudo-C++, using the exact same CUE4Parse API FModel's own
        // "Decompile" feature uses (UClass.DecompileBlueprintToPseudo).
        // For Blueprint-heavy games this is where the gameplay logic lives:
        // visual-scripted logic compiled into the pak files themselves, not
        // the (engine-generic) main executable.
        if (args.Length < 2) { Console.WriteLine("Usage: decompileblueprints <outDir> [pathFilter]"); return 1; }
        var outDir = args[1];
        var filter = args.Length > 2 ? args[2] : null;
        int processed = 0, decompiled = 0, fail = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (path, entry) in provider.Files)
        {
            if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.IsUePackagePayload) continue;
            if (!entry.IsUePackage) continue;
            processed++;
            try
            {
                var pkg = provider.LoadPackage(entry);
                var cppList = new List<string>();
                for (var i = 0; i < pkg.ExportMapLength; i++)
                {
                    var pointer = new CUE4Parse.UE4.Objects.UObject.FPackageIndex(pkg, i + 1).ResolvedObject;
                    if (pointer?.Object is null && pointer?.Class?.Object?.Value is null) continue;

                    var dummy = ((CUE4Parse.UE4.Assets.AbstractUePackage)pkg).ConstructObject(pointer.Class, pkg);
                    if (dummy is not CUE4Parse.UE4.Objects.UObject.UClass || pointer.Object!.Value is not CUE4Parse.UE4.Objects.UObject.UClass blueprint) continue;

                    cppList.Add(blueprint.DecompileBlueprintToPseudo());
                }
                if (cppList.Count == 0) continue;
                var cpp = cppList.Count > 1 ? string.Join("\n\n", cppList) : cppList[0];
                cpp = System.Text.RegularExpressions.Regex.Replace(cpp, @"CallFunc_([A-Za-z0-9_]+)_ReturnValue", "$1");
                cpp = System.Text.RegularExpressions.Regex.Replace(cpp, @"K2Node_DynamicCast_([A-Za-z0-9_]+)", "$1");
                cpp = System.Text.RegularExpressions.Regex.Replace(cpp, @"K2Node_([A-Za-z0-9_]+)", "$1");

                var outPath = SafeJoin(outDir, Path.ChangeExtension(path, ".cpp"));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllText(outPath, cpp);
                decompiled++;
            }
            catch (Exception e) { fail++; Console.WriteLine($"  FAIL {path} -> {e.GetType().Name}: {e.Message}"); }
            if (processed % 5000 == 0)
                Console.WriteLine($"PROGRESS: {processed} scanned (decompiled={decompiled} fail={fail}) elapsed={sw.Elapsed:mm\\:ss}");
        }
        Console.WriteLine($"DONE: decompiled={decompiled} fail={fail} of {processed} packages scanned in {sw.Elapsed:mm\\:ss}");
        return fail > 0 ? 2 : 0;
    }
    case "dumplevel":
    {
        // Inspect how actor placement is stored in a .umap package: class,
        // transform, mesh reference. Useful before writing level-reconstruction
        // code against a guess.
        if (args.Length < 2) { Console.WriteLine("Usage: dumplevel <mapPath> [maxActors] [classFilter]"); return 1; }
        var path = args[1];
        var maxActors = 5;
        if (args.Length > 2 && !int.TryParse(args[2], out maxActors))
        {
            Console.WriteLine($"Usage: dumplevel <mapPath> [maxActors] [classFilter] -- '{args[2]}' is not a number");
            return 1;
        }
        var classFilter = args.Length > 3 ? args[3] : null;
        var pkg = provider.LoadPackage(path);
        Console.WriteLine($"ExportMapLength={pkg.ExportMapLength}");
        var byName = new Dictionary<string, CUE4Parse.UE4.Assets.Exports.UObject>();
        var classCounts = new Dictionary<string, int>();
        foreach (var export in pkg.GetExports())
        {
            byName[export.Name] = export;
            var cls = export.Class?.Name.ToString() ?? "?";
            classCounts[cls] = classCounts.GetValueOrDefault(cls, 0) + 1;
        }
        Console.WriteLine("--- class counts ---");
        foreach (var kv in classCounts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kv.Value,6}  {kv.Key}");

        Console.WriteLine("--- sample actor-like exports (properties) ---");
        int shown = 0;
        foreach (var export in pkg.GetExports())
        {
            var cls = export.Class?.Name.ToString() ?? "";
            if (classFilter != null) { if (!cls.Equals(classFilter, StringComparison.OrdinalIgnoreCase)) continue; }
            else if (!(cls.StartsWith('A') || cls.EndsWith("_C"))) continue;
            if (shown >= maxActors) break;
            shown++;
            Console.WriteLine($"=== {export.Name} (Class={cls}) ===");
            foreach (var tag in export.Properties)
            {
                object? val;
                try { val = tag.Tag?.GenericValue; } catch (Exception e) { val = $"<threw {e.GetType().Name}: {e.Message}>"; }
                Console.WriteLine($"  {tag.Name} ({tag.PropertyType}) = {val}");
            }

            var rootComp = export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("RootComponent", null!);
            if (rootComp != null && rootComp.IsExport)
            {
                var comp = rootComp.ResolvedObject?.Object?.Value;
                Console.WriteLine($"  --> RootComponent resolves to: {comp?.Name} (Class={(comp as CUE4Parse.UE4.Assets.Exports.UObject)?.Class?.Name})");
                if (comp is CUE4Parse.UE4.Assets.Exports.UObject compObj)
                {
                    foreach (var tag in compObj.Properties)
                    {
                        object? val;
                        try { val = tag.Tag?.GenericValue; } catch (Exception e) { val = $"<threw {e.GetType().Name}: {e.Message}>"; }
                        Console.WriteLine($"    [component] {tag.Name} ({tag.PropertyType}) = {val}");
                    }
                }
            }
        }
        break;
    }
    case "exportworld":
    {
        // CUE4Parse-Conversion ships a full built-in level/world exporter
        // (WorldExporter -> UsdWorldFormat) that walks the actual UWorld
        // actor hierarchy natively, including resolving
        // InstancedStaticMeshComponent per-instance transforms (via its own
        // typed UInstancedStaticMeshComponent.PerInstanceSMData, not the
        // generic tagged-property system) and LevelInstance/packed-Blueprint
        // sub-actors recursively. This is a completely different, far more
        // complete path than the StaticMeshActor-only dumpplacements approach.
        if (args.Length < 3) { Console.WriteLine("Usage: exportworld <mapPath> <outDir>"); return 1; }
        var path = args[1];
        var outDir = args[2];
        var pkg = provider.LoadPackage(path);
        var worldExport = pkg.GetExports().FirstOrDefault(e => e.Class?.Name.ToString() == "World");
        if (worldExport == null) { Console.WriteLine("No UWorld export found in package"); return 2; }
        Console.WriteLine($"Found World export: {worldExport.Name} (CLR type={worldExport.GetType().FullName})");
        var options = Exporting.Options(EMeshFormat.USD, exportMaterials: true);
        var session = new CUE4Parse_Conversion.ExportSession(null!) { MaxDegreeOfParallelism = 1 };
        session.Add(worldExport);
        var results = await session.RunAsync(outDir, options, null, CancellationToken.None);
        Console.WriteLine($"DONE: exported {results.Count} file(s) to {outDir}");
        break;
    }
    case "dumpworld":
    {
        // Full level graph -> JSON, resolving Blueprint SCS templates for the
        // ISM geometry `exportworld` can drop. See WorldDump.cs.
        if (args.Length < 3) { Console.WriteLine("Usage: dumpworld <mapPath> <outFile.json> [meshOutDir]  -- meshOutDir: also export every referenced mesh as USD with materials/textures"); return 1; }
        WorldDump.Run(provider, args[1], args[2], args.Length > 3 ? args[3] : null);
        break;
    }
    case "dumpplacements":
    {
        // Extract every directly-placed StaticMeshActor from a .umap: mesh
        // reference + exact RootComponent transform. This is only the
        // statically-recoverable subset -- InstancedStaticMeshComponents
        // spawned by a Blueprint's SimpleConstructionScript hold no
        // serialized transform data here, since that geometry is generated
        // at runtime when the construction script executes. Use `dumpworld`
        // for the full picture.
        if (args.Length < 3) { Console.WriteLine("Usage: dumpplacements <mapPath> <outFile.json>"); return 1; }
        var path = args[1];
        var outFile = args[2];
        var pkg = provider.LoadPackage(path);
        var placements = new List<object>();
        int total = 0, noMesh = 0, engineMesh = 0;
        foreach (var export in pkg.GetExports())
        {
            var cls = export.Class?.Name.ToString() ?? "";
            if (cls != "StaticMeshActor") continue;
            total++;
            var rootComp = export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("RootComponent", null!);
            var comp = rootComp?.ResolvedObject?.Object?.Value as CUE4Parse.UE4.Assets.Exports.UObject;
            if (comp == null) { noMesh++; continue; }

            string? meshRef = null;
            foreach (var tag in comp.Properties)
            {
                if (tag.Name.ToString() != "StaticMesh") continue;
                var s = tag.Tag?.GenericValue?.ToString();
                var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"'([^.']+)\.");
                if (m.Success) meshRef = m.Groups[1].Value;
            }
            if (meshRef == null) { noMesh++; continue; }
            if (meshRef.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase)) { engineMesh++; continue; }

            var loc = comp.GetOrDefault("RelativeLocation", new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0));
            var rot = comp.GetOrDefault("RelativeRotation", new CUE4Parse.UE4.Objects.Core.Math.FRotator(0, 0, 0));
            var scale = comp.GetOrDefault("RelativeScale3D", new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1));

            placements.Add(new
            {
                name = export.Name,
                meshPath = meshRef,
                location = new { x = loc.X, y = loc.Y, z = loc.Z },
                rotation = new { pitch = rot.Pitch, yaw = rot.Yaw, roll = rot.Roll },
                scale = new { x = scale.X, y = scale.Y, z = scale.Z }
            });
        }
        var json = System.Text.Json.JsonSerializer.Serialize(placements, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outFile, json);
        Console.WriteLine($"DONE: {placements.Count} placements written to {outFile} (total StaticMeshActor={total}, noMesh={noMesh}, engineMesh={engineMesh})");
        break;
    }
    case "dumpexports":
    {
        if (args.Length < 2) { Console.WriteLine("Usage: dumpexports <pkgPath>"); return 1; }
        var path = args[1];
        var pkg = provider.LoadPackage(path);
        Console.WriteLine($"ExportMapLength={pkg.ExportMapLength}");
        foreach (var export in pkg.GetExports())
        {
            Console.WriteLine($"  {export.Name} -- CLR type={export.GetType().FullName} ExportType={export.ExportType} Class={export.Class?.Name}");
        }
        break;
    }
    case "dumplocres":
    {
        // Print the game's localized strings via CUE4Parse's .locres reader
        // (namespace/key/value), preferring an English culture when available.
        var ifp = (CUE4Parse.FileProvider.IFileProvider)provider;
        var intl = ifp.Internationalization;
        Console.WriteLine($"AvailableCultures: [{string.Join(", ", intl.AvailableCultures)}]");
        Console.WriteLine($"LocalizationPaths: [{string.Join(", ", intl.LocalizationPaths)}]");
        Console.WriteLine($"CultureMappings: [{string.Join(", ", intl.CultureMappings.Select(kv => $"{kv.Key}->{kv.Value}"))}]");
        Console.WriteLine($"Current Culture: {intl.Culture}");

        var cultureToTry = intl.AvailableCultures.FirstOrDefault(c => c.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? intl.AvailableCultures.FirstOrDefault();
        if (cultureToTry != null)
        {
            Console.WriteLine($"Setting culture to: {cultureToTry}");
            // InternationalizationDictionary.Culture has no public setter in CUE4Parse 1.2.2 -- reflection is the only way in.
            var setter = intl.GetType().GetMethod("set_Culture");
            setter?.Invoke(intl, new object[] { cultureToTry });
        }
        Console.WriteLine($"LocalizedResources dictionary count: {intl.Count}");

        var filter = args.Length > 1 ? args[1] : null;
        int shown = 0;
        var truncated = false;
        foreach (var (ns, table) in intl)
        {
            foreach (var (key, value) in table)
            {
                if (filter != null &&
                    !ns.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !key.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                Console.WriteLine($"  [{ns}] {key} = \"{value}\"");
                shown++;
                if (shown >= 200) { truncated = true; break; }
            }
            if (truncated) break;
        }
        if (truncated) Console.WriteLine("  ...(truncated at 200)");
        Console.WriteLine($"Shown: {shown}");
        break;
    }
    default:
        Console.WriteLine($"Unknown command: {args[0]}");
        return 1;
}

}
catch (Exception e)
{
    Console.Error.WriteLine($"ERROR: {e.GetType().Name}: {e.Message}");
    return 2;
}

return 0;

static bool IsMeshExport(object export)
{
    for (var t = export.GetType(); t != null; t = t.BaseType)
    {
        switch (t.Name)
        {
            case "UStaticMesh":
            case "USkinnedAsset":
            case "USkeletalMesh":
            case "UGeometryCollection":
                return true;
        }
    }
    return false;
}

static string SafeJoin(string root, string relative)
{
    var full = Path.GetFullPath(Path.Combine(root, relative));
    var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full
        : throw new InvalidOperationException($"refusing to write outside {root}: {relative}");
}