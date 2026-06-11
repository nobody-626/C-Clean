using System.IO;
using CClean.Models;
using Microsoft.Win32;

namespace CClean.Services;

/// <summary>所有扫描器的统一接口。每个扫描器负责一类来源，返回若干条结果。</summary>
public interface IScanner
{
    string DisplayName { get; }
    IEnumerable<CleanupCategory> Scan(CancellationToken ct);
}

/// <summary>建结果对象的小助手，少写点重复代码。</summary>
internal static class Item
{
    public static CleanupCategory Make(string icon, string name, string desc,
        SafetyLevel safety, long size, IEnumerable<string>? paths = null, string note = "",
        CleanAction action = CleanAction.DeleteContents)
        => new()
        {
            Icon = icon,
            Name = name,
            Description = desc,
            Safety = safety,
            SizeBytes = size,
            IsSelected = safety == SafetyLevel.Safe, // 只有"可安全清理"默认勾选
            Paths = paths?.ToList() ?? new(),
            Note = note,
            Action = action,
        };
}

// 常用系统目录的快捷方式
internal static class Dirs
{
    public static string Temp => Path.GetTempPath();
    public static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
}

// ============================================================
// 🟢 系统缓存 / 临时文件 / 回收站
// ============================================================
public sealed class SystemCacheScanner : IScanner
{
    public string DisplayName => "系统缓存与临时文件";

    public IEnumerable<CleanupCategory> Scan(CancellationToken ct)
    {
        var results = new List<CleanupCategory>();

        // 用户临时 + Windows\Temp
        var tempPaths = new[] { Dirs.Temp, Path.Combine(Dirs.Windows, "Temp") };
        long temp = FsUtil.DirSize(tempPaths, ct);
        if (temp > 0)
            results.Add(Item.Make("🗂️", "系统与用户临时文件",
                "%TEMP% 与 Windows\\Temp，程序运行残留的临时文件，可放心清理",
                SafetyLevel.Safe, temp, tempPaths));

        // Windows 更新下载缓存
        var update = Path.Combine(Dirs.Windows, "SoftwareDistribution", "Download");
        long upd = FsUtil.DirSize(update, ct);
        if (upd > 0)
            results.Add(Item.Make("🔄", "Windows 更新缓存",
                "已安装更新留下的下载缓存，清理后不影响系统",
                SafetyLevel.Safe, upd, new[] { update }));

        // 缩略图缓存（精确到具体的 .db 文件，删除时只删这些文件）
        var explorer = Path.Combine(Dirs.LocalAppData, "Microsoft", "Windows", "Explorer");
        long thumbs = 0;
        var thumbFiles = new List<string>();
        if (Directory.Exists(explorer))
        {
            foreach (var pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
                foreach (var f in SafeFiles(explorer, pattern))
                    try { thumbs += new FileInfo(f).Length; thumbFiles.Add(f); } catch { }
        }
        if (thumbs > 0)
            results.Add(Item.Make("🖼️", "缩略图与图标缓存",
                "资源管理器缩略图缓存，会按需自动重建",
                SafetyLevel.Safe, thumbs, thumbFiles));

        // 回收站（清空用系统接口，不走文件删除）
        long bin = FsUtil.RecycleBinSize("C:\\");
        if (bin > 0)
            results.Add(Item.Make("🗑️", "回收站 (C:)",
                "回收站中已删除的文件，清空后无法恢复",
                SafetyLevel.Caution, bin, action: CleanAction.EmptyRecycleBin));

        return results;
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Enumerable.Empty<string>(); }
    }
}

// ============================================================
// 🟢 NVIDIA 着色器缓存 / 日志 / 安装包缓存
// ============================================================
public sealed class NvidiaScanner : IScanner
{
    public string DisplayName => "NVIDIA 缓存与日志";

