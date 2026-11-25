using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace WinUIKit
{
    /// <summary>
    /// The single layout for dialog body text. When a <see cref="DialogWindow"/>'s Content is a string it is laid out by <see cref="Build"/>;
    /// don't new up TextBlocks with ad-hoc fonts — font size, family, line spacing and alignment would differ between dialogs; text paragraphs in
    /// custom bodies should also use <see cref="Build"/>.
    /// Use the default width limit (548, same as ContentDialog) and don't widen it; the exception is informational dialogs with wide key/value
    /// lines, which widen to <see cref="WideMaxWidth"/>. When too tall, the dialog window's body area scrolls.
    /// </summary>
    public static class DialogText
    {
        /// <summary>Prefix a line with this to show it in prominent red (see <see cref="Build"/>).</summary>
        public const string AlertPrefix = "! ";

        /// <summary>Splits at the whitespace after "·"; the separator dot stays at the end of the preceding item.</summary>
        private static readonly System.Text.RegularExpressions.Regex SegmentSplit = new(@"(?<=·)\s+");

        /// <summary>
        /// Width that informational dialogs with wide key/value lines widen to: values with several items per line wrap on almost every line
        /// within the default 548 limit. The dialog still shrinks to its content, so short content never gets this wide; on screens narrower than
        /// this the dialog window shrinks to the work area (<see cref="DialogWindow.MaxWidthDip"/>).
        /// </summary>
        public const double WideMaxWidth = 840;

        /// <summary>
        /// Lays out dialog body text as UI — [one method shared by every text dialog in the app] (information, prompts, confirmations, errors, etc.).
        /// Follows Fluent guidance (learn.microsoft.com Typography / Content layout and spacing), with the same styles as a settings page:
        /// <list type="bullet">
        /// <item>The system font throughout (Body 14/20). The guidance calls for one font across the app; a monospace font (Consolas 13) was tried
        ///   earlier, CJK text fell back to another font, and body and BodyStrong headings ended up nearly the same weight, so headings and sections didn't stand out.</item>
        /// <item>Lines starting with "# " → group heading: BodyStrong in the primary text color, 16 above (none for the first line), 4 below, no divider.
        ///   A full-width divider above each heading was tried and chopped the text into many blocks once there were several groups; small Caption headings in the
        ///   secondary color were tried too and could not be told apart from the secondary-colored name column.
        ///   Prefer one line per kind of information without grouping where possible; groups are for the few places that need them (e.g. debug information).</item>
        /// <item>"name : value" lines → two columns: name in the secondary color, value in the primary text color, colon not shown, column spacing 24, row spacing 2.
        ///   With both columns in the same color and a 76 column gap, the eye could hardly connect the two halves of a row.
        ///   The whole text is [one] grid, so values align from top to bottom; headings, dividers and paragraphs span both columns.</item>
        /// <item>A line of only "─" → divider (1px, DividerStrokeColorDefaultBrush, follows the theme). Empty line → 12px gap (guidance: 12 between content areas).</item>
        /// <item>Lines starting with "! " (<see cref="AlertPrefix"/>) → red text (SystemFillColorCriticalBrush, follows the theme) as their own paragraph; with " : " they still use two columns.</item>
        /// </list>
        /// Logs can keep the plain text with character dividers; only the UI rendering changes here.
        /// </summary>
        public static UIElement Build(string summary)
        {
            var panel = new Grid { ColumnSpacing = 24, RowSpacing = 2 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var resources = Application.Current.Resources;
            var block = new System.Text.StringBuilder();
            bool gap = false;   // An empty line came before: leave 12px above the next line

            (int Row, double Top) NewRow(double top)
            {
                if (panel.RowDefinitions.Count == 0) top = 0;
                else if (gap) top = Math.Max(top, 12);
                gap = false;
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                return (panel.RowDefinitions.Count - 1, top);
            }

            void Place(FrameworkElement element, int row, double top, int column = 0, int span = 1)
            {
                Thickness m = element.Margin;
                // Always zero left/right: the settings-page group heading style has a 1px left margin (to align with settings card borders), which here would push headings right of the name column.
                element.Margin = new Thickness(0, top + m.Top, 0, m.Bottom);
                Grid.SetRow(element, row);
                Grid.SetColumn(element, column);
                Grid.SetColumnSpan(element, span);
                panel.Children.Add(element);
            }

            // ThemeResource can only be used in XAML; build via XamlReader so the divider color follows light/dark theme switches.
            FrameworkElement Divider(int space) => (FrameworkElement)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<Rectangle xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                + $"Height='1' Margin='0,{space},0,{space}' HorizontalAlignment='Stretch' "
                + "Fill='{ThemeResource DividerStrokeColorDefaultBrush}'/>");

            // Lines starting with "! " must stand out (e.g. a warning inside debug information): red text uses the system "critical" color. The name column uses the secondary text color.
            // As with Divider, ThemeResource can only be used in XAML; building via XamlReader makes it follow light/dark theme switches.
            TextBlock ThemedText(string brush) => (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                + $"Foreground='{{ThemeResource {brush}}}'/>");
            TextBlock NewText(bool alert) => alert ? ThemedText("SystemFillColorCriticalBrush") : new TextBlock();

            TextBlock Body(string text, bool selectable = true, bool alert = false)
            {
                var tb = NewText(alert);
                tb.Text = text;
                tb.Style = (Style)resources["BodyTextBlockStyle"];
                tb.TextWrapping = TextWrapping.Wrap;
                tb.IsTextSelectionEnabled = selectable;
                return tb;
            }

            // Values with several items on one line like "a · b · c": one TextBlock per item in a flow panel, so lines only wrap between items
            // and never break in the middle of one (symptom: a short phrase split across two lines). The separator dot stays at the end of the preceding item.
            FrameworkElement Value(string text)
            {
                string[] segments = SegmentSplit.Split(text);
                if (segments.Length < 2)
                    return Body(text);
                var flow = new SegmentFlowPanel { Spacing = 4 };
                foreach (string segment in segments)
                    flow.Children.Add(Body(segment));
                return flow;
            }

            // Name in the secondary color, value in the primary text color: the value is what gets read, the name is only a label.
            TextBlock Name(string text, bool alert = false)
            {
                var tb = alert ? NewText(true) : ThemedText("TextFillColorSecondaryBrush");
                tb.Text = text;
                tb.Style = (Style)resources["BodyTextBlockStyle"];
                tb.TextWrapping = TextWrapping.NoWrap;
                return tb;
            }

            void FlushBlock()
            {
                string text = block.ToString().Trim('\n');
                block.Clear();
                if (text.Length == 0) return;
                (int row, double top) = NewRow(0);
                Place(Body(text), row, top, span: 2);
            }

            foreach (string raw in summary.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith(AlertPrefix, StringComparison.Ordinal))
                {
                    // An alert line is its own paragraph (not merged into surrounding paragraphs); a "name : value" line still uses two columns, both red.
                    FlushBlock();
                    string text = line[AlertPrefix.Length..];
                    (int row, double top) = NewRow(0);
                    int sep = text.IndexOf(" : ", StringComparison.Ordinal);
                    if (sep > 0)
                    {
                        Place(Name(text[..sep].TrimEnd(), alert: true), row, top);
                        Place(Body(text[(sep + 3)..], alert: true), row, top, column: 1);
                    }
                    else
                    {
                        Place(Body(text, alert: true), row, top, span: 2);
                    }
                    continue;
                }
                if (line.Length == 0)
                {
                    FlushBlock();
                    if (panel.RowDefinitions.Count > 0) gap = true;
                    continue;
                }
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    FlushBlock();
                    gap = false;   // Spacing between groups comes from the 16 above the heading; don't add the empty-line gap on top
                    (int row, double top) = NewRow(16);   // None for the first line (NewRow zeroes it)
                    Place(new TextBlock
                    {
                        Text = line[2..],
                        Style = (Style)resources["BodyStrongTextBlockStyle"],
                        Margin = new Thickness(0, 0, 0, 4),
                    }, row, top, span: 2);
                    continue;
                }
                if (line.All(c => c == '─'))
                {
                    FlushBlock();
                    (int row, double top) = NewRow(0);
                    Place(Divider(8), row, top, span: 2);
                    continue;
                }
                int colon = line.IndexOf(" : ", StringComparison.Ordinal);
                if (colon > 0 && !char.IsWhiteSpace(line[0]))
                {
                    FlushBlock();
                    (int row, double top) = NewRow(0);
                    Place(Name(line[..colon].TrimEnd()), row, top);
                    Place(Value(line[(colon + 3)..]), row, top, column: 1);
                    continue;
                }
                block.Append(line).Append('\n');
            }
            FlushBlock();
            return panel;
        }
    }
}
