using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Snipit;

/// <summary>
/// The viewer's TesselGUI revision 3 (Carmine) look: icon-over-label action cells and a
/// Carmine Save button, chips and the dark canvas. Same 24-unit line-and-dot icon
/// style as the mode bar. All colours come from <see cref="Palette"/>, which has a
/// black-and-white High Contrast variant.
/// </summary>
internal sealed class EditorChrome
{
    internal sealed record Palette(string Canvas, string Bar, string Surface, string Edge, string Ink, string Muted,
        string Hover, string HoverInk, string Pressed, string SelLine, string Accent, string AccentHover, string AccentPressed, string ActionInk, string Focus)
    {
        public static readonly Palette Dark = new("#17171A", "#232326", "#1F1F22", "#43434A", "#E6E3E1", "#A3A0A4",
            "#28282D", "#E6E3E1", "#2E2E34", "#960018", "#960018", "#A81226", "#7E0014", "#FFFFFF", "#E8939F");
        public static readonly Palette HighContrast = new("#000000", "#000000", "#000000", "#FFFFFF", "#FFFFFF", "#FFFFFF",
            "#FFFFFF", "#000000", "#C0C0C0", "#000000", "#FFFFFF", "#E0E0E0", "#C0C0C0", "#000000", "#FFFFFF");
    }

    /// <summary>Tag value that tells <see cref="TesselTheme"/> to leave a control's own styling alone.</summary>
    public const string Keep = "keep";

    private readonly Palette _p;
    public EditorChrome(bool highContrast) { _p = highContrast ? Palette.HighContrast : Palette.Dark; }

    public Brush CanvasBg => Br(_p.Canvas);
    public Brush Bar => Br(_p.Bar);
    public Brush Surface => Br(_p.Surface);
    public Brush Edge => Br(_p.Edge);
    public Brush Ink => Br(_p.Ink);
    public Brush Muted => Br(_p.Muted);

