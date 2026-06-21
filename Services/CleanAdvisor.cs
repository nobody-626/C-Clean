using System.IO;
using CClean.Models;

namespace CClean.Services;

/// <summary>对某个文件夹/文件给出的清理建议。</summary>
public record Advice(SafetyLevel Level, string Title, string Text, bool CanDelete);

/// <summary>
/// 清理顾问：根据路径特征,告诉用户"这是什么 + 能不能删 + 怎么处理"。
/// 用一套从具体到通用的规则匹配；匹配不到就给保守的通用建议。
/// </summary>
public static class CleanAdvisor
{
    public static Advice For(DirNode node)
    {
        string path = node.FullPath.Replace('/', '\\');
        string lower = path.ToLowerInvariant();
        string name = node.Name.ToLowerInvariant();
        string ext = node.IsDirectory ? "" : Path.GetExtension(name);

        bool Has(string s) => lower.Contains(s);

        // ---------- 系统大文件 ----------
        if (name is "pagefile.sys" or "swapfile.sys")
            return new(SafetyLevel.Risky, "虚拟内存文件",
                "由系统管理,不要手动删除。如需减小,去 系统属性 → 高级 → 性能 → 虚拟内存 调整。", false);

        if (name == "hiberfil.sys")
            return new(SafetyLevel.Caution, "休眠文件",
                "大小约等于内存。若不用休眠功能,以管理员运行 powercfg /h off 即可释放,不能直接删。", false);

        // ---------- 系统关键目录 ----------
        if (lower is @"c:\windows")
            return new(SafetyLevel.Risky, "Windows 系统目录",
                "系统核心文件,删除会导致无法开机。切勿手动清理,交给系统自带的磁盘清理。", false);

        if (Has(@"\windows\softwaredistribution\download"))
            return new(SafetyLevel.Safe, "Windows 更新缓存",
                "已安装更新留下的下载缓存,可放心清理,不影响系统。", true);

        if (Has(@"\windows\temp") || name == "temp" && Has(@"\windows"))
            return new(SafetyLevel.Safe, "系统临时文件", "可放心清理(进回收站)。", true);

        if (Has("windows.old"))
            return new(SafetyLevel.Caution, "旧系统备份",
                "系统升级后保留的旧版本,用于回退。确认不再回退后可删(建议用系统磁盘清理)。", true);

        if (Has(@"\$recycle.bin"))
            return new(SafetyLevel.Caution, "回收站",
                "已删除文件的暂存区。在桌面回收站点'清空'即可释放。", false);

        // ---------- 已安装程序 ----------
        if (lower is @"c:\program files" or @"c:\program files (x86)")
            return new(SafetyLevel.Risky, "已安装程序目录",
                "里面是各软件本体。请用 设置 → 应用 卸载,不要手动删文件夹。", false);

        // ---------- 用户临时/缓存 ----------
        if (Has(@"\appdata\local\temp") || ext is ".tmp" or ".temp")
            return new(SafetyLevel.Safe, "临时文件", "程序运行残留,可放心清理,会自动重建。", true);

        if (Has("cache") || Has("gpucache") || Has(@"\code cache") || Has("\\caches"))
            return new(SafetyLevel.Safe, "应用缓存",
                "软件的缓存目录,删除后会按需自动重建,可清理。", true);

        if (Has("node_modules"))
            return new(SafetyLevel.Caution, "开发依赖目录",
                "前端项目的依赖,删除后在项目里重新安装(npm install)即可恢复。", true);

        // ---------- 聊天软件 ----------
        if (Has(@"\wechat files") || Has("xwechat_files") || Has(@"\tencent files"))
            return new(SafetyLevel.Caution, "聊天软件数据",
                "接收的文件/图片/视频可删;聊天记录数据库请谨慎,删了无法恢复。建议进目录手动挑。", true);

        // ---------- 录屏/媒体 ----------
        if (Has(@"\captures") || (ext is ".mp4" or ".mkv" or ".mov" or ".avi"))
            return new(SafetyLevel.Caution, "视频/录屏文件",
                "占地大且常被忽略。确认不需要后可删,删除后永久消失。", true);

        // ---------- 用户文件夹 ----------
        if (Has(@"\downloads"))
            return new(SafetyLevel.Caution, "下载文件夹",
                "你下载的文件,自行判断哪些不再需要。", true);

        if (lower.EndsWith(@"\programdata") || name == "programdata")
            return new(SafetyLevel.Caution, "程序共享数据",
                "多个程序的共享数据,部分是缓存可清,但需逐个查看,别整体删。", true);

        // ---------- 日志 ----------
        if (ext is ".log" or ".dmp" or ".etl")
            return new(SafetyLevel.Safe, "日志/转储文件", "诊断用的日志,一般可安全删除。", true);

        // ---------- 通用兜底 ----------
        return node.IsDirectory
            ? new(SafetyLevel.Caution, "普通文件夹",
                "双击进入查看内部占用,或'打开目录'在资源管理器里确认内容后再删。", true)
            : new(SafetyLevel.Caution, "文件",
                "可在资源管理器中定位查看,确认无用再删除。", true);
    }
}
