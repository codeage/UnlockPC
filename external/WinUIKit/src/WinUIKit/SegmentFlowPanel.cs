using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinUIKit
{
    /// <summary>
    /// Panel that lays out children left to right and wraps to a new line when they don't fit (none of WinUI's built-in panels does flow layout).
    /// Used in dialog bodies for values with several items on one line, such as "a · b · c · d": one TextBlock per item, so when the width runs out
    /// lines only wrap between items and never in the middle of one. An item wider than the whole line wraps by itself.
    /// </summary>
    internal sealed partial class SegmentFlowPanel : Panel
    {
        /// <summary>Spacing between adjacent items on the same line.</summary>
        public double Spacing { get; set; }

        private readonly List<Rect> _slots = new();

        protected override Size MeasureOverride(Size availableSize)
        {
            _slots.Clear();
            double maxWidth = availableSize.Width;
            double x = 0, y = 0, lineHeight = 0, usedWidth = 0;
            foreach (UIElement child in Children)
            {
                child.Measure(new Size(maxWidth, double.PositiveInfinity));
                Size size = child.DesiredSize;
                if (x > 0 && x + Spacing + size.Width > maxWidth)
                {
                    y += lineHeight;
                    x = 0;
                    lineHeight = 0;
                }
                if (x > 0) x += Spacing;
                _slots.Add(new Rect(x, y, size.Width, size.Height));
                x += size.Width;
                lineHeight = Math.Max(lineHeight, size.Height);
                usedWidth = Math.Max(usedWidth, x);
            }
            return new Size(usedWidth, y + lineHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            for (int i = 0; i < Children.Count && i < _slots.Count; i++)
                Children[i].Arrange(_slots[i]);
            return finalSize;
        }
    }
}
