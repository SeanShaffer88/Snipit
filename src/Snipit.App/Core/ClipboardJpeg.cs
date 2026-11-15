using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Snipit.Core;

namespace Snipit;

/// <summary>Stages file-drop JPEGs under the selected data directory and prunes them on clipboard writes.</summary>
internal static class ClipboardJpeg
{
    private const string Prefix = "Snipit-";
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";
    private const string RetiredSuffix = ".retired.tmp";
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static string DirectoryPath => Path.Combine(AppPaths.DataDir, "clipboard");

    public static string Stage(BitmapSource image)
    {
        string directory = DirectoryPath;
        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"{Prefix}{stamp}-{Guid.NewGuid():N}.jpg");
        return Exporter.Save(image, new Preferences { ImageFormat = "jpg" }, path);
    }

    public static void PrepareForWrite()
    {
        try
        {
            string directory = DirectoryPath;
            if (!Directory.Exists(directory)) return;
            var clipboard = Clipboard.GetDataObject();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (clipboard?.GetDataPresent(DataFormats.FileDrop, autoConvert: false) == true)
            {
                if (clipboard.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] current) return;
                foreach (string path in current) keep.Add(Path.GetFullPath(path));
            }
            DateTime now = DateTime.UtcNow;
            foreach (string file in Directory.EnumerateFiles(directory, Prefix + "*"))
            {
                string name = Path.GetFileName(file);
                int stampStart = Prefix.Length, guidStart = stampStart + TimestampFormat.Length + 1;
                int suffixStart = guidStart + 32;
                if (name.Length < suffixStart || name[guidStart - 1] != '-' ||
                    !DateTime.TryParseExact(name.AsSpan(stampStart, TimestampFormat.Length), TimestampFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                    !Guid.TryParseExact(name.AsSpan(guidStart, 32), "N", out _)) continue;
                string suffix = name[suffixStart..];
                if (suffix == ".jpg")
                {
                    string retired = file + RetiredSuffix;
                    if (keep.Contains(Path.GetFullPath(file))) File.Delete(retired);
                    else if (!File.Exists(retired))
                    {
                        // The external clipboard may have changed at any point since our last write.
                        // Start the grace period when we first see that the JPEG is unreferenced.
                        using var marker = new FileStream(retired, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                    }
                    else if (now - File.GetLastWriteTimeUtc(retired) >= Retention)
                    {
                        File.Delete(file);
                        File.Delete(retired);
                    }
                }
                else if (suffix == ".jpg.tmp" && now - File.GetLastWriteTimeUtc(file) >= Retention)
                    File.Delete(file);
                else if (suffix == ".jpg" + RetiredSuffix && !File.Exists(file[..^RetiredSuffix.Length]))
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Skipped clipboard JPEG cleanup: " + ex.Message);
        }
    }
}
