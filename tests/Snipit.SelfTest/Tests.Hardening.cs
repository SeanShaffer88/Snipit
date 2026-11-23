using Snipit.Core;

namespace Snipit.SelfTest;

// Hardening checks. Each file is one area; Program.cs runs them in order.
static partial class Tests
{
    internal static void Hardening()
    {
        Console.WriteLine("Theme contrast (TesselGUI r3 palette, copied from TesselTheme.cs)");
        double Cr(string a, string b) { ContrastRatio.TryParse(a, out var ca); ContrastRatio.TryParse(b, out var cb); return ContrastRatio.Between(ca, cb); }
        Check(Math.Abs(Cr("#000000", "#FFFFFF") - 21.0) < 0.01, "contrast formula: black/white = 21");
        foreach (var surface in new[] { "#17171A", "#232326", "#1F1F22" })
        {
            Check(Cr("#E6E3E1", surface) >= 4.5, $"foreground text on {surface} >= 4.5");
            Check(Cr("#A3A0A4", surface) >= 4.5, $"muted text on {surface} >= 4.5");
            Check(Cr("#E8939F", surface) >= 3.0, $"focus ring (#E8939F) on {surface} >= 3");
        }
        Check(Cr("#E8939F", "#960018") >= 3.0, "focus ring on action button fill >= 3");
        Check(Cr("#FFFFFF", "#960018") >= 4.5 && Cr("#FFFFFF", "#7E0014") >= 4.5, "white text on action fills >= 4.5");
        Check(Cr("#960018", "#1B1B1D") < 3.0, "old focus colour #960018 stays below 3:1 (why it was replaced)");

        Console.WriteLine("Hardening: CLI args, workflow quoting and trust, log policy");
        var cargs = new[] { "--out", "--clean", "--fullscreen" };
        Check(!CliArgs.Has(cargs, "--clean") && CliArgs.Has(cargs, "--fullscreen") && CliArgs.Value(cargs, "--out") is null, "flag in a value slot is not a flag");
        Check(CliArgs.Value(new[] { "--out", @"C:\a b\x.png" }, "--out") == @"C:\a b\x.png" && CliArgs.Value(new[] { "--out" }, "--out") is null, "value lookup");
        Check(CliArgs.Has(new[] { "--FULLSCREEN" }, "--fullscreen") && CliArgs.TakesValue("--record") && !CliArgs.TakesValue("--all"), "case-insensitive; value flags known");
        string sep = Path.DirectorySeparatorChar.ToString(), spDir = sep + "My Shots", spFile = spDir + sep + "a.png", plainFile = sep + "s" + sep + "a.png";
        Check(WorkflowPlan.ExpandArgs("-i %path% -o %dir%" + sep + "out.png", spFile) == "-i \"" + spFile + "\" -o \"" + spDir + "\"" + sep + "out.png", "unquoted placeholders get quoted");
        Check(WorkflowPlan.ExpandArgs("\"%path%\"", spFile) == "\"" + spFile + "\"", "already-quoted placeholders stay raw");
        Check(WorkflowPlan.ExpandArgs("%name%.%ext%", plainFile) == "a.png" && WorkflowPlan.ExpandArgs("%unknown%", plainFile) == "%unknown%", "plain values unquoted; unknown placeholder untouched");
        Check(WorkflowPlan.QuoteArg(@"C:\dir with space\") == "\"C:\\dir with space\\\\\"", "trailing backslash doubled inside quotes");
        var tp = new Preferences { ExternalEditorPath = @"C:\e.exe", Workflows = new() { new WorkflowDef { Name = "w", Steps = new() { "run: x.exe|%path%" } } } };
        var tkey = new byte[32]; for (int i = 0; i < 32; i++) tkey[i] = (byte)(i + 1);
        var tkey2 = new byte[32]; for (int i = 0; i < 32; i++) tkey2[i] = (byte)(100 + i);
        Check(WorkflowTrust.IsTampered(tp, tkey), "blank trust hash with an editor and a run step is NOT trusted");
        Check(!WorkflowTrust.IsTampered(new Preferences(), tkey), "blank trust hash with nothing to launch is fine");
        Check(!WorkflowTrust.IsTampered(new Preferences { Workflows = new() { new WorkflowDef { Name = "c", Steps = new() { "copy: D:\\x", "effect: Grayscale" } } } }, tkey), "blank hash with only non-launching steps is fine");
        WorkflowTrust.Approve(tp, tkey);
        Check(!WorkflowTrust.IsTampered(tp, tkey), "approved settings are trusted");
        Check(WorkflowTrust.IsTampered(tp, tkey2), "a hash made with another key (another PC or planted file) is rejected");
        tp.Workflows[0].Steps[0] = "run: evil.exe|%path%";
        Check(WorkflowTrust.IsTampered(tp, tkey), "changed run step is detected");
        WorkflowTrust.Approve(tp, tkey); tp.ExternalEditorPath = @"C:\other.exe";
        Check(WorkflowTrust.IsTampered(tp, tkey), "changed editor path is detected");
        WorkflowTrust.Approve(tp, tkey); tp.ImageFormat = "jpg"; tp.IntervalSeconds = 9;
        Check(!WorkflowTrust.IsTampered(tp, tkey), "unrelated settings do not trip the check");
        var legacyTrust = new Preferences { ExternalEditorPath = @"C:\e.exe", WorkflowTrust = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1 })) };
        Check(WorkflowTrust.IsTampered(legacyTrust, tkey), "an old unkeyed hash no longer validates (one re-approval after upgrade)");
        var noKeyThrown = false; try { WorkflowTrust.Compute(tp, Array.Empty<byte>()); } catch (ArgumentException) { noKeyThrown = true; }
        Check(noKeyThrown, "an empty key is rejected");
        Check(WorkflowTrust.Launches("run") && WorkflowTrust.Launches("editor") && !WorkflowTrust.Launches("copy"), "which steps launch programs");
        Check(!LogPolicy.ShouldRoll(LogPolicy.MaxFileBytes - 1) && LogPolicy.ShouldRoll(LogPolicy.MaxFileBytes), "log roll threshold");
        var tj = System.Text.Json.JsonSerializer.Serialize(new Preferences { WorkflowsLocked = true });
        Check(!tj.Contains("WorkflowsLocked") && tj.Contains("WorkflowTrust"), "lock flag is never written to settings");

        Console.WriteLine("v0.12: colour formats");
        Check(ColorFormat.Format("HEX", 255, 128, 0) == "FF8000" && ColorFormat.Format("RGB", 255, 128, 0) == "rgb(255, 128, 0)" && ColorFormat.Format("HTML", 255, 128, 0) == "#FF8000", "colour hex/rgb/html");
        Check(ColorFormat.Format("C++", 0x11, 0x22, 0x33) == "0x332211" && ColorFormat.Format("Delphi", 0x11, 0x22, 0x33) == "$00332211", "colour C++/Delphi BGR order");
        Check(ColorFormat.Next("HEX") == "RGB" && ColorFormat.Next("Delphi") == "HEX" && ColorFormat.Next("bogus") == "RGB" && ColorFormat.Sanitize("x") == "HEX", "format cycling and sanitising");
        Check(ColorFormat.TryParseHex("#fa0", out var hc2) && hc2 == ((byte)0xFF, (byte)0xAA, (byte)0x00) && !ColorFormat.TryParseHex("zz", out _), "hex parse (short form)");
        var gr2 = ColorFormat.Gradient((0, 0, 0), (255, 255, 255), 3);
        Check(gr2.Length == 3 && gr2[1] == ((byte)128, (byte)128, (byte)128) && ColorFormat.Gradient((0, 0, 0), (1, 1, 1), 1).Length == 2, "gradient");
        Check(new Preferences { ColorFormatName = "nope" }.Sanitize().ColorFormatName == "HEX", "prefs sanitise colour format");

    }
}