    public IEnumerable<CleanupCategory> Scan(CancellationToken ct)
    {
        var results = new List<CleanupCategory>();

        // 着色器缓存（DX / GL / OptiX）
        var cachePaths = new[]
        {
            Path.Combine(Dirs.LocalAppData, "NVIDIA", "DXCache"),
            Path.Combine(Dirs.LocalAppData, "NVIDIA", "GLCache"),
            Path.Combine(Dirs.LocalAppData, "NVIDIA", "OptixCache"),
        };
        long cache = FsUtil.DirSize(cachePaths, ct);
        if (cache > 0)
            results.Add(Item.Make("🎮", "NVIDIA 着色器缓存",
                "显卡着色器编译缓存（DXCache/GLCache），删除后游戏首次运行会重建",
                SafetyLevel.Safe, cache, cachePaths));

        // 安装包缓存 + 日志（你提到的"N 卡日志过大"主要在这）
        var logPaths = new[]
        {
            Path.Combine(Dirs.ProgramData, "NVIDIA Corporation", "Downloader"),
            Path.Combine(Dirs.ProgramData, "NVIDIA Corporation", "NvTelemetry"),
            Path.Combine(Dirs.LocalAppData, "NVIDIA Corporation", "NvNode"),
        };
        long logs = FsUtil.DirSize(logPaths, ct);
        if (logs > 0)
            results.Add(Item.Make("📋", "NVIDIA 日志与安装包缓存",
                "驱动下载器残留、遥测与日志文件，可安全清理",
                SafetyLevel.Safe, logs, logPaths));

        return results;
    }
}

// ============================================================
// 🟡 游戏录屏 / 截图
// ============================================================
public sealed class GameRecordingScanner : IScanner
{
    public string DisplayName => "游戏录屏与截图";

    public IEnumerable<CleanupCategory> Scan(CancellationToken ct)
    {
        var results = new List<CleanupCategory>();

        // Xbox Game Bar / ShadowPlay 默认都录到 视频\Captures
        var captures = Path.Combine(Dirs.Videos, "Captures");
        long size = FsUtil.DirSize(captures, ct);
        if (size > 0)
            results.Add(Item.Make("🎬", "游戏录屏 (Captures)",
                "Xbox Game Bar / NVIDIA ShadowPlay 默认录制目录，删除后录像永久消失",
                SafetyLevel.Caution, size, new[] { captures }));

        return results;
    }
}

// ============================================================
// 🟡/🔴 微信 与 QQ 文件
// ============================================================
public sealed class TencentScanner : IScanner
{
    public string DisplayName => "微信 / QQ 文件";

    public IEnumerable<CleanupCategory> Scan(CancellationToken ct)
    {
        var results = new List<CleanupCategory>();
        results.AddRange(ScanWeChat(ct));
        results.AddRange(ScanQQ(ct));
        return results;
    }

    private IEnumerable<CleanupCategory> ScanWeChat(CancellationToken ct)
    {
        // 新旧版本目录都查一下
        var roots = new[]
        {
            Path.Combine(Dirs.Documents, "WeChat Files"),
            Path.Combine(Dirs.Documents, "xwechat_files"),
        }.Where(Directory.Exists).ToList();
        if (roots.Count == 0) return Enumerable.Empty<CleanupCategory>();

        long cache = 0, media = 0, rest = 0;
        var cachePaths = new List<string>();
        var mediaPaths = new List<string>();
        var restPaths = new List<string>();

        foreach (var root in roots)
        {
            foreach (var account in SafeDirs(root))
            {
                ct.ThrowIfCancellationRequested();
                long accountTotal = FsUtil.DirSize(account, ct);

                // 精确到具体子目录，删除时只动这些目录的内容
                var cacheDir = Path.Combine(account, "FileStorage", "Cache");
                var fileDirs = new[] { "File", "Image", "Video" }
                    .Select(s => Path.Combine(account, "FileStorage", s));

                long c = FsUtil.DirSize(cacheDir, ct);
                long m = 0;
                foreach (var d in fileDirs)
                {
                    long s = FsUtil.DirSize(d, ct);
                    if (s > 0) { m += s; mediaPaths.Add(d); }
                }

                cache += c;
                if (c > 0) cachePaths.Add(cacheDir);
                media += m;
                rest += Math.Max(0, accountTotal - c - m); // 剩下的当作聊天记录等，归到🔴更安全
                restPaths.Add(account);
            }
        }

        var list = new List<CleanupCategory>();
        if (cache > 0)
            list.Add(Item.Make("🌐", "微信 缓存",
                "微信的临时缓存，可安全清理", SafetyLevel.Safe, cache, cachePaths));
        if (media > 0)
            list.Add(Item.Make("💬", "微信 接收的文件与图片视频",
                "聊天接收的文件 / 图片 / 视频，删除后将永久消失（聊天里会变成已过期）",
                SafetyLevel.Caution, media, mediaPaths));
        if (rest > 0)
            list.Add(Item.Make("💬", "微信 聊天记录及其它数据",
                "聊天数据库等，删除会丢失聊天记录。本项不参与一键清理，请用打开目录手动处理",
                SafetyLevel.Risky, rest, restPaths,
                note: "估算值（账号目录总量减去已知缓存/文件）",
                action: CleanAction.OpenOnly));
        return list;
    }

