using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

// Why this exists: `exportworld` (CUE4Parse's USD WorldExporter) can emit
// Blueprint-placed actors as empty Xforms. Cooked levels store only
// the per-instance *deltas* of a Blueprint actor's SimpleConstructionScript
// components (InstancingRandomSeed, AttachParent) -- the StaticMesh reference
// and the baked PerInstanceSMData live on the SCS ComponentTemplate inside the
// Blueprint asset itself. This walks the full streaming-level graph, merges
// instance-over-template per component, composes the attach chain into world
// transforms, and writes one JSON the UE editor script can spawn from.
static class WorldDump
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static void Run(DefaultFileProvider provider, string rootPath, string outFile, string? meshOutDir = null)
    {
        var meshPaths = new HashSet<string>();
        var levels = new List<string>();
        var actorsOut = new List<object>();
        var classCounts = new SortedDictionary<string, int>();
        var templateCache = new Dictionary<string, Dictionary<string, UObject>?>();
        var queue = new Queue<(string path, FTransform offset)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int instTotal = 0, meshComps = 0, noMesh = 0;
        queue.Enqueue((rootPath, Identity()));

        while (queue.Count > 0)
        {
            var (lvlPath, levelOffset) = queue.Dequeue();
            if (!seen.Add(lvlPath)) continue;
            IPackage pkg;
            try { pkg = provider.LoadPackage(lvlPath); }
            catch (Exception e) { Console.WriteLine($"SKIP level {lvlPath}: {e.Message}"); continue; }
            var exports = pkg.GetExports().ToList();
            var world = exports.OfType<UWorld>().FirstOrDefault();
            if (world == null) { Console.WriteLine($"SKIP {lvlPath}: no UWorld export"); continue; }
            levels.Add(lvlPath);

            // Streaming sublevels: typed list first, class-name scan as fallback.
            var streaming = new List<UObject>();
            foreach (var sl in world.StreamingLevels ?? []) { var o = SafeLoad(sl); if (o != null) streaming.Add(o); }
            if (streaming.Count == 0)
                streaming.AddRange(exports.Where(e => (e.Class?.Name.Text ?? "").StartsWith("LevelStreaming")));
            foreach (var sl in streaming)
            {
                if (!sl.TryGetValue<FSoftObjectPath>(out var wa, "WorldAsset")) continue;
                var sub = ToPakPath(provider, wa.AssetPathName.Text);
                if (sub == null) continue;
                var off = Identity();
                if (sl.TryGetValue<FTransform>(out var lt, "LevelTransform")) off = lt;
                queue.Enqueue((sub, off * levelOffset));
            }

            // Group exports by outer so an actor's whole component subtree is findable.
            var childrenByOuter = new Dictionary<int, List<int>>();
            for (int i = 0; i < exports.Count; i++)
            {
                var outer = exports[i].Outer;
                if (outer == null || outer.Package != pkg) continue;
                if (!childrenByOuter.TryGetValue(outer.ExportIndex, out var list)) childrenByOuter[outer.ExportIndex] = list = new();
                list.Add(i);
            }

            var level = world.PersistentLevel.Load<ULevel>();
            var actorIdxs = level?.Actors?.Where(a => a?.IsExport == true).Select(a => a!.Index - 1).ToList() ?? new List<int>();
            Console.WriteLine($"LEVEL {lvlPath}: {actorIdxs.Count} actors");

            foreach (var ai in actorIdxs)
            {
                if (ai < 0 || ai >= exports.Count) continue;
                var actor = exports[ai];
                var clsName = actor.Class?.Name.Text ?? "?";
                classCounts[clsName] = classCounts.GetValueOrDefault(clsName) + 1;

                var clsPath = actor.Class?.GetPathName() ?? "";
                if (!templateCache.TryGetValue(clsPath, out var templates))
                    templateCache[clsPath] = templates = CollectScsTemplates(actor.Class);

                // Subtree of component exports under this actor.
                var compIdxs = new List<int>();
                var stack = new Stack<int>(); stack.Push(ai);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    if (!childrenByOuter.TryGetValue(cur, out var kids)) continue;
                    foreach (var k in kids) { compIdxs.Add(k); stack.Push(k); }
                }
                var compByName = new Dictionary<string, int>(); foreach (var i in compIdxs) compByName.TryAdd(exports[i].Name, i);
                var rootIdx = actor.TryGetValue<FPackageIndex>(out var rc, "RootComponent") && rc.IsExport ? rc.Index - 1 : -1;

                var worldMemo = new Dictionary<int, FTransform>();
                FTransform WorldOf(int ci, int depth)
                {
                    if (worldMemo.TryGetValue(ci, out var w)) return w;
                    var comp = exports[ci];
                    var tmpl = FindTemplate(templates, comp.Name);
                    var rel = RelativeOf(comp, tmpl);
                    int parent = -1;
                    if (Merged<FPackageIndex>(comp, tmpl, "AttachParent") is { } ap && !ap.IsNull)
                    {
                        if (ap.IsExport && ap.ResolvedObject?.Package == pkg) parent = ap.Index - 1;
                        else
                        {
                            // Template-side parent lives in the BP package as Foo_GEN_VARIABLE.
                            var pn = ap.Name.Replace("_GEN_VARIABLE", "");
                            if (compByName.TryGetValue(pn, out var pi) && pi != ci) parent = pi;
                        }
                    }
                    w = parent >= 0 && depth < 32 && compIdxs.Contains(parent) ? rel * WorldOf(parent, depth + 1) : rel * levelOffset;
                    worldMemo[ci] = w;
                    return w;
                }

                var compsOut = new List<object>();
                foreach (var ci in compIdxs)
                {
                    var comp = exports[ci];
                    var tmpl = FindTemplate(templates, comp.Name);
                    var mesh = PathOf(Merged<FPackageIndex>(comp, tmpl, "StaticMesh"));
                    if (mesh != null) meshPaths.Add(mesh);
                    var decal = PathOf(Merged<FPackageIndex>(comp, tmpl, "DecalMaterial"));
                    if (decal != null) meshPaths.Add(decal); // materials export as mi.usda + textures too
                    bool hasXf = comp.TryGetValue<FVector>(out _, "RelativeLocation") || (tmpl?.TryGetValue<FVector>(out _, "RelativeLocation") ?? false);
                    var isMeshType = comp is UStaticMeshComponent || (comp.Class?.Name.Text ?? "").Contains("Mesh");
                    if (mesh == null && decal == null && !hasXf && ci != rootIdx && !isMeshType) continue;

                    var wt = WorldOf(ci, 0);
                    var inst = (comp as UInstancedStaticMeshComponent)?.PerInstanceSMData;
                    if (inst == null || inst.Length == 0) inst = (tmpl as UInstancedStaticMeshComponent)?.PerInstanceSMData;
                    List<float[]>? instOut = null;
                    if (inst != null && inst.Length > 0)
                    {
                        instOut = new List<float[]>(inst.Length);
                        foreach (var d in inst) instOut.Add(Pack(InstanceTransform(d) * wt));
                        instTotal += inst.Length;
                    }
                    if (isMeshType) { if (mesh != null) meshComps++; else noMesh++; }

                    var mats = Merged<FPackageIndex[]>(comp, tmpl, "OverrideMaterials")?.Select(PathOf).ToArray();
                    object? spline = null;
                    if (Merged<FStructFallback>(comp, tmpl, "SplineParams") is { } sp)
                        spline = new
                        {
                            startPos = V(sp.GetOrDefault<FVector>("StartPos")), startTangent = V(sp.GetOrDefault<FVector>("StartTangent")),
                            endPos = V(sp.GetOrDefault<FVector>("EndPos")), endTangent = V(sp.GetOrDefault<FVector>("EndTangent")),
                            startScale = V2(sp.GetOrDefault("StartScale", new FVector2D(1, 1))), endScale = V2(sp.GetOrDefault("EndScale", new FVector2D(1, 1))),
                            startRoll = sp.GetOrDefault<float>("StartRoll"), endRoll = sp.GetOrDefault<float>("EndRoll")
                        };

                    compsOut.Add(new
                    {
                        name = comp.Name,
                        type = comp.Class?.Name.Text,
                        isRoot = ci == rootIdx,
                        world = Pack(wt),
                        mesh, decal,
                        materials = mats,
                        instances = instOut,
                        spline
                    });
                }

                string? worldAsset = actor.TryGetValue<FSoftObjectPath>(out var awa, "WorldAsset") ? awa.AssetPathName.Text : null;
                actorsOut.Add(new
                {
                    name = actor.Name,
                    @class = clsName,
                    classPath = clsPath,
                    level = lvlPath,
                    world = rootIdx >= 0 ? Pack(WorldOf(rootIdx, 0)) : null,
                    worldAsset,
                    components = compsOut
                });
            }
        }

        var result = new { root = rootPath, levels, summary = new { actors = actorsOut.Count, meshComponents = meshComps, meshComponentsWithoutMesh = noMesh, instances = instTotal, classCounts }, actors = actorsOut };
        File.WriteAllText(outFile, JsonSerializer.Serialize(result, JsonOpts));
        Console.WriteLine($"DONE: {levels.Count} levels, {actorsOut.Count} actors, {meshComps} mesh components ({noMesh} without a resolvable mesh), {instTotal} ISM instances -> {outFile}");
        foreach (var kv in classCounts.OrderByDescending(kv => kv.Value).Take(25)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
        if (meshOutDir != null) ExportMeshes(provider, meshPaths, meshOutDir);
    }

    static void ExportMeshes(DefaultFileProvider provider, IEnumerable<string> meshPaths, string outDir)
    {
        // USD with materials is the export path that reliably carries textures
        // through a UE editor import (the glTF pass exports geometry only).
        var options = Exporting.Options(CUE4Parse_Conversion.Options.EMeshFormat.USD, exportMaterials: true);
        var session = new CUE4Parse_Conversion.ExportSession(null!) { MaxDegreeOfParallelism = 4 };
        int queued = 0, failed = 0;
        foreach (var mp in meshPaths.Distinct())
        {
            try { session.Add(provider.LoadPackageObject(mp)); queued++; }
            catch (Exception e) { failed++; Console.WriteLine($"  mesh load FAIL {mp}: {e.Message}"); }
        }
        var results = session.RunAsync(outDir, options, null, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"MESHES: queued={queued} loadFail={failed} -> {results.Count} file(s) in {outDir}");
    }

    // Walk the Blueprint class chain collecting SCS component templates keyed by
    // variable name (matches the level's component export name).
    static Dictionary<string, UObject>? CollectScsTemplates(ResolvedObject? cls)
    {
        Dictionary<string, UObject>? result = null;
        int guard = 0;
        while (cls != null && guard++ < 16)
        {
            UObject? clsObj;
            try { clsObj = cls.Object?.Value; } catch { break; }
            if (clsObj is not UBlueprintGeneratedClass bpgc) break;
            var scs = SafeLoad(bpgc.SimpleConstructionScript) as USimpleConstructionScript;
            if (scs != null)
            {
                result ??= new();
                foreach (var n in scs.AllNodes ?? Array.Empty<FPackageIndex>())
                {
                    if (SafeLoad(n) is not USCS_Node node) continue;
                    var tmpl = SafeLoad(node.ComponentTemplate);
                    if (tmpl == null) continue;
                    var key = node.InternalVariableName.Text;
                    if (string.IsNullOrEmpty(key) || key == "None") key = tmpl.Name.Replace("_GEN_VARIABLE", "");
                    result.TryAdd(key, tmpl); // child class wins over parent
                }
            }
            // UserConstructionScript "Add X Component" nodes keep their template
            // here, named NODE_AddFooComponent-0; level copies are suffixed _N.
            foreach (var ct in bpgc.ComponentTemplates ?? Array.Empty<FPackageIndex>())
                if (SafeLoad(ct) is { } t) (result ??= new()).TryAdd(t.Name, t);
            cls = cls.Super;
        }
        return result;
    }

    static UObject? FindTemplate(Dictionary<string, UObject>? templates, string compName)
    {
        if (templates == null) return null;
        if (templates.TryGetValue(compName, out var t)) return t;
        var m = System.Text.RegularExpressions.Regex.Match(compName, @"^(.*)_\d+$");
        return m.Success && templates.TryGetValue(m.Groups[1].Value, out t) ? t : null;
    }

    static UObject? SafeLoad(FPackageIndex? idx)
    {
        if (idx == null || idx.IsNull) return null;
        try { return idx.Load<UObject>(); } catch { return null; }
    }

    static T? Merged<T>(UObject inst, UObject? tmpl, string name)
    {
        try { if (inst.TryGetValue<T>(out var v, name)) return v; } catch { }
        try { if (tmpl != null && tmpl.TryGetValue<T>(out var v, name)) return v; } catch { }
        return default;
    }

    static FTransform RelativeOf(UObject inst, UObject? tmpl)
    {
        var loc = Merged<FVector>(inst, tmpl, "RelativeLocation");
        var rot = Merged<FRotator>(inst, tmpl, "RelativeRotation");
        var hasScale = inst.TryGetValue<FVector>(out var scl, "RelativeScale3D") || (tmpl?.TryGetValue(out scl, "RelativeScale3D") ?? false);
        return new FTransform(rot.Quaternion(), loc, hasScale ? scl : new FVector(1, 1, 1));
    }

    // Serialized as FMatrix44f; CUE4Parse exposes the derived FTransform.
    static FTransform InstanceTransform(FInstancedStaticMeshInstanceData d) => d.TransformData;

    static FTransform Identity() => new(FQuat.Identity, FVector.ZeroVector, new FVector(1, 1, 1));

    static string? PathOf(FPackageIndex? idx)
    {
        if (idx == null || idx.IsNull) return null;
        try { return idx.ResolvedObject?.GetPathName(); } catch { return idx.Name; }
    }

    // "/Game/Maps/City.City" -> "<Project>/Content/Maps/City" (FixPath resolves /Game/, /Engine/ and plugin mounts).
    static string? ToPakPath(DefaultFileProvider provider, string softPath)
    {
        if (string.IsNullOrEmpty(softPath)) return null;
        try { return Path.ChangeExtension(provider.FixPath(softPath.Split('.')[0]), null); }
        catch { return null; }
    }

    static float[] Pack(FTransform t) => new[] { t.Translation.X, t.Translation.Y, t.Translation.Z, t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W, t.Scale3D.X, t.Scale3D.Y, t.Scale3D.Z };
    static float[] V(FVector v) => new[] { v.X, v.Y, v.Z };
    static float[] V2(FVector2D v) => new[] { v.X, v.Y };
}
