using System.IO;
using System.Runtime.InteropServices;

namespace CClean.Services;

/// <summary>文件系统辅助：安全地计算目录大小、查询回收站等。全部只读，不删除任何东西。</summary>
public static class FsUtil
{
    /// <summary>
    /// 递归统计一个目录占用的字节数。
    /// 关键点：逐目录 try/catch（跳过没权限的），并跳过符号链接/junction（避免兜圈子），
    /// 这样扫描永远不会因为某个目录报错而整体崩掉。
    /// </summary>
    public static long DirSize(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;

        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            DirectoryInfo di;
            try { di = new DirectoryInfo(current); }
            catch { continue; }

            // 跳过 junction / 符号链接，否则可能重复计算甚至死循环
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            try
            {
                foreach (var f in di.EnumerateFiles())
                {
                    try { total += f.Length; } catch { /* 文件被占用/无权限，跳过 */ }
                }
                foreach (var sub in di.EnumerateDirectories())
                    stack.Push(sub.FullName);
            }
            catch { /* 该目录无权限，跳过 */ }
        }
        return total;
    }

    /// <summary>把多个目录的大小加起来。</summary>
    public static long DirSize(IEnumerable<string> paths, CancellationToken ct)
    {
        long total = 0;
        foreach (var p in paths) total += DirSize(p, ct);
        return total;
    }

    // ===== 回收站大小：用 Shell 接口查询，比直接读 $Recycle.Bin 可靠 =====
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>查询指定盘符回收站的占用大小（字节）。失败返回 0。</summary>
    public static long RecycleBinSize(string rootPath)
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            int hr = SHQueryRecycleBin(rootPath, ref info);
            return hr == 0 ? info.i64Size : 0;
        }
        catch { return 0; }
    }
}
