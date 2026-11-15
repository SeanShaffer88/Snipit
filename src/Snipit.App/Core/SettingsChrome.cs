using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Snipit;

/// <summary>Settings-only colors and selection styles. High Contrast uses standard WPF controls.</summary>
internal static class SettingsChrome
{
    public static readonly Brush Background = ColorBrush("#161E28");
    public static readonly Brush Panel = ColorBrush("#222F3D");
    public static readonly Brush Rail = ColorBrush("#1B2632");
    public static readonly Brush Edge = ColorBrush("#6C8298");
    public static readonly Brush Ink = ColorBrush("#EDF5FC");
    public static readonly Brush Muted = ColorBrush("#B4C6D8");
    public static readonly Brush Accent = ColorBrush("#71D8ED");
    public static readonly Brush Selected = ColorBrush("#27495B");
    public static readonly Brush Danger = ColorBrush("#FFC1C1");
    private static readonly Brush Hover = ColorBrush("#314656");
    private static readonly Brush Pressed = ColorBrush("#233847");
    private static readonly Brush AccentHover = ColorBrush("#9DE9F6");
    private static readonly Brush AccentPressed = ColorBrush("#55C2D9");

    public static readonly Style NavigationItemStyle = MakeNavigationStyle();
    public static readonly Style ButtonStyle = MakeButtonStyle(false);
    public static readonly Style PrimaryButtonStyle = MakeButtonStyle(true);

    private static Brush ColorBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static Style MakeNavigationStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, MakeTemplate(typeof(ListBoxItem), HorizontalAlignment.Left)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 5, 6)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        AddTrigger(style, UIElement.IsMouseOverProperty, (Control.BackgroundProperty, Hover));
        AddTrigger(style, ListBoxItem.IsSelectedProperty,
            (Control.BackgroundProperty, Selected), (Control.ForegroundProperty, Accent), (Control.FontWeightProperty, FontWeights.SemiBold));
        AddTrigger(style, UIElement.IsKeyboardFocusWithinProperty, (Control.BorderBrushProperty, Accent));
        return style;
    }

    private static Style MakeButtonStyle(bool primary)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, MakeTemplate(typeof(Button), HorizontalAlignment.Center)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 4, 12, 4)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, primary ? Accent : Panel));
        style.Setters.Add(new Setter(Control.ForegroundProperty, primary ? Background : Ink));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, primary ? Accent : Edge));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        AddTrigger(style, UIElement.IsMouseOverProperty, (Control.BackgroundProperty, primary ? AccentHover : Hover));
        AddTrigger(style, ButtonBase.IsPressedProperty, (Control.BackgroundProperty, primary ? AccentPressed : Pressed));
        AddTrigger(style, UIElement.IsKeyboardFocusedProperty, (Control.BorderBrushProperty, primary ? Background : Accent));
        return style;
    }

    private static ControlTemplate MakeTemplate(Type type, HorizontalAlignment alignment)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, alignment);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(type) { VisualTree = border };
    }

    private static void AddTrigger(Style style, DependencyProperty property, params (DependencyProperty Property, object Value)[] values)
    {
        var trigger = new Trigger { Property = property, Value = true };
        foreach (var pair in values) trigger.Setters.Add(new Setter(pair.Property, pair.Value));
        style.Triggers.Add(trigger);
    }
}
