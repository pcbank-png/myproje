using System.Windows;
using System.Windows.Controls;

namespace NSXVeriKurtarmaPro.Infrastructure;

public sealed class ResponsiveDevicePanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(ResponsiveDevicePanel),
        new FrameworkPropertyMetadata(18d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing),
        typeof(double),
        typeof(ResponsiveDevicePanel),
        new FrameworkPropertyMetadata(22d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = InternalChildren.Count;
        if (count == 0)
            return new Size(0, 0);

        double availableWidth = ResolveWidth(availableSize.Width);
        double halfWidth = Math.Max(0, (availableWidth - HorizontalSpacing) / 2d);
        double desiredHeight = 0;

        for (int rowStart = 0; rowStart < count; rowStart += 2)
        {
            bool singleLastItem = rowStart == count - 1;
            double rowHeight = 0;

            UIElement first = InternalChildren[rowStart];
            first.Measure(new Size(singleLastItem ? availableWidth : halfWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, first.DesiredSize.Height);

            if (!singleLastItem)
            {
                UIElement second = InternalChildren[rowStart + 1];
                second.Measure(new Size(halfWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, second.DesiredSize.Height);
            }

            if (rowStart > 0)
                desiredHeight += VerticalSpacing;

            desiredHeight += rowHeight;
        }

        return new Size(availableWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = InternalChildren.Count;
        if (count == 0)
            return finalSize;

        double width = Math.Max(0, finalSize.Width);
        double halfWidth = Math.Max(0, (width - HorizontalSpacing) / 2d);
        double y = 0;

        for (int rowStart = 0; rowStart < count; rowStart += 2)
        {
            bool singleLastItem = rowStart == count - 1;
            UIElement first = InternalChildren[rowStart];
            double rowHeight = first.DesiredSize.Height;

            if (!singleLastItem)
                rowHeight = Math.Max(rowHeight, InternalChildren[rowStart + 1].DesiredSize.Height);

            if (singleLastItem)
            {
                first.Arrange(new Rect(0, y, width, rowHeight));
            }
            else
            {
                first.Arrange(new Rect(0, y, halfWidth, rowHeight));
                InternalChildren[rowStart + 1].Arrange(new Rect(halfWidth + HorizontalSpacing, y, halfWidth, rowHeight));
            }

            y += rowHeight + VerticalSpacing;
        }

        return finalSize;
    }

    private double ResolveWidth(double measuredWidth)
    {
        if (!double.IsInfinity(measuredWidth) && !double.IsNaN(measuredWidth) && measuredWidth > 0)
            return measuredWidth;

        if (!double.IsNaN(ActualWidth) && ActualWidth > 0)
            return ActualWidth;

        return 1;
    }
}
