using System.Windows.Media;

namespace CClean.Models;

/// <summary>
/// 清理项的安全等级。这是整个工具的核心：不管能不能删，都让用户看到，并明确告知后果。
/// </summary>
public enum SafetyLevel
{
    /// <summary>🟢 缓存/临时/日志，删了会自动重建，不丢数据。默认勾选。</summary>
    Safe = 0,
    /// <summary>🟡 你自己的文件（录屏、接收的文件等），删了不影响软件、但永久消失。默认不勾。</summary>
    Caution = 1,
    /// <summary>🔴 聊天记录、已装程序等，删除有风险。可勾选，但删除时要二次输入确认。</summary>
    Risky = 2,
}

/// <summary>
/// 把安全等级翻译成界面要用的文字和颜色。
/// 这里的画刷都 Freeze() 了——因为扫描在后台线程创建对象，冻结后才能安全地给 UI 线程用。
/// </summary>
public static class SafetyStyle
{
    private static Brush Frozen(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // 前景色（文字/描边）
    private static readonly Brush SafeFg = Frozen(0x2E, 0xE6, 0xC0); // 青绿
    private static readonly Brush CautionFg = Frozen(0xF2, 0xB8, 0x4B); // 琥珀
    private static readonly Brush RiskyFg = Frozen(0xFF, 0x7B, 0x7B); // 红

    // 背景色（半透明小药丸底色）
    private static readonly Brush SafeBg = Frozen(0x2E, 0xE6, 0xC0, 0x26);
    private static readonly Brush CautionBg = Frozen(0xF2, 0xB8, 0x4B, 0x26);
    private static readonly Brush RiskyBg = Frozen(0xFF, 0x7B, 0x7B, 0x26);

    public static Brush Foreground(SafetyLevel l) => l switch
    {
        SafetyLevel.Safe => SafeFg,
        SafetyLevel.Caution => CautionFg,
        _ => RiskyFg,
    };

    public static Brush Background(SafetyLevel l) => l switch
    {
        SafetyLevel.Safe => SafeBg,
        SafetyLevel.Caution => CautionBg,
        _ => RiskyBg,
    };

    /// <summary>卡片上小药丸里的短标签。</summary>
    public static string Badge(SafetyLevel l) => l switch
    {
        SafetyLevel.Safe => "可安全清理",
        SafetyLevel.Caution => "删除后会丢文件",
        _ => "谨慎 · 删除需确认",
    };

    /// <summary>分组标题（列表按安全等级分组显示）。</summary>
    public static string GroupLabel(SafetyLevel l) => l switch
    {
        SafetyLevel.Safe => "🟢  可安全清理 · 缓存/临时，删除后自动重建",
        SafetyLevel.Caution => "🟡  可清理 · 你的文件，删除后将永久消失",
        _ => "🔴  谨慎 · 聊天数据 / 程序，删除需二次确认",
    };
}
