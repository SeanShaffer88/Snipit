using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Snipit.Core;
using Path = System.IO.Path;

internal static class Program
{
    private const int Width = 640;
    private const int Height = 360;
    private const byte BaseShade = 54;

    [STAThread]
    private static int Main(string[] arguments)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
                throw new InvalidOperationException("Native fixture input requires a disposable GitHub-hosted Windows runner.");
            string runner = Environment.GetEnvironmentVariable("RUNNER_TEMP")
                ?? throw new InvalidOperationException("Missing RUNNER_TEMP.");
            string root = Path.GetFullPath(runner).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string output = Path.GetFullPath(arguments.Single());
            Check(output.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(output),
                "Fixture exports require a fresh directory in RUNNER_TEMP.");
            Check(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "data")),
                "Fixture must have an isolated Snipit data directory.");
            Directory.CreateDirectory(output);
            _ = Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor DPI v2
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Window editor = BuildEditor(output);
            editor.Topmost = true;
            int result = 1;
            editor.ContentRendered += async (_, _) =>
            {
                try
                {
                    Console.WriteLine("Exercising the actual Snipit WPF editor with owned-window Win32 SendInput.");
                    await new EditorCheck(editor, output).Run();
                    result = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { application.Shutdown(result); }
            };
            return application.Run(editor);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Window BuildEditor(string output)
    {
        byte[] bgra = new byte[Width * Height * 4];
        for (int index = 0; index < bgra.Length; index += 4)
        {
            bgra[index] = bgra[index + 1] = bgra[index + 2] = BaseShade;
            bgra[index + 3] = 255;
        }
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, bgra, Width * 4);
        bitmap.Freeze();

        Assembly assembly = typeof(Snipit.Program).Assembly;
        Type Required(string name) => assembly.GetType("Snipit." + name, throwOnError: true)!;
        var capture = Activator.CreateInstance(Required("CaptureResult"),
            bitmap, new RectPx(0, 0, Width, Height), false, Width * Height / 1_000_000.0, 1);
        var meta = Activator.CreateInstance(Required("CaptureMeta"),
            "Owned test fixture", null, null, null, null, null, null);
        var prefs = new Preferences { OutputFolder = output, ImageFormat = "png", EditorToolHotkeys = true };
        Action persist = () => { };
        Action<string, string, string?> notify = (title, _, _) => Console.WriteLine("Editor callback: " + title);
        return (Window)(Activator.CreateInstance(Required("EditorWindow"), capture, meta, 1, 0, prefs, persist, notify)
            ?? throw new InvalidOperationException("Editor construction returned null."));
    }

    private sealed class EditorCheck
    {
        private readonly Window _editor;
        private readonly Canvas _layer;
        private readonly Canvas _overlay;
        private readonly IntPtr _hwnd;
        private readonly string _output;

        public EditorCheck(Window editor, string output)
        {
            _editor = editor;
            _output = output;
            _hwnd = new WindowInteropHelper(editor).Handle;
            _layer = GetCanvas("_layer");
            _overlay = GetCanvas("_lineOverlay");
        }

        private Canvas GetCanvas(string field) =>
            _editor.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_editor) as Canvas
            ?? throw new InvalidOperationException($"Missing editor canvas {field}.");

        public async Task Run()
        {
            _editor.Activate();
            await Task.Delay(250);
            Check(_editor.IsLoaded && _hwnd != IntPtr.Zero, "Editor did not load.");
            Console.WriteLine($"Input viewport: {_layer.ActualWidth} x {_layer.ActualHeight} image units.");

            await ClickControl<ToggleButton>("Line (I)");
            await ClickControl<ToggleButton>("Blue");
            var slider = FindControl<Slider>("Stroke width");
            var range = (IRangeValueProvider?)FrameworkElementAutomationPeer.CreatePeerForElement(slider)
                ?.GetPattern(PatternInterface.RangeValue);
            Check(range is not null, "Stroke width does not expose its range.");
            range!.SetValue(8);
            await Task.Delay(100);

            var a = new Point(80, 90);
            var b = new Point(320, 90);
            var movedA = new Point(100, 155);
            var movedB = new Point(335, 220);
            await Drag(a, b);
            AssertLine(a, b, 8);
            AssertHandles();
            byte[] selected = await SaveImage();
            AssertBlue(selected, new Point(200, 90));
            int strokePixels = Enumerable.Range(80, 21).Count(y => IsBlue(selected, 200, y));
            Check(strokePixels is >= 6 and <= 10, $"An 8-pixel line showed {strokePixels} solid-blue cross-section pixels.");
            Console.WriteLine("Horizontal line draws with the Blue swatch and the requested width.");

            await Key(0x1B); // Escape clears current selection
            Check(!_overlay.Children.OfType<Rectangle>().Any(handle => handle.IsVisible), "Escape did not clear endpoint handles.");
            byte[] original = await SaveImage();
            AssertSamePixels(selected, original, "Selection handles leaked into the saved PNG.");
            Console.WriteLine("While-selected and selection-cleared exports have identical decoded PNG pixels.");

            await ClickImage(Middle(a, b));
            AssertHandles();
            await Drag(a, movedA);
            AssertLine(movedA, b, 8);
            byte[] firstMove = await SaveImage();
            AssertBlue(firstMove, movedA);
            Check(!IsBlue(firstMove, (int)a.X, (int)a.Y), "The original first endpoint was still painted.");

            await ClickImage(Middle(movedA, b));
            await Drag(b, movedB);
            AssertLine(movedA, movedB, 8);
            byte[] bothMoves = await SaveImage();
            AssertBlue(bothMoves, movedB);
            Check(!IsBlue(bothMoves, (int)b.X, (int)b.Y), "The original last endpoint was still painted.");
            Console.WriteLine("Both endpoints can be dragged while preserving the line style.");

            await ClickImage(Middle(movedA, movedB));
            await Drag(movedA, new Point(148, 237), cancel: true);
            AssertLine(movedA, movedB, 8);
            AssertSamePixels(await SaveImage(), bothMoves, "Escape committed a partial endpoint edit.");

            await Command(ApplicationCommands.Undo);
            AssertLine(movedA, b, 8);
            AssertSamePixels(await SaveImage(), firstMove, "Undo did not reverse just the last endpoint move.");
            await Command(ApplicationCommands.Undo);
            AssertLine(a, b, 8);
            AssertSamePixels(await SaveImage(), original, "Second Undo did not reverse just the first endpoint move.");
            await Command(ApplicationCommands.Undo);
            Check(!_layer.Children.OfType<Line>().Any(), "Third Undo did not remove the line creation.");

            await Command(ApplicationCommands.Redo);
            AssertLine(a, b, 8);
            await Command(ApplicationCommands.Redo);
            AssertLine(movedA, b, 8);
            await Command(ApplicationCommands.Redo);
            AssertLine(movedA, movedB, 8);
            AssertSamePixels(await SaveImage(), bothMoves, "Redo did not restore the endpoint edits.");
            Console.WriteLine("Escape cancels a live drag and adds no history. Undo and Redo each traverse standalone edits.");

            await ClickControl<ToggleButton>("Connector (L)");
            await Key(0x49); // I switches back to the Line tool
            Check(FindControl<ToggleButton>("Line (I)").IsChecked == true, "I did not select Line.");
            await Drag(new Point(430, 80), new Point(430, 275));
            Check(_layer.Children.OfType<Line>().Count() == 2, "Vertical drag did not create a standalone line.");
            AssertBlue(await SaveImage(), new Point(430, 180));

            await Key(0x4C); // L remains assigned to the Connector tool
            Check(FindControl<ToggleButton>("Connector (L)").IsChecked == true, "L did not select Connector.");
            await Drag(new Point(42, 65), new Point(250, 125));
            Check(_layer.Children.OfType<System.Windows.Shapes.Path>().Count() == 1,
                "Connector drag did not retain its separate annotation type.");
            byte[] connector = await SaveImage();
            AssertBlue(connector, new Point(130, 65));
            AssertBlue(connector, new Point(250, 105));
            AssertBlue(connector, new Point(250, 125));
            Console.WriteLine("Vertical Line and the L shortcut for the existing elbow Connector both render.");

            Console.WriteLine($"PASS: Snipit editor integration. PNG artifacts were written under {_output}");
        }

        private void AssertLine(Point start, Point end, double width)
        {
            Line[] lines = _layer.Children.OfType<Line>().ToArray();
            Check(lines.Length == 1, $"Expected one standalone annotation, got {lines.Length}.");
            var line = lines[0];
            Check((new Point(line.X1, line.Y1) - start).Length < 2 &&
                (new Point(line.X2, line.Y2) - end).Length < 2,
                $"Expected endpoints {start} and {end}, got {line.X1},{line.Y1} and {line.X2},{line.Y2}.");
            Check(Math.Abs(line.StrokeThickness - width) < 0.01 &&
                line.Stroke is SolidColorBrush brush && brush.Color == Color.FromRgb(0x2D, 0x9C, 0xFF),
                "The line did not retain the selected width and Blue stroke.");
        }

        private void AssertHandles()
        {
            int count = _overlay.Children.OfType<Rectangle>().Count(handle => handle.IsVisible &&
                handle.ActualWidth > 0 && handle.ActualHeight > 0);
            Check(count == 2, $"Expected two visible endpoint handles, got {count}.");
        }

        private static Point Middle(Point a, Point b) => a + (b - a) / 2;

        private T FindControl<T>(string name) where T : FrameworkElement =>
            Descendants(_editor).OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (DependencyObject nested in Descendants(child)) yield return nested;
            }
        }

        private async Task ClickControl<T>(string name) where T : FrameworkElement
        {
            T control = FindControl<T>(name);
            Check(control.IsVisible && control.IsEnabled, $"Toolbar item {name} is unavailable.");
            await ClickAt(control.PointToScreen(new Point(control.ActualWidth / 2, control.ActualHeight / 2)));
        }

        private Task ClickImage(Point point) => ClickAt(_layer.PointToScreen(point));

        private async Task ClickAt(Point screen)
        {
            Move(screen);
            await Task.Delay(70);
            Native.MouseButton(down: true);
            try { await Task.Delay(70); }
            finally { Native.MouseButton(down: false); }
            await Task.Delay(120);
        }

        private async Task Drag(Point start, Point end, bool cancel = false)
        {
            Move(_layer.PointToScreen(start));
            await Task.Delay(70);
            Native.MouseButton(down: true);
            try
            {
                await Task.Delay(70);
                for (int step = 1; step <= 4; step++)
                {
                    Move(_layer.PointToScreen(start + (end - start) * (step / 4.0)));
                    await Task.Delay(55);
                }
                if (cancel) await Key(0x1B);
            }
            finally { Native.MouseButton(down: false); }
            await Task.Delay(140);
        }

        private async Task Key(ushort key)
        {
            EnsureForeground();
            Native.KeyPress(key);
            await Task.Delay(140);
        }

        private void EnsureForeground() =>
            Check(Native.GetForegroundWindow() == _hwnd, "Refusing native input outside the owned editor window.");

        private void Move(Point screen)
        {
            EnsureForeground();
            var target = new Native.Point((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            Check(Native.GetAncestor(Native.WindowFromPoint(target), 2) == _hwnd,
                $"Refusing native input at screen coordinate {screen} outside the owned editor.");
            Native.MoveAbsolute(target);
        }

        private async Task Command(RoutedCommand command)
        {
            Check(command.CanExecute(null, _editor), $"Editor command {command.Name} is disabled.");
            command.Execute(null, _editor);
            await Task.Delay(130);
        }

        private async Task<byte[]> SaveImage()
        {
            var previous = Directory.GetFiles(_output, "*.png").ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(FindControl<Button>("Save").IsEnabled, "The Save action is unavailable.");
            await Command(ApplicationCommands.Save);
            var additions = Directory.GetFiles(_output, "*.png").Where(path => !previous.Contains(path)).ToArray();
            Check(additions.Length == 1, $"Save wrote {additions.Length} new PNG files instead of one.");
            using var file = File.OpenRead(additions[0]);
            var decoder = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(decoder.Frames.Count == 1, "Save wrote an unexpected PNG frame count.");
            BitmapSource frame = decoder.Frames[0];
            Check(frame.PixelWidth == Width && frame.PixelHeight == Height, "Save changed the fixture dimensions.");
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            byte[] result = new byte[Width * Height * 4];
            bitmap.CopyPixels(result, Width * 4, 0);
            return result;
        }
    }

    private static bool IsBlue(byte[] image, int x, int y)
    {
        int offset = (y * Width + x) * 4;
        return image[offset] >= 249 && Math.Abs(image[offset + 1] - 0x9C) <= 4 &&
            Math.Abs(image[offset + 2] - 0x2D) <= 4 && image[offset + 3] == 255;
    }

    private static void AssertBlue(byte[] image, Point point)
    {
        for (int y = (int)point.Y - 2; y <= (int)point.Y + 2; y++)
            for (int x = (int)point.X - 2; x <= (int)point.X + 2; x++)
                if (IsBlue(image, x, y)) return;
        throw new InvalidOperationException($"Expected a Blue stroke near image point {point}.");
    }

    private static void AssertSamePixels(byte[] actual, byte[] expected, string error) =>
        Check(actual.AsSpan().SequenceEqual(expected), error);

    private static void Check(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point(int x, int y)
        {
            public int X = x;
            public int Y = y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Mouse
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public UIntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Keyboard
        {
            public ushort VirtualKey, ScanCode;
            public uint Flags, Time;
            public UIntPtr Extra;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputData
        {
            [FieldOffset(0)] public Mouse Mouse;
            [FieldOffset(0)] public Keyboard Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputData Data;
        }

        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point screen);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flag);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] events, int size);

        public static void MoveAbsolute(Point point)
        {
            int width = GetSystemMetrics(78);
            int height = GetSystemMetrics(79);
            Check(width > 1 && height > 1, "The runner has no usable virtual desktop.");
            int x = (int)Math.Round((point.X - GetSystemMetrics(76)) * 65535.0 / (width - 1));
            int y = (int)Math.Round((point.Y - GetSystemMetrics(77)) * 65535.0 / (height - 1));
            Check(x is >= 0 and <= 65535 && y is >= 0 and <= 65535, "Pointer exceeds the virtual desktop.");
            var input = new Input();
            input.Data.Mouse = new Mouse { X = x, Y = y, Flags = 0xC001 }; // MOVE | VIRTUALDESK | ABSOLUTE
            Send(input);
        }

        public static void MouseButton(bool down)
        {
            var input = new Input();
            input.Data.Mouse.Flags = down ? 0x0002u : 0x0004u;
            Send(input);
        }

        public static void KeyPress(ushort key)
        {
            var press = new Input { Type = 1 };
            press.Data.Keyboard.VirtualKey = key;
            var release = press;
            release.Data.Keyboard.Flags = 0x0002;
            Send(press, release);
        }

        private static void Send(params Input[] events)
        {
            if (SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) != events.Length)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed.");
        }
    }
}
