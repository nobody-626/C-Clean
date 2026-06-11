using System.ComponentModel;
using System.Windows.Media;

namespace CClean.Models;

/// <summary>一键清理时对该项执行的动作。</summary>
public enum CleanAction
{
    /// <summary>删除 Paths 里目录的"内容"（保留目录本身）或删除指定文件。</summary>
    DeleteContents,
    /// <summary>清空回收站（用系统接口，不走文件删除）。</summary>
    EmptyRecycleBin,
    /// <summary>不参与一键删除，只能"打开目录"手动处理（如聊天记录、已装程序）。</summary>
    OpenOnly,
}

/// <summary>
/// 一条扫描结果，比如"系统临时文件""微信接收的文件""NVIDIA 着色器缓存"。
/// 实现 INotifyPropertyChanged，这样勾选/大小变化时界面能自动刷新。
/// </summary>
public class CleanupCategory : INotifyPropertyChanged
{
    private bool _isSelected;
    private long _sizeBytes;

    /// <summary>分类图标（先用 emoji 占位）。</summary>
    public string Icon { get; set; } = "📁";

    /// <summary>名称，如"系统临时文件"。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句话说明，告诉用户这是什么、删了有什么后果。</summary>
    public string Description { get; set; } = "";

    /// <summary>安全等级，决定颜色、默认是否勾选、删除时是否要确认。</summary>
    public SafetyLevel Safety { get; set; } = SafetyLevel.Safe;

    /// <summary>该项真实占用的字节数（由扫描器计算）。</summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set { _sizeBytes = value; OnPropertyChanged(nameof(SizeBytes)); OnPropertyChanged(nameof(SizeDisplay)); }
    }

    /// <summary>这条结果实际对应的磁盘路径（供以后真正删除时使用，本轮只读不删）。</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>补充信息，如软件的安装日期；为空则界面不显示。</summary>
    public string Note { get; set; } = "";

    /// <summary>一键清理时对该项执行的动作。</summary>
    public CleanAction Action { get; set; } = CleanAction.DeleteContents;

    /// <summary>该项是否能被一键清理处理（🔴 聊天/程序只能手动）。</summary>
    public bool CanAutoClean => Action != CleanAction.OpenOnly;

    /// <summary>用户是否勾选。默认只勾选"可安全清理"的项。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    // ----- 下面这些是给界面绑定用的派生属性 -----
    public string SizeDisplay => FormatBytes(SizeBytes);
    public int SafetyOrder => (int)Safety;                       // 用于排序：🟢→🟡→🔴
    public string SafetyBadge => SafetyStyle.Badge(Safety);      // 小药丸文字
    public Brush SafetyForeground => SafetyStyle.Foreground(Safety);
    public Brush SafetyBackground => SafetyStyle.Background(Safety);
    public string GroupLabel => SafetyStyle.GroupLabel(Safety);  // 分组标题
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>把字节数转成人类可读的大小字符串，如 "1.2 GB"。</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
