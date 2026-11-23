using Snipit.Core;

namespace Snipit.SelfTest;

// Imaging checks. Each file is one area; Program.cs runs them in order.
static partial class Tests
{
    internal static void Imaging()
    {
        Console.WriteLine("Redactor");
        {
            OcrWord W(string t, int x, int y, int w = 30, int h = 10) => new(t, new RectPx(x, y, w, h));
            var hits = Redactor.Find(new[] { W("Contact", 0, 0, 45), W("john@example.com", 50, 0, 110), W("today", 170, 0, 35) });
            Check(hits.Count == 1 && hits[0].Kind == "email" && hits[0].Rect == new RectPx(49, -1, 112, 12), "redact: email in a line -> its word box, inflated 1px");
            hits = Redactor.Find(new[] { W("john.doe", 0, 0, 55), W("@example.com", 60, 0, 80) });
            Check(hits.Count == 1 && hits[0].Rect == new RectPx(-1, -1, 142, 12), "redact: email split across words -> union box");
            hits = Redactor.Find(new[] { W("see", 0, 0, 20), W("https://example.com/x.", 25, 0, 130) });
            Check(hits.Count == 1 && hits[0].Kind == "url", "redact: url detected (trailing punctuation tolerated)");
            hits = Redactor.Find(new[] { W("4242", 0, 0, 30), W("4242", 35, 0, 30), W("4242", 70, 0, 30), W("4242", 105, 0, 30) });
            Check(hits.Count == 1 && hits[0].Kind == "card" && hits[0].Rect == new RectPx(-1, -1, 137, 12), "redact: luhn-valid card across four words -> one union box");
            Check(Redactor.Find(new[] { W("4242", 0, 0, 30), W("4242", 35, 0, 30), W("4242", 70, 0, 30), W("4241", 105, 0, 30) }).Count == 0, "redact: luhn-invalid number is not a card");
            Check(Redactor.Find(new[] { W("10.0.0.25", 0, 0, 60) }).Count == 1, "redact: ipv4 detected");
            Check(Redactor.Find(new[] { W("999.1.1.1", 0, 0, 60) }).Count == 0, "redact: out-of-range ipv4 rejected");
            hits = Redactor.Find(new[] { W("(555)", 0, 0, 35), W("123-4567", 40, 0, 60) });
            Check(hits.Count == 1 && hits[0].Kind == "phone" && hits[0].Rect == new RectPx(-1, -1, 102, 12), "redact: phone across words -> union box");
            Check(Redactor.Find(new[] { W("sk-9f8e7d6c5b4a39281706fedcba", 0, 0, 200) }).Count == 1, "redact: long mixed token (api-key shape) detected");
            Check(Redactor.Find(new[] { W("internationalisation", 0, 0, 130) }).Count == 0, "redact: a long plain word is not a secret");
            Check(Redactor.Find(new[] { W("The", 0, 0, 20), W("quick", 25, 0, 35), W("brown", 65, 0, 35), W("fox", 105, 0, 20) }).Count == 0, "redact: ordinary sentence -> no hits");
            hits = Redactor.Find(new[]
            {
                W("mail", 0, 0, 30), W("john@example.com", 35, 0, 110),
                W("or", 0, 30, 15), W("https://example.com/?u=john@example.com", 20, 30, 260),
            });
            Check(hits.Count == 2 && hits[0].Kind == "email" && hits[1].Kind == "url", "redact: email and url on separate lines, no double-claim inside the url");
            Check(Redactor.Find(Array.Empty<OcrWord>()).Count == 0, "redact: no words -> no hits");
        }

        Console.WriteLine("PixelAdjust");
        {
            bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
            Check(Throws(() => PixelAdjust.Grayscale(new byte[3])), "pixel ops: partial pixel rejected");
            Check(Throws(() => PixelAdjust.Border(new byte[0], 0, 0, 0, 0, 0, 0)), "frame ops: zero width rejected");
            Check(Throws(() => PixelAdjust.Border(new byte[4], 4, 4, 1, 0, 0, 0)), "frame ops: buffer/size mismatch rejected");

            var grey = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 200 };
            PixelAdjust.Grayscale(grey);
            Check(grey[0] == 76 && grey[4] == 150 && grey[8] == 29 && grey[12] == 255, "grayscale: Rec.601 luma for red/green/blue/white");
            Check(grey[0] == grey[1] && grey[1] == grey[2] && grey[15] == 200, "grayscale: channels equalised, alpha untouched");
        }

        Console.WriteLine("Redaction");
        {
            // 4x2 image, every pixel greyish with alpha 255. Pixel (x,y) sits at ((y * 4 + x) * 4) bytes.
            var px = new byte[4 * 2 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 9; px[i + 1] = 45; px[i + 2] = 81; px[i + 3] = 255; }
            Redaction.FillRectangles(px, 4, 2, new[] { new RectPx(1, 0, 2, 1) });
            // The hit box (x 1..2, y 0) is solid opaque black.
            Check(px[4] == 0 && px[5] == 0 && px[6] == 0 && px[7] == 255, "redact: hit rectangle pixels are opaque black");
            Check(px[8] == 0 && px[9] == 0 && px[10] == 0 && px[11] == 255, "redact: hit rectangle pixels are opaque black (right half)");
            // Untouched pixels keep their colour: (0,0), (3,0), (0,1), (1,1), (3,1).
            Check(px[0] == 9 && px[3] == 255 && px[12] == 9 && px[16] == 9 && px[20] == 9 && px[28] == 9 && px[31] == 255, "redact: untouched pixels keep their colour");
            // Clipping: a part-off-frame rectangle only fills what is in frame; an outside one is ignored.
            var px2 = new byte[4 * 2 * 4];
            for (int i = 0; i < px2.Length; i += 4) { px2[i] = 7; px2[i + 3] = 255; }
            Redaction.FillRectangles(px2, 4, 2, new[] { new RectPx(-2, -2, 4, 4), new RectPx(99, 99, 5, 5) });
            int filled = 0;
            for (int i = 0; i < px2.Length; i += 4) if (px2[i] == 0) filled++;
            Check(filled == 4, "redact: partly-off rectangles are clipped and outside rectangles are ignored");
            bool shortBuf = false;
            try { Redaction.FillRectangles(new byte[8], 4, 1, new[] { RectPx.Empty }); } catch (ArgumentException) { shortBuf = true; }
            Check(shortBuf, "redact: short buffer refused");
        }

    }
}
