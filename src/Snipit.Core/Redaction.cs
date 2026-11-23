namespace Snipit.Core;

/// <summary>
/// Bakes sensitive spans into a BGRA buffer: every rectangle is painted solid opaque
/// black. The caller supplies the spans from <see cref="Redactor"/>; this class decides
/// which pixels become black. Rectangles are clipped to the buffer (a span reported
/// outside the image contributes nothing), and nothing else is touched — the rest of
/// the capture is preserved exactly as captured.
/// </summary>
public static class Redaction
{
    public static void FillRectangles(byte[] bgra, int width, int height, IReadOnlyList<RectPx> rects)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.Length != (long)width * height * 4)
            throw new ArgumentException("Pixel buffer does not match width × height.", nameof(bgra));
        foreach (var r in rects)
        {
            int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
            int x1 = Math.Min(width, r.Right), y1 = Math.Min(height, r.Bottom);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * width + x) * 4;
                    bgra[i] = 0; bgra[i + 1] = 0; bgra[i + 2] = 0; bgra[i + 3] = 255;
                }
        }
    }
}
