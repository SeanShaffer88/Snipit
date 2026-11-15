using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Snipit.Core;

namespace Snipit;

/// <summary>Every user-facing option (charter: toggles for everything, tooltip on every control).</summary>
internal sealed class SettingsWindow : Window
{
    private readonly Preferences _prefs;
    private readonly Func<Preferences, string?> _apply;
    private readonly Action<string>? _warn;
    private readonly TextBox _folder = new(), _fixW = new(), _fixH = new(), _interval = new(), _extEditor = new(), _ffmpeg = new();
    private readonly TextBlock _presetInfo = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _useLast = new() { Content = "Use last capture region", Padding = new Thickness(8, 3, 8, 3) };
    private int[]? _presetRect;
    private readonly ComboBox _wfCapture = new(), _wfRecording = new();
    private readonly Button _wfManage = new() { Content = "Manage workflows…", Padding = new Thickness(8, 3, 8, 3) };
    private List<WorkflowDef> _workflows = new();
    private readonly ComboBox _format = new(), _target = new(), _delay = new(), _matte = new(), _effect = new();
    private readonly CheckBox _startAtLogin = new() { Content = "Start at login" };
    private readonly bool? _initialStartAtLogin;
    private readonly CheckBox _toasts = new() { Content = "Show notifications" };
    private readonly CheckBox _contrast = new() { Content = "High-contrast highlight" };
    private readonly CheckBox _toolKeys = new() { Content = "Single-key tool shortcuts in the editor" };
    private readonly Slider _dim = new() { Minimum = 20, Maximum = 90, MaxWidth = 200, TickFrequency = 10, IsSnapToTickEnabled = false };
    private readonly TextBlock _error = new() { Foreground = TesselTheme.Danger, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public SettingsWindow(Preferences prefs, Func<Preferences, string?> apply, Action<string>? warn = null)
    {
        _prefs = prefs; _apply = apply; _warn = warn;
        Title = "Snipit settings";
        Rect workArea = SystemParameters.WorkArea;
        Width = Math.Min(640, workArea.Width);
        Height = Math.Min(680, workArea.Height);
        MinWidth = Math.Min(400, Width);
        MinHeight = Math.Min(320, Height);
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;

        _format.ItemsSource = ImageFormats.All;
        _target.ItemsSource = new[] { "Current monitor", "All monitors" };
        _delay.ItemsSource = DelayCycle.Steps;
        _matte.ItemsSource = new[] { "Transparent", "Black", "White" };
        _effect.ItemsSource = AutoEffects.All;

        _folder.Text = prefs.OutputFolder; _folder.ToolTip = "Where captures are saved. Empty = your Pictures folder.";
        _extEditor.Text = prefs.ExternalEditorPath; _extEditor.ToolTip = "Path to an image editor program (for example paint.net or GIMP). The editor window's External editor button opens the saved capture in it. Empty = none.";
        _ffmpeg.Text = prefs.FfmpegPath; _ffmpeg.ToolTip = "Path to your own ffmpeg.exe, used instead of the bundled FFmpeg for exports. Empty = the bundled copy.";
        _format.SelectedItem = prefs.ImageFormat; _format.ToolTip = "File format for saved captures. PNG and TIFF keep transparency; JPEG, BMP, GIF and PDF drop it. A still GIF is a single frame; interval captures in GIF become one animated file. PDF holds one image, losslessly.";
        _presetRect = prefs.PresetRect;
        _useLast.ToolTip = "Sets the preset region to the rectangle of your most recent capture.";
        _useLast.Click += (_, _) =>
        {
            if (_prefs.LastCaptureRect is { Length: 4 } r) { _presetRect = (int[])r.Clone(); }
            UpdatePresetLabel();
        };
        UpdatePresetLabel();
        _workflows = prefs.Workflows.Select(w => new WorkflowDef { Name = w.Name, ApplyTo = w.ApplyTo, Steps = new List<string>(w.Steps) }).ToList();
        RefreshWorkflowCombos(prefs.CaptureWorkflow, prefs.RecordingWorkflow);
        _wfManage.ToolTip = "Create and edit the named workflows that can run after a capture or a recording.";
        _wfManage.Click += (_, _) =>
        {
            var win = new WorkflowWindow(_workflows) { Owner = this };
            if (win.ShowDialog() == true)
            {
                _workflows = win.Result;
                RefreshWorkflowCombos(SelectedWorkflow(_wfCapture), SelectedWorkflow(_wfRecording));
            }
        };
        _fixW.Text = prefs.FixedWidth.ToString(); _fixW.ToolTip = "Width in pixels of the fixed-size region (16 to 16384).";
        _fixH.Text = prefs.FixedHeight.ToString(); _fixH.ToolTip = "Height in pixels of the fixed-size region (16 to 16384).";
        _interval.Text = prefs.IntervalSeconds.ToString(); _interval.ToolTip = "Seconds between frames for interval capture (1 to 86400). Start it from the tray menu.";
        _target.SelectedIndex = prefs.FullScreenTarget == "AllMonitors" ? 1 : 0; _target.ToolTip = "What Print Screen captures in Quick Mode and what Full screen now captures.";
        _delay.SelectedItem = prefs.DefaultDelaySeconds; _delay.ToolTip = "Capture countdown in seconds. Quick Mode bypasses it.";
        _matte.SelectedItem = prefs.MatteColor; _matte.ToolTip = "Fill for empty corners when monitors form an L-shape.";
        _effect.SelectedItem = AutoEffects.Sanitize(prefs.AutoEffect); _effect.ToolTip = "Applied automatically to every capture. Border draws a thin black frame; Grayscale converts to grey.";
        var (startAtLogin, startupWarning) = StartAtLogin.Read();
        _initialStartAtLogin = startAtLogin;
        _startAtLogin.IsChecked = startAtLogin == true;
        _startAtLogin.IsEnabled = startAtLogin.HasValue;
        string startupHelp = startupWarning ?? (startAtLogin.HasValue
            ? "Open Snipit automatically when you sign in to Windows."
            : "Available for installed copies of Snipit.");
        _startAtLogin.ToolTip = startupHelp;
        ToolTipService.SetShowOnDisabled(_startAtLogin, true);
        AutomationProperties.SetHelpText(_startAtLogin, startupHelp);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        SetError(startupWarning);
        _toasts.IsChecked = prefs.ShowToasts; _toasts.ToolTip = "Show notifications for other tools and errors. Routine screen captures are silent.";
        _toolKeys.IsChecked = prefs.EditorToolHotkeys; _toolKeys.ToolTip = "P pen, H highlight, A arrow, L connector, R rect, O ellipse, T text, N step, K callout, G stamp, B blur, S spotlight, M magnify, C crop, X cut out. Turn off if letter keys clash with your assistive technology.";
        _contrast.IsChecked = prefs.HighContrastHighlight; _contrast.ToolTip = "Thicker yellow highlight and opaque hint bars.";
        _dim.Value = prefs.DimmerOpacity * 100; _dim.ToolTip = "How dark the screen gets while picking.";

        Content = BuildLayout(startupWarning is null ? startupHelp : null);
        TesselTheme.Apply(this);
    }

    private Grid BuildLayout(string? startupHelp)
    {
        bool highContrast = SystemParameters.HighContrast;
        Brush surface = highContrast ? SystemColors.WindowBrush : TesselTheme.Surface;
        Brush edge = highContrast ? SystemColors.WindowTextBrush : TesselTheme.Edge;

        var heading = new TextBlock { Text = "Settings", FontSize = 24, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        var headingPanel = new StackPanel();
        headingPanel.Children.Add(heading);
        headingPanel.Children.Add(new Border
        {
            Width = 32, Height = 3, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Background = highContrast ? SystemColors.WindowTextBrush : TesselTheme.Action,
        });
        var header = new Border { BorderBrush = edge, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(24, 16, 24, 16), Child = headingPanel };

        var sections = new StackPanel { Margin = new Thickness(24, 16, 24, 8) };
        void AddSection(string title, UIElement content)
        {
            var label = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
            var section = new StackPanel();
            section.Children.Add(label);
            section.Children.Add(content);
            sections.Children.Add(new Border
            {
                BorderBrush = edge, BorderThickness = new Thickness(1), Background = surface,
                CornerRadius = new CornerRadius(4), Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 12), Child = section,
            });
        }

        var general = new StackPanel();
        foreach (var toggle in new[] { _startAtLogin, _toasts, _contrast, _toolKeys })
        {
            toggle.MinHeight = 26;
            toggle.VerticalContentAlignment = VerticalAlignment.Center;
            general.Children.Add(toggle);
            if (toggle == _startAtLogin && startupHelp is not null)
            {
                general.Children.Add(new TextBlock
                {
                    Text = startupHelp, Foreground = TesselTheme.Muted, Tag = "keep",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 0, 0, 4),
                });
            }
        }
        general.Children.Add(_error);
        AddSection("General", general);
        AddSection("Capture", FormRows(
            ("Output folder", _folder), ("Image format", _format), ("Full-screen target", _target),
            ("Default delay (s)", _delay), ("Preset region", _presetInfo), (null, _useLast),
            ("Fixed region width", _fixW), ("Fixed region height", _fixH), ("Interval (seconds)", _interval),
            ("Gap fill", _matte), ("Dimmer darkness", _dim), ("Auto effect", _effect)));
        AddSection("Workflows", FormRows(("After capture", _wfCapture), ("After recording", _wfRecording), (null, _wfManage)));
        AddSection("External tools", FormRows(("External editor", _extEditor), ("FFmpeg override", _ffmpeg)));
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Content = sections,
        };

        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 80, MinHeight = 30, ToolTip = "Apply and store settings." };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Close without changes." };
        ok.Click += (_, _) => Commit();
        // The window is shown modeless (Show, not ShowDialog), where IsCancel and
        // DialogResult do nothing — Cancel and Esc close explicitly.
        cancel.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var footer = new Border { Background = surface, BorderBrush = edge, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12, 24, 12), Child = buttons };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(scroll, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(header); root.Children.Add(scroll); root.Children.Add(footer);
        return root;
    }

    private static Grid FormRows(params (string? Label, FrameworkElement Field)[] rows)
    {
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        for (int row = 0; row < rows.Length; row++)
        {
            var (label, field) = rows[row];
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (label is not null)
            {
                var text = new TextBlock
                {
                    Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 3, 12, 3),
                };
                Grid.SetRow(text, row);
                form.Children.Add(text);
            }
            field.Margin = new Thickness(0, 3, 0, 3);
            field.MinHeight = 26;
            if (field is Button) field.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            form.Children.Add(field);
        }
        return form;
    }

    private void SetError(string? message)
    {
        _error.Text = message ?? "";
        _error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (_error.IsLoaded && _error.Visibility == Visibility.Visible)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                _error.BringIntoView();
                UIElementAutomationPeer.FromElement(_error)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void UpdatePresetLabel() =>
        _presetInfo.Text = _presetRect is { Length: 4 } r ? $"{r[0]}, {r[1]} · {r[2]} × {r[3]} px" : "Not set";

    private void RefreshWorkflowCombos(string? capture, string? recording)
    {
        foreach (var (combo, selected) in new[] { (_wfCapture, capture), (_wfRecording, recording) })
        {
            combo.Items.Clear();
            combo.Items.Add("(none)");
            foreach (var w in _workflows) combo.Items.Add(w.Name);
            combo.SelectedItem = selected is not null && combo.Items.Contains(selected) ? selected : "(none)";
        }
    }

    private static string SelectedWorkflow(ComboBox combo) =>
        combo.SelectedItem as string is { } s && s != "(none)" ? s : "";

    private void Commit()
    {
        // Start from a copy of what is stored so settings this window does not edit (effect sizes, recording
        // options, hotkeys flag, ...) are carried over instead of being reset to defaults.
        var p = _prefs.Clone();
        p.OutputFolder = _folder.Text.Trim();
        p.ImageFormat = (string)_format.SelectedItem;
        p.PresetRect = _presetRect;
        p.ExternalEditorPath = _extEditor.Text.Trim();
        p.FfmpegPath = _ffmpeg.Text.Trim();
        p.Workflows = _workflows;
        p.CaptureWorkflow = SelectedWorkflow(_wfCapture);
        p.RecordingWorkflow = SelectedWorkflow(_wfRecording);
        p.FullScreenTarget = _target.SelectedIndex == 1 ? "AllMonitors" : "CurrentMonitor";
        p.DefaultDelaySeconds = (int)_delay.SelectedItem;
        p.MatteColor = (string)_matte.SelectedItem;
        p.ShowToasts = _toasts.IsChecked == true;
        p.HighContrastHighlight = _contrast.IsChecked == true;
        p.DimmerOpacity = _dim.Value / 100;
        p.AutoEffect = (string)_effect.SelectedItem;
        p.EditorToolHotkeys = _toolKeys.IsChecked == true;
        // Per-field validation (owner field report, 2026-11-13): one bad field must
        // never discard the whole save — an invalid entry keeps its previous value
        // and is named in the warning, everything else applies and persists.
        var problems = new List<string>();
        if (!int.TryParse(_fixW.Text, out int fw) || !int.TryParse(_fixH.Text, out int fh)
            || fw is < FixedRegion.MinSize or > FixedRegion.MaxSize || fh is < FixedRegion.MinSize or > FixedRegion.MaxSize)
        { fw = _prefs.FixedWidth; fh = _prefs.FixedHeight; problems.Add($"Fixed region size must be {FixedRegion.MinSize} to {FixedRegion.MaxSize} pixels — kept {fw}×{fh}."); }
        if (!int.TryParse(_interval.Text, out int iv) || iv is < IntervalPlan.MinSeconds or > IntervalPlan.MaxSeconds)
        { iv = _prefs.IntervalSeconds; problems.Add($"Interval must be {IntervalPlan.MinSeconds} to {IntervalPlan.MaxSeconds} seconds — kept {iv}."); }
        p.FixedWidth = fw; p.FixedHeight = fh; p.IntervalSeconds = iv;
        string? warning = _apply(p);
        if (warning is not null) problems.Add(warning);
        bool startAtLogin = _startAtLogin.IsChecked == true;
        if (_initialStartAtLogin is bool initial && startAtLogin != initial
            && StartAtLogin.SetEnabled(startAtLogin) is { } startupWarning)
        {
            problems.Add(startupWarning);
            SetError("Settings saved, with notes: " + string.Join(" ", problems));
            return;
        }
        if (problems.Count > 0)
        {
            string msg = "Settings saved, with notes: " + string.Join(" ", problems);
            if (_warn is not null) _warn(msg); else SetError(msg);
        }
        Close();
    }
}
