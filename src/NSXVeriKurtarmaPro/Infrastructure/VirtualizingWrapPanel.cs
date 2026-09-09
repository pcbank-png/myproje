using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace NSXVeriKurtarmaPro.Infrastructure;

/// <summary>
/// Fixed-size virtualizing wrap panel used by the recovery thumbnail view.
/// It realizes only the rows that intersect the viewport, so large scans do not
/// create tens of thousands of visual containers at once.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(184d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(184d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _itemsPerRow = 1;

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

    protected override Size MeasureOverride(Size availableSize)
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int itemCount = owner?.Items.Count ?? 0;

        double width = ResolveViewportLength(availableSize.Width, ActualWidth, Math.Max(1d, ItemWidth));
        double height = ResolveViewportLength(availableSize.Height, ActualHeight, Math.Max(1d, ItemHeight * 3d));
        double itemWidth = Math.Max(1d, ItemWidth);
        double itemHeight = Math.Max(1d, ItemHeight);

        _itemsPerRow = Math.Max(1, (int)Math.Floor(width / itemWidth));
        int rowCount = itemCount == 0 ? 0 : (itemCount + _itemsPerRow - 1) / _itemsPerRow;

        UpdateScrollInfo(new Size(width, rowCount * itemHeight), new Size(width, height));

        if (itemCount == 0)
        {
            CleanupAll();
            return new Size(width, height);
        }

        int firstVisibleRow = Math.Max(0, (int)Math.Floor(VerticalOffset / itemHeight));
        int visibleRowCount = Math.Max(1, (int)Math.Ceiling(height / itemHeight) + 1);
        int firstVisibleIndex = Math.Min(itemCount - 1, firstVisibleRow * _itemsPerRow);
        int lastVisibleIndex = Math.Min(itemCount - 1, ((firstVisibleRow + visibleRowCount) * _itemsPerRow) - 1);

        CleanupOutsideRange(firstVisibleIndex, lastVisibleIndex);
        RealizeRange(firstVisibleIndex, lastVisibleIndex, itemWidth, itemHeight);

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        if (owner is null)
            return finalSize;

        double itemWidth = Math.Max(1d, ItemWidth);
        double itemHeight = Math.Max(1d, ItemHeight);
        int perRow = Math.Max(1, _itemsPerRow);
        double contentWidth = perRow * itemWidth;
        double horizontalInset = Math.Max(0d, (finalSize.Width - contentWidth) / 2d);

        for (int childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            UIElement child = InternalChildren[childIndex];
            int itemIndex = owner.ItemContainerGenerator.IndexFromContainer(child);
            if (itemIndex < 0)
                continue;

            int row = itemIndex / perRow;
            int column = itemIndex % perRow;
            double x = horizontalInset + (column * itemWidth) - HorizontalOffset;
            double y = (row * itemHeight) - VerticalOffset;

            child.Arrange(new Rect(x, y, itemWidth, itemHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0)
            return;

        double itemHeight = Math.Max(1d, ItemHeight);
        int row = index / Math.Max(1, _itemsPerRow);
        double top = row * itemHeight;
        double bottom = top + itemHeight;

        if (top < VerticalOffset)
            SetVerticalOffset(top);
        else if (bottom > VerticalOffset + ViewportHeight)
            SetVerticalOffset(bottom - ViewportHeight);
    }

    private void RealizeRange(int firstIndex, int lastIndex, double itemWidth, double itemHeight)
    {
        if (firstIndex < 0 || lastIndex < firstIndex)
            return;

        IItemContainerGenerator generator = ItemContainerGenerator;
        GeneratorPosition startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
        {
            for (int itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                bool newlyRealized;
                if (generator.GenerateNext(out newlyRealized) is not UIElement child)
                    continue;

                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(itemWidth, itemHeight));
            }
        }
    }

    private void CleanupOutsideRange(int firstIndex, int lastIndex)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;

        for (int childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            GeneratorPosition position = new(childIndex, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
                continue;

            generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void CleanupAll()
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        for (int childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            generator.Remove(new GeneratorPosition(childIndex, 0), 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        bool changed = !AreClose(_extent, extent) || !AreClose(_viewport, viewport);
        _extent = extent;
        _viewport = viewport;

        double vertical = ClampOffset(_offset.Y, ExtentHeight, ViewportHeight);
        double horizontal = ClampOffset(_offset.X, ExtentWidth, ViewportWidth);
        if (!AreClose(_offset.X, horizontal) || !AreClose(_offset.Y, vertical))
            _offset = new Point(horizontal, vertical);

        if (changed)
            ScrollOwner?.InvalidateScrollInfo();
    }

    private static double ResolveViewportLength(double measured, double actual, double fallback)
    {
        if (!double.IsInfinity(measured) && !double.IsNaN(measured) && measured > 0)
            return measured;
        if (!double.IsInfinity(actual) && !double.IsNaN(actual) && actual > 0)
            return actual;
        return fallback;
    }

    private static double ClampOffset(double value, double extent, double viewport)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0d;
        return Math.Max(0d, Math.Min(value, Math.Max(0d, extent - viewport)));
    }

    private static bool AreClose(Size left, Size right) =>
        AreClose(left.Width, right.Width) && AreClose(left.Height, right.Height);

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.5d;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - ItemHeight);
    public void LineDown() => SetVerticalOffset(VerticalOffset + ItemHeight);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 24d);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 24d);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - (ItemHeight * 2d));
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + (ItemHeight * 2d));
    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - 48d);
    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + 48d);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);

    public void SetHorizontalOffset(double offset)
    {
        double next = CanHorizontallyScroll ? ClampOffset(offset, ExtentWidth, ViewportWidth) : 0d;
        if (AreClose(_offset.X, next))
            return;

        _offset.X = next;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateArrange();
    }

    public void SetVerticalOffset(double offset)
    {
        double next = CanVerticallyScroll ? ClampOffset(offset, ExtentHeight, ViewportHeight) : 0d;
        if (AreClose(_offset.Y, next))
            return;

        _offset.Y = next;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        if (owner is null)
            return rectangle;

        DependencyObject? current = visual;
        while (current is not null && current is not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);

        if (current is ListBoxItem container)
        {
            int index = owner.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0)
                BringIndexIntoView(index);
        }

        return rectangle;
    }
}
