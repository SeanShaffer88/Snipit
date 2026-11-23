namespace Snipit.Core;

/// <summary>
/// Whole-image adjustments on a BGRA buffer (4 bytes per pixel, row-major, no
/// row padding). These are exactly the operations the capture pipeline bakes into
/// a capture — the "editing" stage of a capture tool — so they stay pure-logic
/// and offline. <see cref="AutoEffects"/> is the one caller surface.
/// </summary>
public static class PixelAdjust
{
    /// <summary>Paints a solid frame of the given thickness (clamped to half the smaller side) inside the image edges.</summary>
    public static void Border(byte[] bgra, int width, int height, int thickness, byte r, byte g, byte b)
    {
        ValidateFrame(bgra, width, height);
        int t = Math.Clamp(thickness, 0, Math.Min(width, height) / 2);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x >= t && x < width - t && y >= t && y < height - t) continue;
                int i = (y * width + x) * 4;
                bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
            }
        }
    }

    /// <summary>Rec.601 luma: grey = (299 R + 587 G + 114 B) / 1000, rounded.</summary>
    public static void Grayscale(byte[] bgra)
    {
        ValidatePixels(bgra);
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int grey = (299 * bgra[i + 2] + 587 * bgra[i + 1] + 114 * bgra[i] + 500) / 1000;
            bgra[i] = bgra[i + 1] = bgra[i + 2] = (byte)grey;
        }
    }

    private static void ValidatePixels(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length % 4 != 0) throw new ArgumentException("Buffer length must be a whole number of BGRA pixels.", nameof(bgra));
    }

    private static void ValidateFrame(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        if (bgra.Length != (long)width * height * 4)
            throw new ArgumentException($"Buffer length {bgra.Length} does not match {width}×{height} BGRA.", nameof(bgra));
    }
}
