namespace Snipit.Core;

/// <summary>
/// Auto-applied effect choices for captures (Settings → Auto effect). Baked into the
/// pixels of the captured image right after the screen is captured and before the
/// clipboard and the file copy — the one place "editing" legitimately happens in a
/// capture tool: at capture time, not afterwards on the saved file.
/// </summary>
public static class AutoEffects
{
    public static readonly string[] All = { "None", "Border", "Grayscale" };
    public const string Default = "None";

    public static string Sanitize(string? name) =>
        name is not null && Array.IndexOf(All, name) >= 0 ? name : Default;

    /// <summary>Bakes the effect into a BGRA buffer in place. Unknown or None = untouched.</summary>
    public static void Apply(byte[] bgra, int width, int height, string? effect)
    {
        switch (Sanitize(effect))
        {
            case "Grayscale":
                PixelAdjust.Grayscale(bgra);
                break;
            case "Border":
                PixelAdjust.Border(bgra, width, height, Math.Max(2, Math.Min(width, height) / 120), 0, 0, 0);
                break;
        }
    }
}
