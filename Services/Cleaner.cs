using System.IO;
using System.Runtime.InteropServices;
using CClean.Models;
using Microsoft.VisualBasic.FileIO;

namespace CClean.Services;

/// <summary>一次清理的结果：释放了多少、删了几项、有哪些出错。</summary>
public record CleanResult(long FreedBytes, int DeletedCount, List<string> Errors);

/// <summary>
/// 真实删除。默认把文件送进回收站（可恢复），而不是彻底删除——这是关键的安全设计。
/// 🔴 OpenOnly 的项（聊天记录、已装程序）一律跳过，绝不自动删。
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

                foreach (var path in item.Paths)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                        freed += DeleteDirectoryContents(path, toRecycleBin, ct, errors, ref count);
                    else if (File.Exists(path))
                        freed += DeleteOneFile(path, toRecycleBin, errors, ref count);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{item.Name}: {ex.Message}"); }
        }

        return new CleanResult(freed, count, errors);
    }

    /// <summary>删除目录里的内容，但保留目录本身（如 %TEMP% 必须存在）。</summary>
    private static long DeleteDirectoryContents(string dir, bool recycle, CancellationToken ct,
        List<string> errors, ref int count)
    {
        long freed = 0;

        foreach (var file in SafeEnum(() => Directory.EnumerateFiles(dir)))
        {
            ct.ThrowIfCancellationRequested();
            freed += DeleteOneFile(file, recycle, errors, ref count);
        }

        foreach (var sub in SafeEnum(() => Directory.EnumerateDirectories(dir)))
        {
            ct.ThrowIfCancellationRequested();
            long size = FsUtil.DirSize(sub, ct);
            try
            {
                FileSystem.DeleteDirectory(sub, UIOption.OnlyErrorDialogs,
                    recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently,
                    UICancelOption.DoNothing);
                freed += size;
                count++;
            }
            catch (Exception ex) { errors.Add($"{sub}: {ex.Message}"); }
        }
        return freed;
    }

    private static long DeleteOneFile(string file, bool recycle, List<string> errors, ref int count)
    {
        long size;
        try { size = new FileInfo(file).Length; } catch { size = 0; }
        try
        {
            FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs,
                recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently,
                UICancelOption.DoNothing);
            count++;
            return size;
        }
        catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); return 0; }
    }

    private static IEnumerable<string> SafeEnum(Func<IEnumerable<string>> getter)
    {
        try { return getter().ToList(); }
        catch { return Enumerable.Empty<string>(); }
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
