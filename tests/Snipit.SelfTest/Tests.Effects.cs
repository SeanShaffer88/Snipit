using Snipit.Core;

namespace Snipit.SelfTest;

// Effects checks. Each file is one area; Program.cs runs them in order.
static partial class Tests
{
    internal static void Effects()
    {
        Console.WriteLine("AreaMask");
        {
            var a1 = new RectPx(10, 10, 4, 4); var a2 = new RectPx(30, 20, 5, 5);
            Check(AreaMask.Bounds(new[] { a1, a2 }) == new RectPx(10, 10, 25, 15), "areas: bounds hold every area");
            Check(AreaMask.Bounds(Array.Empty<RectPx>()).IsEmpty && AreaMask.Bounds(new[] { RectPx.Empty, a1 }) == a1, "areas: empty list and empty areas are ignored");
            var frame = AreaMask.Bounds(new[] { a1, a2 });
            int fw = frame.Width, fh = frame.Height;
            var fp = new byte[fw * fh * 4];
            for (int i = 0; i < fp.Length; i += 4) { fp[i] = 9; fp[i + 1] = 9; fp[i + 2] = 9; fp[i + 3] = 255; }
            AreaMask.ApplyAlpha(fp, fw, fh, new[] { a1, a2 }, frame);
            int FA(int vx, int vy) => fp[((vy - frame.Y) * fw + (vx - frame.X)) * 4 + 3];
            Check(FA(10, 10) == 255 && FA(13, 13) == 255 && FA(30, 20) == 255 && FA(34, 24) == 255, "areas: pixels inside an area are kept");
            Check(FA(14, 10) == 0 && FA(20, 15) == 0 && FA(29, 20) == 0 && FA(35 - 1, 25 - 1 + 0) == 255, "areas: pixels between areas and just outside edges go transparent");
            int kept = 0; for (int i = 3; i < fp.Length; i += 4) if (fp[i] == 255) kept++;
            Check(kept == 16 + 25, "areas: exactly the area pixels are kept");
            bool clear = true; for (int i = 0; i < fp.Length; i += 4) if (fp[i + 3] == 0 && (fp[i] | fp[i + 1] | fp[i + 2]) != 0) clear = false;
            Check(clear, "areas: cut pixels are fully transparent black");
            var ov = new byte[8 * 8 * 4]; for (int i = 3; i < ov.Length; i += 4) ov[i] = 255;
            AreaMask.ApplyAlpha(ov, 8, 8, new[] { new RectPx(0, 0, 5, 5), new RectPx(3, 3, 5, 5) }, new RectPx(0, 0, 8, 8));
            int okp = 0; for (int i = 3; i < ov.Length; i += 4) if (ov[i] == 255) okp++;
            Check(okp == 25 + 25 - 4, "areas: overlapping areas merge without double counting");
            var off = new byte[4 * 4 * 4]; for (int i = 3; i < off.Length; i += 4) off[i] = 255;
            AreaMask.ApplyAlpha(off, 4, 4, new[] { new RectPx(100, 100, 5, 5), new RectPx(-2, -2, 4, 4) }, new RectPx(0, 0, 4, 4));
            int okq = 0; for (int i = 3; i < off.Length; i += 4) if (off[i] == 255) okq++;
            Check(okq == 4, "areas: areas off the frame are ignored and partly-off areas are clipped");
            bool shortBufThrew = false; try { AreaMask.ApplyAlpha(new byte[4], 2, 2, new[] { a1 }, new RectPx(0, 0, 2, 2)); } catch (ArgumentException) { shortBufThrew = true; }
            Check(shortBufThrew, "areas: a short buffer is refused");
        }



        {
            var ch = new EscapeChord();
            Check(!ch.Press(1000) && !ch.Press(1400) && ch.Press(1800), "escape chord: three presses inside the window fire on the third");
            Check(!ch.Press(2000) && !ch.Press(2100), "escape chord: restarts after firing");
            var slow = new EscapeChord();
            Check(!slow.Press(0) && !slow.Press(1000) && !slow.Press(2000) && !slow.Press(3000), "escape chord: slow presses never fire");
            Check(!slow.Press(3500) && slow.Press(3800), "escape chord: three inside 1.5 s after slow ones fires");
            var reset = new EscapeChord();
            Check(!reset.Press(5000) && !reset.Press(5100) && !reset.Press(10), "escape chord: an earlier tick (counter reset) drops stale presses");
        }



        {
            Check(Math.Abs(WcagContrast.Ratio("#000000", "#FFFFFF") - 21.0) < 0.01, "contrast: black on white is 21:1");
            Check(WcagContrast.Ratio("#777777", "#FFFFFF") is > 4.4 and < 4.6, "contrast: #777 on white is about 4.48:1");
            // The dark theme's text tokens (TesselTheme / EditorChrome) on every surface they sit on.
            string[] surfaces = { "#17171A", "#232326", "#1F1F22", "#28282D", "#2E2E34" };
            bool inkOk = true, mutedOk = true;
            foreach (var surface in surfaces)
            {
                inkOk &= WcagContrast.Ratio("#E6E3E1", surface) >= WcagContrast.AaNormalText;
                mutedOk &= WcagContrast.Ratio("#A3A0A4", surface) >= WcagContrast.AaNormalText;
            }
            Check(inkOk, "contrast: foreground token meets AA 4.5:1 on every dark surface");
            Check(mutedOk, "contrast: muted token meets AA 4.5:1 on every dark surface");
            Check(WcagContrast.Ratio("#89868A", "#232326") < WcagContrast.AaNormalText, "contrast: the old muted value #89868A failed AA on the bar");
            Check(WcagContrast.Ratio("#E8939F", "#232326") >= WcagContrast.AaLargeOrUi, "contrast: focus ring colour meets 3:1 on the bar");
            var badHex = false; try { WcagContrast.Luminance("red"); } catch (FormatException) { badHex = true; }
            Check(badHex, "contrast: malformed colour rejected");
        }


        {
            // Clone must carry every stored setting (Settings starts from it so unedited settings are never reset).
            var src = new Preferences();
            foreach (var prop in typeof(Preferences).GetProperties())
            {
                if (!prop.CanWrite || !prop.CanRead) continue;
                object? v = prop.PropertyType == typeof(bool) ? !(bool)prop.GetValue(src)!
                          : prop.PropertyType == typeof(int) ? (object)((int)prop.GetValue(src)! + 7)
                          : prop.PropertyType == typeof(double) ? (object)0.5
                          : prop.PropertyType == typeof(string) ? "x" + prop.Name
                          : prop.PropertyType == typeof(uint) ? (object)(((uint)prop.GetValue(src)!) + 3u)
                          : null;
                if (v is not null) prop.SetValue(src, v);
            }
            var cl = src.Clone();
            var lost = typeof(Preferences).GetProperties().Where(pp => pp.CanWrite && pp.CanRead && !Equals(pp.GetValue(src), pp.GetValue(cl)) && pp.PropertyType.IsValueType | pp.PropertyType == typeof(string)).Select(pp => pp.Name).ToList();
            Check(lost.Count == 0, "preferences clone carries every simple setting" + (lost.Count == 0 ? "" : ": lost " + string.Join(", ", lost)));
            Check(new Preferences().CopyTextAfterCapture == false && new Preferences().RedactAfterCapture == false, "capture-time OCR toggles default to off");
        }

    }
}