    private IEnumerable<CleanupCategory> ScanQQ(CancellationToken ct)
    {
        var root = Path.Combine(Dirs.Documents, "Tencent Files");
        if (!Directory.Exists(root)) return Enumerable.Empty<CleanupCategory>();

        long media = 0, rest = 0;
        var mediaPaths = new List<string>();
        var restPaths = new List<string>();

        foreach (var account in SafeDirs(root))
        {
            ct.ThrowIfCancellationRequested();
            long accountTotal = FsUtil.DirSize(account, ct);
            long m = 0;
            foreach (var name in new[] { "FileRecv", "Image", "Video" })
            {
                var d = Path.Combine(account, name);
                long s = FsUtil.DirSize(d, ct);
                if (s > 0) { m += s; mediaPaths.Add(d); }
            }
            media += m;
            rest += Math.Max(0, accountTotal - m);
            restPaths.Add(account);
        }

        var list = new List<CleanupCategory>();
        if (media > 0)
            list.Add(Item.Make("💬", "QQ 接收的文件与图片视频",
                "QQ 接收的文件 / 图片 / 视频，删除后将永久消失",
                SafetyLevel.Caution, media, mediaPaths));
        if (rest > 0)
            list.Add(Item.Make("💬", "QQ 聊天记录及其它数据",
                "聊天数据等，删除会丢失记录。本项不参与一键清理，请用打开目录手动处理",
                SafetyLevel.Risky, rest, restPaths,
                note: "估算值（账号目录总量减去已知接收文件）",
                action: CleanAction.OpenOnly));
        return list;
    }

    private static long SubSize(string baseDir, CancellationToken ct, params string[] parts)
        => FsUtil.DirSize(Path.Combine(new[] { baseDir }.Concat(parts).ToArray()), ct);

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Enumerable.Empty<string>(); }
    }
}

// ============================================================
// 🔴 体积较大的已安装软件（含安装日期，供判断是否长期不用）
// ============================================================
public sealed class InstalledSoftwareScanner : IScanner
{
    public string DisplayName => "已安装的大型软件";

    // 只列出体积 ≥ 这个阈值的软件，避免刷屏
    private const long MinSize = 400L * 1024 * 1024; // 400 MB
    private const int MaxItems = 15;

    public IEnumerable<CleanupCategory> Scan(CancellationToken ct)
    {
        var apps = new Dictionary<string, (long size, string date, string path)>();

        ReadUninstall(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        ReadUninstall(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        ReadUninstall(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);

        return apps
            .Where(kv => kv.Value.size >= MinSize)
            .OrderByDescending(kv => kv.Value.size)
            .Take(MaxItems)
            .Select(kv => Item.Make("📦", kv.Key,
                "已安装的程序。卸载请用 Windows 设置里的应用管理，不要直接删文件夹",
                SafetyLevel.Risky, kv.Value.size,
                paths: string.IsNullOrEmpty(kv.Value.path) ? null : new[] { kv.Value.path },
                note: string.IsNullOrEmpty(kv.Value.date) ? "" : $"安装于 {kv.Value.date}",
                action: CleanAction.OpenOnly))
            .ToList();
    }

    private static void ReadUninstall(RegistryKey root, string sub, Dictionary<string, (long, string, string)> apps)
    {
        try
        {
            using var key = root.OpenSubKey(sub);
            if (key == null) return;

            foreach (var subName in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(subName);
                if (app == null) continue;

                if (app.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (app.GetValue("SystemComponent") is int sc && sc == 1) continue; // 系统组件
                if (app.GetValue("ParentKeyName") != null) continue;                // 更新补丁
                if (app.GetValue("EstimatedSize") is not int sizeKb) continue;       // 没有大小信息就跳过

                long size = sizeKb * 1024L;
                string date = FormatDate(app.GetValue("InstallDate") as string);
                string path = (app.GetValue("InstallLocation") as string ?? "").Trim('"');

                // 同名取较大值，避免 32/64 位重复
                if (!apps.TryGetValue(name, out var existing) || size > existing.Item1)
                    apps[name] = (size, date, path);
            }
        }
        catch { /* 读注册表失败就忽略这一处 */ }
    }

    private static string FormatDate(string? raw)
    {
        if (raw is { Length: 8 } &&
            DateTime.TryParseExact(raw, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd");
        return raw ?? "";
    }
}
