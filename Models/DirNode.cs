namespace CClean.Models;

/// <summary>空间分析里的一个节点：一个文件夹或一个文件，带它的真实大小。</summary>
public class DirNode
{
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsDirectory { get; set; }

    public string SizeDisplay => CleanupCategory.FormatBytes(SizeBytes);
}
