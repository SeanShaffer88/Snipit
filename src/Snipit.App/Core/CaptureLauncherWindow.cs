using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Snipit.Core;
using Snipit.Native;

namespace Snipit;

/// <summary>On-demand launch panel attached to the top of the monitor.</summary>
internal sealed class CaptureLauncherWindow : Window
{
    public CaptureLauncherWindow(MonitorInfo monitor, Action<Action> dispatch,
        params (string Label, string Glyph, Action Run)[] commands)
    {
        double scale = monitor.Scale;
        Width = Math.Min(690, monitor.Bounds.Width / scale);
        double sideInset = Math.Min(67, Width * 0.16);
        double contentWidth = Width - 2 * sideInset;
        int columns = Math.Clamp((int)(contentWidth / 106), 1, 3);
        int rows = (commands.Length + columns - 1) / columns;
        Height = 44 + rows * 30;
        double bevel = Math.Min(Height * 0.55, sideInset - Math.Min(10, Width * 0.025));
        bool showGlyphs = contentWidth / columns >= 130;
        Title = "Snipit capture launcher";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        AutomationProperties.SetName(this, Title);

        bool highContrast = SystemParameters.HighContrast;
        Brush background = highContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(29, 31, 35));
        Brush foreground = highContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        Brush edge = highContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(86, 207, 226));
        var vertices = new PointCollection
        {
            new(1, 1), new(Width - 1, 1),
            new(Width - bevel, Height - 1), new(bevel, Height - 1),
        };
        var surface = new Grid();
        surface.Children.Add(new Polygon { Points = vertices, Fill = background, Stroke = edge, StrokeThickness = 1 });

        var content = new DockPanel { Margin = new Thickness(sideInset, 8, sideInset, 14) };
        var title = new TextBlock
        {
            Text = "Snipit", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = edge,
            Margin = new Thickness(2, 0, 0, 6),
        };
        DockPanel.SetDock(title, Dock.Top);
        content.Children.Add(title);
        var buttons = new System.Windows.Controls.Primitives.UniformGrid { Columns = columns };
        for (int index = 0; index < commands.Length; index++)
        {
            var command = commands[index];
            bool primary = index == 0;
            var label = new DockPanel();
            if (showGlyphs)
            {
                var glyph = new TextBlock
                {
                    Text = command.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(glyph, Dock.Left);
                label.Children.Add(glyph);
            }
            label.Children.Add(new TextBlock
            {
                Text = command.Label, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var button = new Button
            {
                Content = label, Margin = new Thickness(2), Padding = new Thickness(3, 1, 3, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = command.Label,
                Foreground = primary && !highContrast ? Brushes.Black : foreground,
            };
            if (!highContrast)
            {
                button.Background = primary ? edge : new SolidColorBrush(Color.FromRgb(43, 47, 53));
                button.BorderBrush = primary ? edge : new SolidColorBrush(Color.FromRgb(92, 102, 112));
            }
            AutomationProperties.SetName(button, command.Label);
            button.Click += (_, _) => dispatch(command.Run);
            buttons.Children.Add(button);
        }
        content.Children.Add(buttons);
        surface.Children.Add(content);
        Content = surface;

        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int width = (int)Math.Round(Width * scale), height = (int)Math.Round(Height * scale);
            int left = monitor.Bounds.X + (monitor.Bounds.Width - width) / 2;
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, monitor.Bounds.Y, width, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() => { if (IsVisible && !IsActive) Close(); });
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Loaded += (_, _) => (buttons.Children[0] as Button)?.Focus();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    public static void FlushDismissal() => _ = DwmFlush();
}
