using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Snipit;

internal sealed partial class EditorWindow
{
    private const double MinLineLength = 4;
    private const double LineHitRadius = 6; // Preview device-independent pixels.
    private static readonly object StandaloneLine = new();
    private readonly Canvas _lineOverlay = new() { IsHitTestVisible = false };
    private readonly Rectangle _firstLineHandle = NewLineHandle();
    private readonly Rectangle _lastLineHandle = NewLineHandle();
    private Line? _selectedLine, _lineDraft;
    private LineEndpointEdit? _lineEndpointEdit;
    private Vector _lineGrabOffset;

    private bool HasLineGesture => _lineDraft is not null || _lineEndpointEdit is not null;

    private sealed class LineEndpointEdit(Line line, bool first, Point before) : IEdit
    {
        public Point Before { get; } = before;
        public Point After { get; set; } = before;
        public Point OtherEndpoint => Endpoint(line, !first);
        public long Cost => 0;
        public void Undo() => SetEndpoint(line, first, Before);
        public void Redo() => SetEndpoint(line, first, After);
    }

    private void BeginLineGesture(Point hit)
    {
        if (_selectedLine is { } selected)
        {
            Point first = Endpoint(selected, true), last = Endpoint(selected, false);
            double firstDistance = (hit - first).Length, lastDistance = (hit - last).Length;
            if (Math.Min(firstDistance, lastDistance) <= LineHitRadius / LinePreviewScale())
            {
                if (!_layer.CaptureMouse()) return;
                bool isFirst = firstDistance <= lastDistance;
                Point endpoint = isFirst ? first : last;
                _lineGrabOffset = endpoint - hit;
                _lineEndpointEdit = new LineEndpointEdit(selected, isFirst, endpoint);
                return;
            }
        }

        foreach (var line in _layer.Children.OfType<Line>().Reverse())
        {
            if (line.Tag != StandaloneLine || !HitsLine(hit, line)) continue;
            SelectLine(line);
            return;
        }

        ClearLineSelection();
        if (!_layer.CaptureMouse()) return;
        Point start = Clamp(hit);
        _lineDraft = new Line
        {
            Tag = StandaloneLine,
            X1 = start.X, Y1 = start.Y, X2 = start.X, Y2 = start.Y,
            Stroke = Stroke(), StrokeThickness = _thickness.Value,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        _lineOverlay.Children.Add(_lineDraft);
    }

    private bool HitsLine(Point hit, Line line)
    {
        Point start = Endpoint(line, true);
        Vector span = Endpoint(line, false) - start;
        double fraction = Math.Clamp(Vector.Multiply(hit - start, span) / span.LengthSquared, 0, 1);
        return (hit - (start + span * fraction)).Length <= LineHitRadius / LinePreviewScale() + line.StrokeThickness / 2;
    }

    private void UpdateLineGesture(Point point)
    {
        if (_lineDraft is { } draft) SetEndpoint(draft, false, point);
        if (_lineEndpointEdit is not { } edit) return;
        Point proposed = Clamp(point + _lineGrabOffset);
        if ((proposed - edit.OtherEndpoint).Length < MinLineLength) return;
        edit.After = proposed;
        edit.Redo();
        UpdateLineHandles();
    }

    private void FinishLineGesture(Point point)
    {
        if (_lineDraft is { } draft)
        {
            _lineDraft = null;
            _lineOverlay.Children.Remove(draft);
            if ((point - Endpoint(draft, true)).Length < MinLineLength) return;
            SetEndpoint(draft, false, point);
            _layer.Children.Add(draft);
            Push(new AddEdit(_layer, draft));
            SelectLine(draft);
        }
        if (_lineEndpointEdit is not { } edit) return;
        Point proposed = Clamp(point + _lineGrabOffset);
        _lineEndpointEdit = null;
        if ((proposed - edit.OtherEndpoint).Length < MinLineLength)
            edit.Undo();
        else if (proposed != edit.Before)
        {
            edit.After = proposed;
            edit.Redo();
            Push(edit);
        }
        else edit.Undo();
        UpdateLineHandles();
    }

    private void CancelLineGesture()
    {
        if (_lineDraft is { } draft)
        {
            _lineDraft = null;
            _lineOverlay.Children.Remove(draft);
        }
        if (_lineEndpointEdit is not { } edit) return;
        _lineEndpointEdit = null;
        edit.Undo();
        UpdateLineHandles();
    }

    private void ClearLineSelection()
    {
        bool captured = HasLineGesture;
        CancelLineGesture();
        if (captured && _layer.IsMouseCaptured) _layer.ReleaseMouseCapture();
        _selectedLine = null;
        _lineOverlay.Children.Remove(_firstLineHandle);
        _lineOverlay.Children.Remove(_lastLineHandle);
    }

    private void SelectLine(Line line)
    {
        _selectedLine = line;
        if (!_lineOverlay.Children.Contains(_firstLineHandle))
        {
            _lineOverlay.Children.Add(_firstLineHandle);
            _lineOverlay.Children.Add(_lastLineHandle);
        }
        UpdateLineHandles();
        _status.Text = "Line selected. Drag either endpoint to adjust.";
    }

    private static Rectangle NewLineHandle() => new()
    {
        Fill = SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brushes.White,
        Stroke = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Brushes.Black,
    };

    private void UpdateLineHandles()
    {
        if (_selectedLine is not { } line) return;
        double scale = LinePreviewScale();
        SetHandle(_firstLineHandle, Endpoint(line, true), scale);
        SetHandle(_lastLineHandle, Endpoint(line, false), scale);
    }

    private double LinePreviewScale()
    {
        var transform = _layer.TransformToAncestor(this);
        return Math.Max(0.001, (transform.Transform(new Point(1, 0)) - transform.Transform(new Point())).Length);
    }

    private static void SetHandle(Rectangle handle, Point point, double scale)
    {
        double size = 9 / scale;
        handle.Width = handle.Height = size;
        handle.StrokeThickness = 1 / scale;
        Canvas.SetLeft(handle, point.X - size / 2);
        Canvas.SetTop(handle, point.Y - size / 2);
    }

    private static Point Endpoint(Line line, bool first) =>
        first ? new Point(line.X1, line.Y1) : new Point(line.X2, line.Y2);

    private static void SetEndpoint(Line line, bool first, Point value)
    {
        if (first) { line.X1 = value.X; line.Y1 = value.Y; }
        else { line.X2 = value.X; line.Y2 = value.Y; }
    }
}
