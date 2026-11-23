using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Snipit.Core;
using Snipit.Native;

namespace Snipit;

/// <summary>
/// The viewer — a thin capture-adjacent surface. It shows a capture, its metadata and capture
/// details, and hands it where it belongs: clipboard, file, external editor, Explorer. It edits
/// nothing: anything deeper belongs in an external editor (Settings). The auto-effect, redaction
/// and recognised-text-on-clipboard are baked into the capture itself by the capture pipeline
/// before this window is ever shown.
/// </summary>
internal sealed class EditorWindow : Window
{
    private readonly Preferences _prefs;
    private readonly Action _persist;
    private readonly Action<string, string, string?> _notify;
    private readonly CaptureMeta _meta;
    private readonly Image _image = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4) };
    private readonly Button _openFolder = new() { Content = "Open folder", Visibility = Visibility.Collapsed, Margin = new Thickness(4, 0, 4, 0), ToolTip = "Show the saved file in Explorer." };
    private readonly Button _copyPath = new() { Content = "Copy path", Visibility = Visibility.Collapsed, Margin = new Thickness(4, 0, 4, 0), ToolTip = "Copy the saved file's full path to the clipboard." };
    private readonly EditorChrome _chrome = new(SystemParameters.HighContrast);
    private string? _savedPath;

    public EditorWindow(CaptureResult cap, CaptureMeta meta, int monitorCount, int delay, Preferences prefs, Action persist, Action<string, string, string?> notify)
    {
        _prefs = prefs; _persist = persist; _notify = notify; _meta = meta;
        Title = "Snipit";
        Width = 1180; Height = 720; MinWidth = 920; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties_Name(this, "Snipit viewer");

        var toolbar = BuildToolbar();
        var root = new DockPanel();
        root.Children.Add(toolbar); DockPanel.SetDock(toolbar, Dock.Top);
        Background = _chrome.CanvasBg; Foreground = _chrome.Ink; FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        _status.Foreground = _chrome.Muted; _status.Tag = EditorChrome.Keep;
        foreach (var sb in new[] { _openFolder, _copyPath }) { sb.Template = _chrome.ChipTemplate; sb.Foreground = _chrome.Ink; sb.Tag = EditorChrome.Keep; sb.Height = 22; }
        var statusBar = new DockPanel { Background = _chrome.Surface };
        var statusButtons = new StackPanel { Orientation = Orientation.Horizontal };
        statusButtons.Children.Add(_openFolder); statusButtons.Children.Add(_copyPath);
        DockPanel.SetDock(statusButtons, Dock.Right);
        statusBar.Children.Add(statusButtons); statusBar.Children.Add(_status);
        var statusEdge = new Border { BorderBrush = _chrome.Edge, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusBar };
        DockPanel.SetDock(statusEdge, Dock.Bottom);
        root.Children.Add(statusEdge);
        var side = BuildMetaPanel(cap, meta, monitorCount, delay);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _image, Margin = new Thickness(8), Focusable = false });
        root.Background = _chrome.CanvasBg;
        Content = root;
        if (!SystemParameters.HighContrast) Loaded += (_, _) => TesselTheme.Apply(this);
        else { Background = _chrome.CanvasBg; Foreground = _chrome.Ink; }

        _image.Source = cap.Image;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);

        _openFolder.Click += (_, _) => { if (_savedPath is not null) Process.Start("explorer.exe", $"/select,\"{_savedPath}\""); };
        _copyPath.Click += (_, _) => { if (_savedPath is not null) { try { Clipboard.SetText(_savedPath); _status.Text = "Path copied."; } catch (Exception) { _status.Text = "Clipboard busy."; } } };

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => Save()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopyImage(cap.Image)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Copy, Key.C, ModifierKeys.Control));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

        _status.Text = meta.Mode + (cap.HasGaps ? " · monitor layout has gaps (matte fill)" : "");
    }

    private static void AutomationProperties_Name(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    // ---- UI construction -----------------------------------------------------------------------

    private UIElement BuildToolbar()
    {
        var postActions = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(string label, string icon, string tip, Action a)
        {
            var b = _chrome.ActionCell(label, icon, tip); b.Click += (_, _) => a(); postActions.Children.Add(b);
        }
        Add("Copy", "copy", "Copy the capture to the clipboard. Key: Ctrl+C.", () => CopyImage(_image.Source as BitmapSource));
        if (!string.IsNullOrWhiteSpace(_prefs.ExternalEditorPath))
            Add("Open in editor", "external", "Save, then open the capture in the external editor set in Settings.", () => OpenExternal());
        Add("Reveal", "folder", "Save if needed, then show the capture in Explorer.", () => Reveal());
        var save = _chrome.SaveButton("Save to the output folder."); save.Click += (_, _) => Save(); postActions.Children.Add(save);
        var bar = new Border { Background = _chrome.Bar, BorderBrush = _chrome.Edge, BorderThickness = new Thickness(0, 0, 0, 1), Child = postActions };
        bar.Margin = new Thickness(0, 0, 0, 0);
        return bar;
    }

    private UIElement BuildMetaPanel(CaptureResult cap, CaptureMeta m, int monitors, int delay)
    {
        var lines = new List<string>
        {
            $"Mode: {m.Mode}",
            $"Size: {cap.Image.PixelWidth} × {cap.Image.PixelHeight} px",
            $"Rect (physical px): {cap.Rect}",
            $"Monitors in layout: {monitors}",
            $"Delay used: {(delay > 0 ? delay + " s" : "none")}",
        };
        if (m.App is not null) lines.Add($"App: {m.App}");
        if (m.ControlType is not null) lines.Add($"UIA type: {m.ControlType}");
        if (!string.IsNullOrEmpty(m.ElementName)) lines.Add($"Name: {m.ElementName}");
        if (!string.IsNullOrEmpty(m.AutomationId)) lines.Add($"AutomationId: {m.AutomationId}");
        if (!string.IsNullOrEmpty(m.ClassName)) lines.Add($"Class: {m.ClassName}");
        if (!string.IsNullOrEmpty(m.UiaPath)) lines.Add($"Path: {m.UiaPath}");
        var tb = new TextBox
        {
            Text = string.Join(Environment.NewLine, lines), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = _chrome.Ink, Margin = new Thickness(8), Tag = EditorChrome.Keep,
            ToolTip = "Capture details, handy for bug reports. Select and copy as needed.",
        };
        System.Windows.Automation.AutomationProperties.SetName(tb, "Capture metadata");
        return new Border { Width = 260, Background = _chrome.Surface, BorderBrush = _chrome.Edge, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = tb } };
    }

    // ---- actions -------------------------------------------------------------------------------

    private void OpenExternal()
    {
        if (_savedPath is null) Save();
        if (_savedPath is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_prefs.ExternalEditorPath.Trim()) { Arguments = "\"" + _savedPath + "\"", UseShellExecute = true });
            _status.Text = "Opened in the external editor.";
        }
        catch (Exception ex) { Log.Error("External editor failed", ex); _status.Text = "Could not start the external editor: " + ex.Message; }
    }

    private void Reveal()
    {
        if (_savedPath is null) Save();
        if (_savedPath is not null) Process.Start("explorer.exe", $"/select,\"{_savedPath}\"");
    }

    private void CopyImage(BitmapSource? image)
    {
        if (image is null) { _status.Text = "The capture is not available."; return; }
        _status.Text = ClipboardWriter.SetImage(image) switch
        {
            ImageClipboardResult.ImageAndJpeg => "Copied to clipboard.",
            ImageClipboardResult.ImageOnly => "Image copied. JPEG file paste is unavailable.",
            _ => "Could not copy to the clipboard. Try again.",
        };
    }

    private void Save()
    {
        try
        {
            if (_image.Source is not BitmapSource img) { _status.Text = "The capture is not available."; return; }
            _savedPath = Exporter.Save(img, _prefs);
            _status.Text = "Saved " + _savedPath;
            _openFolder.Visibility = _copyPath.Visibility = Visibility.Visible;
            _notify("Saved", _savedPath, _savedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Save failed", ex);
            _status.Text = "Save failed: " + ex.Message;
        }
    }

    // ---- window geometry (physical px, validated against monitors) -----------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (_prefs.EditorBounds is { Length: 4 } b)
        {
            var saved = new RectPx(b[0], b[1], b[2], b[3]);
            bool visible = MonitorEnumerator.Current.Any(m => m.Bounds.Intersect(saved).Width >= 100 && m.Bounds.Intersect(saved).Height >= 50);
            if (visible && saved.Width >= 300 && saved.Height >= 200)
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, saved.X, saved.Y, saved.Width, saved.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        if (_prefs.EditorMaximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _prefs.EditorMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal && NativeMethods.GetWindowRect(hwnd, out var r))
            _prefs.EditorBounds = new[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
        try { _persist(); } catch (Exception ex) { Log.Warn("Could not persist viewer geometry: " + ex.Message); }
    }
}
