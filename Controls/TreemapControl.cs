using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CClean.Models;

namespace CClean.Controls;

/// <summary>
/// 一个 treemap 控件：把一组文件夹/文件画成大小不一的彩色方块（面积 = 占用空间）。
/// 用 squarified 算法布局，让方块尽量接近正方形、好看好点。
/// 鼠标悬停高亮并通过 Hovered 事件汇报；单击文件夹通过 Activated 事件通知外部下钻。
/// 直接重写 OnRender 绘制，而不是创建上千个控件，性能好。
/// </summary>
public class TreemapControl : FrameworkElement
{
    private List<DirNode> _nodes = new();
    private readonly List<(DirNode node, Rect rect)> _layout = new();
    private DirNode? _hover;
    private DirNode? _selected;
    private double _total;

    /// <summary>双击了某个节点（外部用它来下钻进文件夹 / 定位文件）。</summary>
    public event Action<DirNode>? Activated;
    /// <summary>单击选中了某个节点（外部用它显示清理建议）。</summary>
    public event Action<DirNode>? Selected;
    /// <summary>悬停的节点变化。null 表示移出。</summary>
    public event Action<DirNode?>? Hovered;

    private static readonly Brush FileBrush = MakeFrozen(Color.FromRgb(0x55, 0x5E, 0x70));
    private static readonly Brush BorderPen = MakeFrozen(Color.FromRgb(0x14, 0x16, 0x1F));
    private static readonly Brush HoverPen = MakeFrozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectPen = MakeFrozen(Color.FromRgb(0x2E, 0xE6, 0xC0));

    private static Brush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // 文件夹按顺序取色，区分度高
    private static readonly Color[] DirColors =
    {
        Color.FromRgb(0x2E, 0xB8, 0xA6),
        Color.FromRgb(0x4C, 0x8B, 0xF0),
        Color.FromRgb(0x9B, 0x6C, 0xE8),
        Color.FromRgb(0xE8, 0x7A, 0xB0),
        Color.FromRgb(0xE6, 0x9A, 0x4C),
        Color.FromRgb(0x3F, 0xC4, 0x6E),
        Color.FromRgb(0x59, 0x9B, 0xD6),
        Color.FromRgb(0xD6, 0x6B, 0x6B),
    };

    public TreemapControl()
    {
        ClipToBounds = true;
    }

    public void SetNodes(List<DirNode> nodes)
    {
        _nodes = nodes ?? new();
        _hover = null;
        _selected = null;
        _total = _nodes.Sum(n => (double)n.SizeBytes);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // 背景（同时让整个区域可命中鼠标）
        dc.DrawRectangle(MakeFrozen(Color.FromRgb(0x1A, 0x1D, 0x29)), null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        _layout.Clear();
        if (_nodes.Count == 0 || ActualWidth < 4 || ActualHeight < 4)
        {
            DrawCenteredHint(dc, _nodes.Count == 0 ? "（空）" : "");
            return;
        }

        Squarify(_nodes, new Rect(0, 0, ActualWidth, ActualHeight), _layout);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int colorIdx = 0;
        var border = new Pen(BorderPen, 1);
        border.Freeze();

        foreach (var (node, rect) in _layout)
        {
            if (rect.Width < 1 || rect.Height < 1) continue;

            Brush fill;
            if (node.IsDirectory)
            {
                fill = MakeFrozen(DirColors[colorIdx % DirColors.Length]);
                colorIdx++;
            }
            else fill = FileBrush;

            dc.DrawRectangle(fill, border, rect);

            // 选中项青色粗边；否则悬停项白色边
            if (ReferenceEquals(node, _selected))
            {
                var sp = new Pen(SelectPen, 2.5);
                sp.Freeze();
                dc.DrawRectangle(null, sp, rect);
            }
            else if (ReferenceEquals(node, _hover))
            {
                var hp = new Pen(HoverPen, 1.5);
                hp.Freeze();
                dc.DrawRectangle(null, hp, rect);
            }

            // 方块够大才写字
            if (rect.Width > 46 && rect.Height > 26)
                DrawLabel(dc, node, rect, dpi);
        }
    }

    private void DrawLabel(DrawingContext dc, DirNode node, Rect rect, double dpi)
    {
        string pct = _total > 0 ? $" · {node.SizeBytes / _total * 100:0.#}%" : "";
        string text = $"{node.Name}\n{node.SizeDisplay}{pct}";
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 12, Brushes.White, dpi)
        {
            MaxTextWidth = Math.Max(0, rect.Width - 10),
            MaxTextHeight = Math.Max(0, rect.Height - 8),
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(rect.X + 6, rect.Y + 5));
    }

