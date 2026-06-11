using System.Collections.Concurrent;
using System.IO;
using CClean.Models;

namespace CClean.Services;

/// <summary>
/// 空间分析：给定一个目录，算出它每个直接子项（子文件夹/文件）的真实大小。
/// 文件夹大小是递归统计的，比较慢，所以这里并行计算并缓存——同一目录再进就秒开。
/// 全程只读，不删除任何东西。
/// </summary>
public static class SpaceAnalyzer
{
    // 缓存：目录路径 -> 它的子项列表（已按大小排序）
    private static readonly ConcurrentDictionary<string, List<DirNode>> _cache = new();

    /// <summary>清空缓存（删除文件后需要刷新时调用）。</summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>
    /// 取某个目录下所有子项及其大小，按从大到小排序。
    /// progress 会汇报当前正在统计的子文件夹名（用于界面提示）。
    /// </summary>
    public static List<DirNode> GetChildren(string dir, CancellationToken ct, IProgress<string>? progress = null)
    {
        if (_cache.TryGetValue(dir, out var cached)) return cached;

        var nodes = new ConcurrentBag<DirNode>();

        // 1) 子文件夹：每个递归求大小，多个文件夹并行算，加速明显
        var subDirs = SafeEnum(() => Directory.EnumerateDirectories(dir)).ToList();
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
        };
        Parallel.ForEach(subDirs, options, sd =>
        {
            progress?.Report(Path.GetFileName(sd));
            long size = FsUtil.DirSize(sd, ct);
            if (size > 0)
                nodes.Add(new DirNode { FullPath = sd, Name = Path.GetFileName(sd), SizeBytes = size, IsDirectory = true });
        });

        // 2) 直接位于该目录下的文件（包含 hiberfil.sys / pagefile.sys 这类大块系统文件）
        foreach (var f in SafeEnum(() => Directory.EnumerateFiles(dir)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length > 0)
                    nodes.Add(new DirNode { FullPath = f, Name = fi.Name, SizeBytes = fi.Length, IsDirectory = false });
            }
            catch { /* 被占用/无权限，跳过 */ }
        }

        var result = nodes.OrderByDescending(n => n.SizeBytes).ToList();
        _cache[dir] = result;
        return result;
    }

    private static IEnumerable<string> SafeEnum(Func<IEnumerable<string>> getter)
    {
        try { return getter().ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }
}
