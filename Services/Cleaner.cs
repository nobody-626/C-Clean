using System.IO;
using System.Runtime.InteropServices;
using CClean.Models;

namespace CClean.Services;

/// <summary>一次清理的结果：释放了多少、删了几项、有哪些出错。</summary>
public record CleanResult(long FreedBytes, int DeletedCount, List<string> Errors);

/// <summary>
/// 真实删除。默认把文件送进回收站（可恢复），而不是彻底删除——这是关键的安全设计。
/// 🔴 OpenOnly 的项（聊天记录、已装程序）一律跳过，绝不自动删。
///
/// 删除统一走 Windows 的 Shell 接口 SHFileOperation，并打开"静默 + 不弹错误框"开关：
///  · 被占用的文件会被自动跳过，不会再弹"文件正在使用中"对话框；
///  · 一个被占用的文件也不会连累同目录里其它能删的文件。
/// </summary>
public static class Cleaner
{
    /// <summary>
    /// 清理选中的项。toRecycleBin=true 进回收站（默认）；false 才是永久删除。
    /// </summary>
    public static CleanResult Clean(IEnumerable<CleanupCategory> items, bool toRecycleBin,
        CancellationToken ct, IProgress<string>? progress = null)
    {
        long freed = 0;
        int count = 0;
        var errors = new List<string>();

        // 先处理"清空回收站"，再处理"送进回收站"的项，避免把刚送进去的又清掉
        var ordered = items
            .Where(i => i.CanAutoClean)
            .OrderBy(i => i.Action == CleanAction.EmptyRecycleBin ? 0 : 1)
            .ToList();

        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(item.Name);

            try
            {
                if (item.Action == CleanAction.EmptyRecycleBin)
                {
                    long before = FsUtil.RecycleBinSize("C:\\");
                    if (EmptyRecycleBin()) { freed += before; count++; }
                    continue;
                }

                // 累计这一项里"没能删掉"的内容，最后汇总成一条友好提示
                long remainBytes = 0;
                int remainCount = 0;

                foreach (var path in item.Paths)
                {
                    ct.ThrowIfCancellationRequested();

                    if (Directory.Exists(path))
                        CleanDirContents(path, toRecycleBin, ct,
                            ref freed, ref count, ref remainBytes, ref remainCount);
                    else if (File.Exists(path))
                        CleanSingleFile(path, toRecycleBin,
                            ref freed, ref count, ref remainBytes, ref remainCount);
                }

                if (remainCount > 0)
                    errors.Add($"{item.Name}：{remainCount} 项正被占用，未能删除" +
                               $"（剩余 {CleanupCategory.FormatBytes(remainBytes)}），关掉相关程序后可再清理");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{item.Name}：{ex.Message}"); }
        }

        return new CleanResult(freed, count, errors);
    }

    /// <summary>
    /// 删除单个路径（空间分析页里用户选中某个方块后删除它）。
    /// 与一键清理不同，这里删除的是路径本身（整个文件夹或文件）。
    /// </summary>
    public static (long freed, string? error) DeletePath(string path, bool recycle)
    {
        try
        {
            long size;
            if (Directory.Exists(path)) size = FsUtil.DirSize(path, CancellationToken.None);
            else if (File.Exists(path)) size = SafeLen(path);
            else return (0, "路径不存在");

            ShellDelete(new[] { path }, recycle);

            // 删完后还在 → 多半是被占用
            if (Directory.Exists(path) || File.Exists(path))
                return (0, "目标正被其它程序占用，未能删除");
            return (size, null);
        }
        catch (Exception ex) { return (0, ex.Message); }
    }

    /// <summary>
    /// 删除目录里的内容，但保留目录本身（如 %TEMP% 必须存在）。
    /// 用"删除前后体积之差"来统计真实释放量——被占用而跳过的部分会留在 after 里，不会算进去。
    /// </summary>
    private static void CleanDirContents(string dir, bool recycle, CancellationToken ct,
        ref long freed, ref int count, ref long remainBytes, ref int remainCount)
    {
        var children = TopLevelEntries(dir);          // 只取直接子项（文件 + 子目录）
        if (children.Count == 0) return;

        long before = FsUtil.DirSize(dir, ct);
        ShellDelete(children, recycle);               // 批量送删，被占用的自动跳过
        long after = FsUtil.DirSize(dir, ct);

        freed += Math.Max(0, before - after);
        int leftover = TopLevelEntries(dir).Count;    // 没删掉的直接子项个数
        count += Math.Max(0, children.Count - leftover);

        if (leftover > 0) { remainBytes += after; remainCount += leftover; }
    }

    /// <summary>删除单个文件（如缩略图缓存的某个 .db）。</summary>
    private static void CleanSingleFile(string file, bool recycle,
        ref long freed, ref int count, ref long remainBytes, ref int remainCount)
    {
        long size = SafeLen(file);
        ShellDelete(new[] { file }, recycle);

        if (!File.Exists(file)) { freed += size; count++; }
        else { remainBytes += size; remainCount++; }   // 还在 → 被占用
    }

    private static List<string> TopLevelEntries(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch { return new List<string>(); }
    }

    private static long SafeLen(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    // ===== 用 Shell 接口删除：静默、不弹框、被占用的自动跳过 =====
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;            // 双 \0 结尾的多路径字符串
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;          // 不显示进度框
    private const ushort FOF_NOCONFIRMATION = 0x0010;  // 不弹"确定删除吗"
    private const ushort FOF_ALLOWUNDO = 0x0040;       // 进回收站（而不是彻底删）
    private const ushort FOF_NOERRORUI = 0x0400;       // 出错也不弹框，直接跳过

    /// <summary>
    /// 把若干文件/目录一次性送删。recycle=true 进回收站，false 永久删除。
    /// 被其它程序占用的项会被自动跳过，整批不会因此中断。
    /// </summary>
    private static void ShellDelete(IEnumerable<string> paths, bool recycle)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (list.Count == 0) return;

        // SHFileOperation 要求多路径用 \0 分隔、并以额外一个 \0 收尾（双 \0 结尾）。
        // StringToHGlobalUni 会按字符串长度整段拷贝，能保留中间的 \0，并自带末尾 \0。
        string joined = string.Join("\0", list) + "\0";
        IntPtr pFrom = Marshal.StringToHGlobalUni(joined);
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = pFrom,
                fFlags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI
                                  | (recycle ? FOF_ALLOWUNDO : 0)),
            };
            SHFileOperation(ref op);
        }
        finally { Marshal.FreeHGlobal(pFrom); }
    }

    // ===== 清空回收站：系统接口，无确认/无进度框/无声音 =====
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    private static bool EmptyRecycleBin()
    {
        try
        {
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0;
        }
        catch { return false; }
    }
}
