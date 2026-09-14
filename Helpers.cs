using System.Text.RegularExpressions;

static partial class Helpers
{
    public const int ProgressSmall = 1000;
    public const int ProgressMedium = 5000;
    public const int ProgressLarge = 10000;
    public const int ExportBatchSize = 250;
    public const int MaxExportDop = 4;
    public const int BundleNodeCap = 200;
    public const int LocresDisplayCap = 200;

    public static readonly HashSet<string> RawPackageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".uasset", ".uexp", ".ubulk", ".uptnl" };

    public static bool MatchesFilter(string path, string? filter) =>
        filter is null || path.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public static string SafeJoin(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full
            : throw new InvalidOperationException($"refusing to write outside {root}: {relative}");
    }

    public static bool IsMeshExport(object export)
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

    [GeneratedRegex(@"CallFunc_([A-Za-z0-9_]+)_ReturnValue")]
    public static partial Regex CallFuncReturnValueRegex();

    [GeneratedRegex(@"K2Node_DynamicCast_([A-Za-z0-9_]+)")]
    public static partial Regex DynamicCastRegex();

    [GeneratedRegex(@"K2Node_([A-Za-z0-9_]+)")]
    public static partial Regex K2NodeRegex();
}

class FileIndex
{
    readonly Dictionary<string, HashSet<string>> _index = new(StringComparer.OrdinalIgnoreCase);

    public static FileIndex Build(string rootDir)
    {
        var fi = new FileIndex();
        if (!Directory.Exists(rootDir)) return fi;
        var rootFull = Path.GetFullPath(rootDir);
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(rootFull, file);
            var key = Path.Combine(Path.GetDirectoryName(rel) ?? "", Path.GetFileNameWithoutExtension(rel)).Replace('\\', '/');
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (!fi._index.TryGetValue(key, out var exts))
                fi._index[key] = exts = new(StringComparer.OrdinalIgnoreCase);
            exts.Add(ext);
        }
        return fi;
    }

    string KeyFor(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path)).Replace('\\', '/');

    public bool HasAnyFile(string relPath) => _index.ContainsKey(KeyFor(relPath));

    public bool HasConvertedFile(string relPath, bool isPackage)
    {
        if (!_index.TryGetValue(KeyFor(relPath), out var exts)) return false;
        if (!isPackage) return true;
        return exts.Any(e => !Helpers.RawPackageExts.Contains(e));
    }

    public bool HasFileWithExt(string relPath, string ext)
    {
        if (!_index.TryGetValue(KeyFor(relPath), out var exts)) return false;
        return exts.Contains(ext);
    }
}