    private void DrawCenteredHint(DrawingContext dc, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 14, MakeFrozen(Color.FromRgb(0x6B, 0x74, 0x80)), dpi);
        dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
    }

    // ===================== 鼠标交互 =====================
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            Hovered?.Invoke(hit);
            InvalidateVisual();
            Cursor = hit is { IsDirectory: true } ? Cursors.Hand : Cursors.Arrow;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null) { _hover = null; Hovered?.Invoke(null); InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit == null) return;

        if (e.ClickCount >= 2)
        {
            Activated?.Invoke(hit); // 双击：下钻 / 定位
        }
        else
        {
            _selected = hit;        // 单击：选中并给建议
            InvalidateVisual();
            Selected?.Invoke(hit);
        }
    }

    private DirNode? HitTest(Point p)
    {
        foreach (var (node, rect) in _layout)
            if (rect.Contains(p)) return node;
        return null;
    }

    // ===================== squarified 布局算法 =====================
    private static void Squarify(List<DirNode> nodes, Rect bounds, List<(DirNode, Rect)> output)
    {
        var items = nodes.Where(n => n.SizeBytes > 0).ToList();
        double total = items.Sum(n => (double)n.SizeBytes);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        double areaScale = bounds.Width * bounds.Height / total;
        var queue = new Queue<(DirNode node, double area)>(items.Select(n => (n, n.SizeBytes * areaScale)));

        Rect free = bounds;
        var row = new List<(DirNode node, double area)>();
        double side = Math.Min(free.Width, free.Height);

        while (queue.Count > 0)
        {
            var next = queue.Peek();
            var current = row.Select(r => r.area).ToList();
            var withNext = new List<double>(current) { next.area };

            if (row.Count == 0 || Worst(current, side) >= Worst(withNext, side))
            {
                row.Add(queue.Dequeue());
            }
            else
            {
                free = LayoutRow(row, free, output);
                row.Clear();
                side = Math.Min(free.Width, free.Height);
            }
        }
        if (row.Count > 0) LayoutRow(row, free, output);
    }

    /// <summary>一行里最差（最不接近正方形）的长宽比，越小越好。</summary>
    private static double Worst(List<double> areas, double side)
    {
        if (areas.Count == 0 || side <= 0) return double.MaxValue;
        double sum = areas.Sum();
        double max = areas.Max();
        double min = areas.Min();
        if (sum <= 0 || min <= 0) return double.MaxValue;
        double s2 = side * side;
        double sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    /// <summary>把一行方块铺到可用区域的短边上，返回去掉这条带子后剩下的区域。</summary>
    private static Rect LayoutRow(List<(DirNode node, double area)> row, Rect free, List<(DirNode, Rect)> output)
    {
        double rowArea = row.Sum(r => r.area);

        if (free.Width <= free.Height)
        {
            // 短边是宽：横向铺一条高度为 band 的带子
            double band = rowArea / free.Width;
            double x = free.X;
            foreach (var (node, area) in row)
            {
                double w = band > 0 ? area / band : 0;
                output.Add((node, new Rect(x, free.Y, w, band)));
                x += w;
            }
            return new Rect(free.X, free.Y + band, free.Width, Math.Max(0, free.Height - band));
        }
        else
        {
            // 短边是高：纵向铺一条宽度为 band 的带子
            double band = rowArea / free.Height;
            double y = free.Y;
            foreach (var (node, area) in row)
            {
                double h = band > 0 ? area / band : 0;
                output.Add((node, new Rect(free.X, y, band, h)));
                y += h;
            }
            return new Rect(free.X + band, free.Y, Math.Max(0, free.Width - band), free.Height);
        }
    }
}
