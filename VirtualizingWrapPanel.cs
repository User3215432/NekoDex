using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MangaLibraryApp;

/// <summary>Zeilenweise umbrechendes, virtualisierendes Raster für Cover-Karten.</summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(196d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(292d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    public VirtualizingWrapPanel()
    {
        CanVerticallyScroll = true;
    }

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer ScrollOwner { get; set; } = null!;

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        _offset = new Point();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? Math.Max(_viewport.Width, ItemWidth) : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? Math.Max(_viewport.Height, ItemHeight) : availableSize.Height;
        UpdateScrollInfo(width, height);
        Realize(width);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateScrollInfo(finalSize.Width, finalSize.Height);
        var columns = Columns(finalSize.Width);
        foreach (UIElement child in InternalChildren)
        {
            var index = Containers.IndexFromContainer(child);
            if (index < 0)
                continue;
            var row = index / columns;
            var column = index % columns;
            child.Arrange(new Rect(column * ItemWidth - _offset.X, row * ItemHeight - _offset.Y, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 48);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 48);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 48);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 48);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() => SetHorizontalOffset(0);
    public void PageRight() => SetHorizontalOffset(0);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 72);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 72);
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement element)
            return rectangle;
        var index = Containers.IndexFromContainer(element);
        if (index < 0)
            return rectangle;
        var row = index / Columns(Math.Max(_viewport.Width, ItemWidth));
        var top = row * ItemHeight;
        if (top < VerticalOffset)
            SetVerticalOffset(top);
        else if (top + ItemHeight > VerticalOffset + ViewportHeight)
            SetVerticalOffset(top + ItemHeight - ViewportHeight);
        return rectangle;
    }

    public void SetHorizontalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, Math.Max(0, ExtentWidth - ViewportWidth)));
        if (Math.Abs(offset - _offset.X) < 0.5)
            return;
        _offset.X = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateArrange();
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, Math.Max(0, ExtentHeight - ViewportHeight)));
        if (Math.Abs(offset - _offset.Y) < 0.5)
            return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    private void UpdateScrollInfo(double width, double height)
    {
        var columns = Columns(width);
        var rows = ItemCount == 0 ? 0 : (ItemCount + columns - 1) / columns;
        var extent = new Size(Math.Max(width, columns * ItemWidth), rows * ItemHeight);
        var viewport = new Size(width, height);
        var changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        if (changed)
            ScrollOwner?.InvalidateScrollInfo();
    }

    private void Realize(double width)
    {
        var count = ItemCount;
        if (count == 0 || ItemWidth <= 1 || ItemHeight <= 1)
        {
            ClearChildren();
            return;
        }

        var columns = Columns(width);
        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / ItemHeight) - 1);
        var lastRow = (int)Math.Ceiling((_offset.Y + Math.Max(_viewport.Height, ItemHeight)) / ItemHeight) + 1;
        var first = Math.Min(count - 1, firstRow * columns);
        var last = Math.Min(count - 1, (lastRow * columns) - 1);
        RecycleOutside(first, last);

        IItemContainerGenerator generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var i = first; i <= last; i++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNew);
                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(ItemWidth, ItemHeight));
                childIndex++;
            }
        }
    }

    private void RecycleOutside(int first, int last)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= first && index <= last)
                continue;
            generator.Remove(new GeneratorPosition(i, 0), 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void ClearChildren()
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        while (InternalChildren.Count > 0)
        {
            generator.Remove(new GeneratorPosition(0, 0), 1);
            RemoveInternalChildRange(0, 1);
        }
    }

    private ItemContainerGenerator Containers => (ItemContainerGenerator)ItemContainerGenerator;

    private int Columns(double width) => Math.Max(1, (int)Math.Floor(Math.Max(width, ItemWidth) / ItemWidth));

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
}