    private static Brush Br(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    // ---- icons ---------------------------------------------------------------------------------

    // 24x24 grid, 1.6 stroke, round caps and joins; Dots are the small Nothing-style accents (decoration only).
    private static readonly Dictionary<string, (string[] Paths, (double X, double Y)[] Dots)> Icons = new()
    {
        ["copy"] = (new[] { "M11 8.5h6.5A2.5 2.5 0 0 1 20 11v6.5a2.5 2.5 0 0 1-2.5 2.5H11a2.5 2.5 0 0 1-2.5-2.5V11A2.5 2.5 0 0 1 11 8.5z", "M15.5 8.5V6.5A2.5 2.5 0 0 0 13 4H6.5A2.5 2.5 0 0 0 4 6.5V13a2.5 2.5 0 0 0 2.5 2.5h2" }, Array.Empty<(double, double)>()),
        ["save"] = (new[] { "M5 7.5A2.5 2.5 0 0 1 7.5 5H16l3 3v9.5a2.5 2.5 0 0 1-2.5 2.5h-9A2.5 2.5 0 0 1 5 17.5z", "M8.5 5v4h6V5M8.5 20v-5h7v5" }, Array.Empty<(double, double)>()),
        ["folder"] = (new[] { "M3.5 6.5A2.5 2.5 0 0 1 6 4h3.5l2 2.5H18A2.5 2.5 0 0 1 20.5 9v7.5A2.5 2.5 0 0 1 18 19H6a2.5 2.5 0 0 1-2.5-2.5z" }, Array.Empty<(double, double)>()),
        ["external"] = (new[] { "M14 3h7v7", "M21 3L13 11", "M19 14v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2h4" }, Array.Empty<(double, double)>()),
    };

    /// <summary>A line icon whose colour follows the Foreground of the button it sits in.</summary>
    public UIElement Icon(string name, double size)
    {
        var (paths, dots) = Icons[name];
        var c = new Canvas { Width = 24, Height = 24 };
        foreach (var d in paths)
        {
            var p = new Path
            {
                Data = Geometry.Parse(d), StrokeThickness = 1.6, Fill = null,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            p.SetBinding(Shape.StrokeProperty, FollowForeground());
            c.Children.Add(p);
        }
        foreach (var (x, y) in dots)
        {
            var e = new Ellipse { Width = 1.6, Height = 1.6 };
            e.SetBinding(Shape.FillProperty, FollowForeground());
            Canvas.SetLeft(e, x - .8); Canvas.SetTop(e, y - .8);
            c.Children.Add(e);
        }
        return new Viewbox { Width = size, Height = size, Child = c, IsHitTestVisible = false };
    }

    private static Binding FollowForeground() =>
        new("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1) };

    // ---- cells ---------------------------------------------------------------------------------

    private UIElement CellContent(string icon, string label)
    {
        var grid = new Grid();
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(Icon(icon, 22));
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Tag = Keep });
        grid.Children.Add(stack);
        return grid;
    }

    private static void Name(DependencyObject d, string name, string tip)
    {
        System.Windows.Automation.AutomationProperties.SetName(d, name);
        if (d is FrameworkElement fe) fe.ToolTip = tip;
    }

    /// <summary>A plain action cell: icon over label.</summary>
    public Button ActionCell(string label, string icon, string tip)
    {
        var b = new Button { Width = 72, Height = 56, Margin = new Thickness(2), Content = CellContent(icon, label), Template = ButtonTemplate, Foreground = Ink, Tag = Keep };
        Name(b, label, tip);
        return b;
    }

    /// <summary>The one Carmine action: Save.</summary>
    public Button SaveButton(string tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        row.Children.Add(Icon("save", 18));
        row.Children.Add(new TextBlock { Text = "Save", FontSize = 12, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Tag = Keep });
        var b = new Button { Height = 56, MinWidth = 96, Margin = new Thickness(8, 2, 2, 2), Padding = new Thickness(14, 0, 14, 0), Content = row, Template = SaveTemplate, Foreground = Br(_p.ActionInk), Tag = Keep };
        Name(b, "Save", tip + " Key: Ctrl+S.");
        return b;
    }

    public ControlTemplate ChipTemplate => ButtonTemplate;

    // ---- templates (XAML text, colours filled from the palette) ---------------------------------

    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private string Fill(string xaml) => xaml
        .Replace("@BAR@", _p.Bar).Replace("@EDGE@", _p.Edge).Replace("@INK@", _p.Ink).Replace("@MUTED@", _p.Muted)
        .Replace("@HOVERINK@", _p.HoverInk).Replace("@HOVER@", _p.Hover).Replace("@PRESSED@", _p.Pressed)
        .Replace("@ACCENTHOVER@", _p.AccentHover).Replace("@ACCENTPRESSED@", _p.AccentPressed).Replace("@ACCENT@", _p.Accent)
        .Replace("@ACTIONINK@", _p.ActionInk).Replace("@FOCUS@", _p.Focus);

    private ControlTemplate Load(string xaml) => (ControlTemplate)XamlReader.Parse(Fill(xaml));

    private ControlTemplate? _button, _save;

    private ControlTemplate ButtonTemplate => _button ??= Load($@"
<ControlTemplate {Ns} TargetType=""Button"">
  <Grid>
    <Border x:Name=""bg"" CornerRadius=""8"" Background=""Transparent"" BorderBrush=""Transparent"" BorderThickness=""2""/>
    <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""bg"" Property=""Background"" Value=""@HOVER@""/><Setter Property=""Foreground"" Value=""@HOVERINK@""/></Trigger>
    <Trigger Property=""IsPressed"" Value=""True""><Setter TargetName=""bg"" Property=""Background"" Value=""@PRESSED@""/></Trigger>
    <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""bg"" Property=""BorderBrush"" Value=""@FOCUS@""/></Trigger>
    <Trigger Property=""IsEnabled"" Value=""False""><Setter Property=""Opacity"" Value=""0.4""/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>");

    private ControlTemplate SaveTemplate => _save ??= Load($@"
<ControlTemplate {Ns} TargetType=""Button"">
  <Grid>
    <Border x:Name=""bg"" CornerRadius=""8"" Background=""@ACCENT@"" BorderBrush=""Transparent"" BorderThickness=""2""/>
    <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" Margin=""{{TemplateBinding Padding}}""/>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""bg"" Property=""Background"" Value=""@ACCENTHOVER@""/></Trigger>
    <Trigger Property=""IsPressed"" Value=""True""><Setter TargetName=""bg"" Property=""Background"" Value=""@ACCENTPRESSED@""/></Trigger>
    <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""bg"" Property=""BorderBrush"" Value=""@FOCUS@""/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>");
}
