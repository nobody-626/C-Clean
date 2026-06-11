using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using CClean.Models;
using CClean.Services;

namespace CClean;

public partial class MainWindow : Window
{
    /// <summary>"可清理项"页的扫描结果。</summary>
    private readonly ObservableCollection<CleanupCategory> _items = new();

    private CancellationTokenSource? _scanCts;       // 清理扫描
    private CancellationTokenSource? _analysisCts;   // 空间分析
    private bool _busy;                              // 正在扫描/清理，避免重入

    private string _currentDir = @"C:\";             // 空间分析当前所在目录
    private string _analysisSummary = "";            // 鼠标移出 treemap 时恢复的概要文字

    public MainWindow()
    {
        InitializeComponent();
        SetupGroupedList();

        Treemap.Activated += OnTreemapActivated;
        Treemap.Hovered += OnTreemapHovered;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadDriveInfo();
        await LoadTreemap(@"C:\");   // 默认进入"空间分析"页
    }

    private void SetupGroupedList()
    {
        var view = new CollectionViewSource { Source = _items };
        view.SortDescriptions.Add(new SortDescription(nameof(CleanupCategory.SafetyOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(CleanupCategory.SizeBytes), ListSortDirection.Descending));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupCategory.GroupLabel)));
        CategoryList.ItemsSource = view.View;
    }

    // ===================== 侧边栏切换 =====================
    private void NavAnalysis_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AnalysisPanel.Visibility = Visibility.Visible;
        CleanupPanel.Visibility = Visibility.Collapsed;
    }

    private async void NavCleanup_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        CleanupPanel.Visibility = Visibility.Visible;
        AnalysisPanel.Visibility = Visibility.Collapsed;
        if (_items.Count == 0 && !_busy) await RunCleanupScanAsync(); // 首次进入才扫
    }

    /// <summary>顶部"重新扫描"：刷新当前所在的页面。</summary>
    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (AnalysisPanel.Visibility == Visibility.Visible)
        {
            SpaceAnalyzer.ClearCache();
            await LoadTreemap(_currentDir);
        }
        else
        {
            await RunCleanupScanAsync();
        }
    }

    // ===================== 空间分析（treemap） =====================
    private async Task LoadTreemap(string dir)
    {
        _analysisCts?.Cancel();
        _analysisCts = new CancellationTokenSource();
        var ct = _analysisCts.Token;

        _currentDir = dir;
        BreadcrumbText.Text = dir;
        BackButton.IsEnabled = ParentOf(dir) != null;
        AnalysisProgress.Visibility = Visibility.Visible;
        HoverText.Text = "正在分析…";

        var progress = new Progress<string>(name => { if (!ct.IsCancellationRequested) HoverText.Text = $"正在统计：{name} …"; });

        try
        {
            var nodes = await Task.Run(() => SpaceAnalyzer.GetChildren(dir, ct, progress), ct);
            if (ct.IsCancellationRequested) return;

            Treemap.SetNodes(nodes);
            long total = 0;
            foreach (var n in nodes) total += n.SizeBytes;
            _analysisSummary = $"{nodes.Count} 项 · 合计 {CleanupCategory.FormatBytes(total)}（单击文件夹下钻 / 单击文件定位）";
            HoverText.Text = _analysisSummary;
        }
        catch (OperationCanceledException) { /* 被新的分析取代，忽略 */ }
        catch (Exception ex) { HoverText.Text = "分析出错：" + ex.Message; }
        finally
        {
            if (!ct.IsCancellationRequested) AnalysisProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnTreemapActivated(DirNode node)
    {
        if (node.IsDirectory) await LoadTreemap(node.FullPath);
        else OpenPath(node.FullPath); // 文件：在资源管理器中定位，便于手动删除
    }

    private void OnTreemapHovered(DirNode? node)
    {
        HoverText.Text = node != null
            ? $"{node.Name} — {node.SizeDisplay}{(node.IsDirectory ? "（单击进入）" : "")}"
            : _analysisSummary;
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var parent = ParentOf(_currentDir);
        if (parent != null) await LoadTreemap(parent);
    }

    private static string? ParentOf(string dir)
    {
        try { return Directory.GetParent(dir.TrimEnd('\\'))?.FullName; }
        catch { return null; }
    }

    // ===================== 可清理项扫描 =====================
    private async Task RunCleanupScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _busy = true;
        _items.Clear();
        UpdateSelectedSize();
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;

        var scanners = new IScanner[]
        {
            new SystemCacheScanner(), new NvidiaScanner(), new GameRecordingScanner(),
            new TencentScanner(), new InstalledSoftwareScanner(),
        };

        try
        {
            foreach (var scanner in scanners)
            {
                ct.ThrowIfCancellationRequested();
                StatusText.Text = $"正在扫描：{scanner.DisplayName} …";
                var found = await Task.Run(() => scanner.Scan(ct).ToList(), ct);
                foreach (var item in found)
                {
                    item.PropertyChanged += Item_PropertyChanged;
                    _items.Add(item);
                }
                UpdateSelectedSize();
            }

            long grand = 0;
            foreach (var i in _items) grand += i.SizeBytes;
            StatusText.Text = _items.Count == 0
                ? "扫描完成：未发现可清理项"
                : $"扫描完成：共 {_items.Count} 项，合计 {CleanupCategory.FormatBytes(grand)}";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消扫描"; }
        catch (Exception ex) { StatusText.Text = "扫描出错：" + ex.Message; }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = true;
            _busy = false;
            UpdateSelectedSize();
            LoadDriveInfo();
        }
    }

    // ===================== 真实清理（删除） =====================
    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var selected = _items.Where(c => c.IsSelected).ToList();
        var deletable = selected.Where(c => c.CanAutoClean).ToList();
        var skipped = selected.Where(c => !c.CanAutoClean).ToList();

        if (deletable.Count == 0)
        {
            MessageBox.Show(this,
                "所选项目无法一键清理（🔴 聊天记录、已装程序请用每项的“打开目录”手动处理）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool recycle = RecycleCheck.IsChecked == true;
        long total = deletable.Sum(c => c.SizeBytes);
        string list = string.Join("\n", deletable.Select(c => $"  • {c.Name}  ({c.SizeDisplay})"));
        string skipNote = skipped.Count > 0
            ? $"\n\n（{skipped.Count} 个 🔴 项将被跳过，请手动处理）"
            : "";
        string mode = recycle ? "删除到回收站（之后可从回收站恢复）" : "永久删除（不可恢复！）";

        var confirm = MessageBox.Show(this,
            $"即将{mode}：\n\n{list}\n\n预计释放约 {CleanupCategory.FormatBytes(total)}。{skipNote}\n\n确定继续吗？",
            "确认清理", MessageBoxButton.OKCancel,
            recycle ? MessageBoxImage.Question : MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        _busy = true;
        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        var cts = new CancellationTokenSource();
        var progress = new Progress<string>(name => StatusText.Text = $"正在清理：{name} …");

        try
        {
            var result = await Task.Run(() => Cleaner.Clean(deletable, recycle, cts.Token, progress), cts.Token);

            StatusText.Text = $"清理完成：释放 {CleanupCategory.FormatBytes(result.FreedBytes)}，" +
                              $"处理 {result.DeletedCount} 个对象" +
                              (result.Errors.Count > 0 ? $"，{result.Errors.Count} 个未能删除" : "");

            if (result.Errors.Count > 0)
            {
                string head = string.Join("\n", result.Errors.Take(8));
                MessageBox.Show(this,
                    $"有 {result.Errors.Count} 个对象未能删除（通常是正在被占用）：\n\n{head}" +
                    (result.Errors.Count > 8 ? "\n…" : ""),
                    "部分未删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { StatusText.Text = "清理出错：" + ex.Message; }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            _busy = false;
            SpaceAnalyzer.ClearCache(); // 删了东西，分析缓存作废
            await RunCleanupScanAsync(); // 重新扫描，刷新剩余大小
        }
    }

    // ===================== 打开目录（手动删除） =====================
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CleanupCategory item)
            OpenInExplorer(item);
    }

    private void CategoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CategoryList.SelectedItem is CleanupCategory item)
            OpenInExplorer(item);
    }

    private void OpenInExplorer(CleanupCategory item)
    {
        string? target = item.Paths.FirstOrDefault(p => Directory.Exists(p) || File.Exists(p));
        if (target == null)
        {
            MessageBox.Show(this,
                $"“{item.Name}”没有可定位的具体目录（例如回收站，请直接在桌面打开回收站清理）。",
                "无法定位目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenPath(target);
    }

    /// <summary>在资源管理器中打开目录；若是文件则定位并选中它。</summary>
    private void OpenPath(string target)
    {
        try
        {
            var psi = Directory.Exists(target)
                ? new ProcessStartInfo("explorer.exe", $"\"{target}\"")
                : new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"");
            psi.UseShellExecute = true;
            Process.Start(psi);
            StatusText.Text = $"已打开：{target}";
        }
        catch (Exception ex) { MessageBox.Show(this, "打开目录失败：" + ex.Message, "错误"); }
    }

    // ===================== 磁盘容量仪表盘 =====================
    private void LoadDriveInfo()
    {
        try
        {
            var drive = new DriveInfo("C");
            long total = drive.TotalSize, free = drive.TotalFreeSpace, used = total - free;
            double usedPercent = total > 0 ? used * 100.0 / total : 0;

            DriveUsageText.Text =
                $"已用 {CleanupCategory.FormatBytes(used)} / 共 {CleanupCategory.FormatBytes(total)}" +
                $"，剩余 {CleanupCategory.FormatBytes(free)}";
            UpdateGauge(usedPercent);
        }
        catch (Exception ex) { DriveUsageText.Text = "无法读取磁盘信息：" + ex.Message; }
    }

    private void UpdateGauge(double percent)
    {
        GaugePercent.Text = $"{percent:0}%";
        double cx = 75, cy = 75, r = 66;
        double angle = Math.Min(Math.Max(percent, 0), 100) / 100.0 * 360.0;

        if (angle <= 0) { GaugeArc.Data = null; return; }
        if (angle >= 360) angle = 359.99;

        Point start = PointOnCircle(cx, cy, r, -90);
        Point end = PointOnCircle(cx, cy, r, -90 + angle);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        GaugeArc.Data = geo;
    }

    private static Point PointOnCircle(double cx, double cy, double r, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCategory.IsSelected)) UpdateSelectedSize();
    }

    private void UpdateSelectedSize()
    {
        long total = 0;
        foreach (var c in _items) if (c.IsSelected) total += c.SizeBytes;
        SelectedSizeText.Text = CleanupCategory.FormatBytes(total);
        CleanButton.IsEnabled = total > 0 && !_busy;
    }

    // ===================== 深色标题栏 =====================
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int useDark = 1;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        }
        catch { }
    }
}
