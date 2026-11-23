using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Snipit;

internal enum ImageClipboardResult { Failed, ImageOnly, ImageAndJpeg }

internal static class ClipboardWriter
{
    /// <summary>Put plain text on the clipboard (retrying while another process holds it). Returns false if it stayed locked.</summary>
    public static bool SetText(string text)
    {
        try
        {
            ClipboardJpeg.PrepareForWrite();
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetDataObject(text, true); return true; }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { Thread.Sleep(15); }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard text write failed", ex);
            return false;
        }
    }

    /// <summary>Copies image formats and a JPEG file drop when staging succeeds. <paramref name="text"/> is laid onto the
    /// same clipboard as a plain-text format (e.g. recognised capture text), so it never replaces the image.</summary>
    public static ImageClipboardResult SetImage(BitmapSource image, string? text = null)
    {
        try
        {
            ClipboardJpeg.PrepareForWrite();
            using var png = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            enc.Save(png);
            png.Position = 0;

            var data = new DataObject();
            data.SetImage(image);          // CF_DIB / CF_BITMAP
            data.SetData("PNG", png);      // lossless, keeps transparency for apps that understand it
            if (!string.IsNullOrWhiteSpace(text)) data.SetText(text.Trim());
            try
            {
                string path = ClipboardJpeg.Stage(image);
                data.SetFileDropList(new StringCollection { path });
                using var effect = new MemoryStream(new byte[] { 1, 0, 0, 0 }); // DROPEFFECT_COPY, DWORD
                data.SetData("Preferred DropEffect", effect, autoConvert: false);
                Publish(data);
                return ImageClipboardResult.ImageAndJpeg;
            }
            catch (Exception ex)
            {
                Log.Error("Could not publish a clipboard JPEG file. Falling back to image paste", ex);
            }

            var imageOnly = new DataObject();
            imageOnly.SetImage(image);
            png.Position = 0;
            imageOnly.SetData("PNG", png);
            if (!string.IsNullOrWhiteSpace(text)) imageOnly.SetText(text.Trim());
            Publish(imageOnly);
            return ImageClipboardResult.ImageOnly;
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard write failed", ex);
            return ImageClipboardResult.Failed;
        }
    }

    private static void Publish(DataObject data)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch (COMException) when (attempt < 4) { Thread.Sleep(15); }
        }
    }
}
