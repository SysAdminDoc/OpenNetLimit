using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenNetLimit.MarketingHero;

internal static class Program
{
    private const int HeroWidth = 1280;
    private const int HeroHeight = 640;

    private static readonly string[] VisibleCopy =
    [
        "Open source network control",
        "OpenNetLimit",
        "Bandwidth control without the guesswork.",
        "See the traffic. Set the limit.",
        "Live traffic",
        "Per-app limits",
        "Usage history",
        "Local automation",
        "Windows 10 and 11",
        "Local-first controls",
        "Open source for Windows",
        "Know what uses your connection.",
        "Control what it can use.",
        "Per-app bandwidth limits, useful history, and local automation.",
        "No account"
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            var repoRoot = Path.GetFullPath(options.GetValueOrDefault("repo", FindRepoRoot()));
            var archiveRoot = Path.GetFullPath(options.GetValueOrDefault(
                "archive",
                Path.Combine(repoRoot, "assets", "concepts", "2026-09-12-readme-hero")));
            var selected = options.GetValueOrDefault("select", "none");
            if (selected is not ("none" or "1" or "2"))
                throw new ArgumentException("--select must be none, 1, or 2.");

            Directory.CreateDirectory(archiveRoot);
            Directory.CreateDirectory(Path.Combine(archiveRoot, "review"));

            var logoPath = Path.Combine(repoRoot, "assets", "brand", "opennetlimit-selected-master.png");
            var screenshotPath = Path.Combine(repoRoot, "assets", "screenshots", "02-live-traffic.png");
            var referencePath = Path.Combine(archiveRoot, "source", "previous-social-preview.png");
            RequireFile(logoPath);
            RequireFile(screenshotPath);
            RequireFile(referencePath);
            RejectReleaseNumbers(VisibleCopy);

            using var logo = Image.FromFile(logoPath);
            using var screenshot = Image.FromFile(screenshotPath);
            using var reference = Image.FromFile(referencePath);
            using var candidateOne = RenderContinuityHero(logo, screenshot);
            using var candidateTwo = RenderProductLedHero(logo, screenshot);

            var candidateOnePath = Path.Combine(archiveRoot, "readme-hero-candidate-01-continuity.png");
            var candidateTwoPath = Path.Combine(archiveRoot, "readme-hero-candidate-02-product-led.png");
            SavePng(candidateOne, candidateOnePath);
            SavePng(candidateTwo, candidateTwoPath);

            SaveScaled(candidateOne, Path.Combine(archiveRoot, "review", "candidate-01-960.png"), 960, 480);
            SaveScaled(candidateOne, Path.Combine(archiveRoot, "review", "candidate-01-640.png"), 640, 320);
            SaveScaled(candidateTwo, Path.Combine(archiveRoot, "review", "candidate-02-960.png"), 960, 480);
            SaveScaled(candidateTwo, Path.Combine(archiveRoot, "review", "candidate-02-640.png"), 640, 320);
            SaveComparison(candidateOne, candidateTwo, Path.Combine(archiveRoot, "review", "candidate-comparison.png"));
            SaveReferenceComparison(reference, candidateOne, candidateTwo, Path.Combine(archiveRoot, "review", "reference-and-candidates.png"));

            string? finalPath = null;
            if (selected != "none")
            {
                var chosen = selected == "1" ? candidateOne : candidateTwo;
                finalPath = Path.Combine(archiveRoot, "readme-hero-final.png");
                SavePng(chosen, finalPath);
                SavePng(chosen, Path.Combine(repoRoot, "assets", "marketing", "readme-hero.png"));
                SavePng(chosen, Path.Combine(repoRoot, "assets", "marketing", "social-preview.png"));
                File.WriteAllLines(Path.Combine(repoRoot, "assets", "marketing", "readme-hero-copy.txt"), VisibleCopy);
                SaveScaled(chosen, Path.Combine(archiveRoot, "review", "selected-960.png"), 960, 480);
                SaveScaled(chosen, Path.Combine(archiveRoot, "review", "selected-640.png"), 640, 320);
            }

            var outputFiles = Directory.EnumerateFiles(archiveRoot, "*.png", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new
                {
                    file = Path.GetRelativePath(archiveRoot, path).Replace('\\', '/'),
                    sha256 = Sha256(path),
                    bytes = new FileInfo(path).Length
                })
                .ToArray();
            var report = new
            {
                width = HeroWidth,
                height = HeroHeight,
                selectedCandidate = selected == "none" ? null : $"candidate-{selected}",
                containsReleaseNumber = false,
                sources = new
                {
                    logo = Path.GetRelativePath(repoRoot, logoPath).Replace('\\', '/'),
                    screenshot = Path.GetRelativePath(repoRoot, screenshotPath).Replace('\\', '/')
                },
                final = finalPath is null ? null : new
                {
                    file = Path.GetRelativePath(archiveRoot, finalPath).Replace('\\', '/'),
                    sha256 = Sha256(finalPath)
                },
                files = outputFiles
            };
            File.WriteAllText(
                Path.Combine(archiveRoot, "render-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Bitmap RenderContinuityHero(Image logo, Image screenshot)
    {
        var bitmap = NewCanvas();
        using var graphics = Prepare(bitmap);
        graphics.Clear(Color.FromArgb(6, 17, 31));

        using (var field = new SolidBrush(Color.FromArgb(10, 36, 58)))
            graphics.FillEllipse(field, -310, -260, 735, 795);
        using (var glow = new SolidBrush(Color.FromArgb(18, 12, 199, 238)))
            graphics.FillEllipse(glow, 810, -360, 620, 560);

        graphics.DrawImage(logo, new Rectangle(58, 65, 82, 82));
        DrawText(graphics, "OPEN SOURCE NETWORK CONTROL", 58, 190, 350, 28, 17, FontStyle.Bold, Color.FromArgb(38, 208, 247));
        DrawText(graphics, "OpenNetLimit", 58, 232, 355, 62, 46, FontStyle.Bold, Color.FromArgb(247, 250, 255));
        DrawText(graphics, "Bandwidth control\nwithout the guesswork.", 58, 316, 365, 100, 34, FontStyle.Bold, Color.FromArgb(245, 248, 253));
        DrawText(graphics, "See the traffic. Set the limit.", 58, 432, 340, 34, 20, FontStyle.Regular, Color.FromArgb(172, 190, 212));
        DrawText(graphics, "LIVE TRAFFIC  •  PER-APP LIMITS", 58, 492, 360, 28, 15, FontStyle.Bold, Color.FromArgb(66, 232, 175));
        DrawText(graphics, "USAGE HISTORY  •  LOCAL AUTOMATION", 58, 525, 375, 28, 15, FontStyle.Bold, Color.FromArgb(38, 208, 247));
        DrawText(graphics, "Windows 10 and 11  •  Local-first controls", 58, 579, 380, 28, 16, FontStyle.Regular, Color.FromArgb(133, 154, 181));

        DrawProductFrame(graphics, screenshot, new Rectangle(449, 55, 796, 546), new Rectangle(469, 78, 752, 510));
        return bitmap;
    }

    private static Bitmap RenderProductLedHero(Image logo, Image screenshot)
    {
        var bitmap = NewCanvas();
        using var graphics = Prepare(bitmap);
        graphics.Clear(Color.FromArgb(5, 15, 29));

        using (var halo = new SolidBrush(Color.FromArgb(28, 0, 167, 226)))
            graphics.FillEllipse(halo, -225, -420, 850, 850);
        using (var lowerGlow = new SolidBrush(Color.FromArgb(18, 40, 229, 166)))
            graphics.FillEllipse(lowerGlow, 150, 465, 640, 470);

        graphics.DrawImage(logo, new Rectangle(59, 55, 72, 72));
        DrawText(graphics, "OPEN SOURCE FOR WINDOWS", 151, 61, 300, 25, 16, FontStyle.Bold, Color.FromArgb(41, 209, 246));
        DrawText(graphics, "OpenNetLimit", 151, 88, 300, 40, 28, FontStyle.Bold, Color.FromArgb(247, 250, 255));

        DrawText(graphics, "Know what uses", 59, 172, 385, 53, 39, FontStyle.Bold, Color.FromArgb(247, 250, 255));
        DrawText(graphics, "your connection.", 59, 218, 390, 53, 39, FontStyle.Bold, Color.FromArgb(247, 250, 255));
        DrawText(graphics, "Control what it can use.", 59, 276, 390, 48, 31, FontStyle.Bold, Color.FromArgb(65, 231, 175));
        DrawText(graphics, "Per-app bandwidth limits, useful history,\nand local automation.", 59, 338, 380, 66, 19, FontStyle.Regular, Color.FromArgb(173, 193, 216));

        DrawPill(graphics, "LIVE TRAFFIC", new Rectangle(59, 433, 150, 38), Color.FromArgb(19, 56, 78), Color.FromArgb(63, 215, 248));
        DrawPill(graphics, "PER-APP LIMITS", new Rectangle(219, 433, 174, 38), Color.FromArgb(15, 62, 60), Color.FromArgb(72, 230, 177));
        DrawPill(graphics, "USAGE HISTORY", new Rectangle(59, 483, 164, 38), Color.FromArgb(19, 56, 78), Color.FromArgb(63, 215, 248));
        DrawPill(graphics, "NO ACCOUNT", new Rectangle(233, 483, 160, 38), Color.FromArgb(15, 62, 60), Color.FromArgb(72, 230, 177));
        DrawText(graphics, "Windows 10 and 11  •  Local-first controls", 59, 572, 370, 27, 16, FontStyle.Regular, Color.FromArgb(135, 157, 184));

        DrawProductFrame(graphics, screenshot, new Rectangle(476, 53, 770, 536), new Rectangle(496, 75, 726, 493));
        return bitmap;
    }

    private static void DrawProductFrame(Graphics graphics, Image screenshot, Rectangle frame, Rectangle screen)
    {
        for (var offset = 16; offset >= 4; offset -= 4)
        {
            var alpha = 7 + (16 - offset);
            using var shadow = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            using var shadowPath = RoundedRectangle(new Rectangle(frame.X - offset / 2, frame.Y + offset / 2, frame.Width + offset, frame.Height + offset), 28);
            graphics.FillPath(shadow, shadowPath);
        }

        using (var frameBrush = new SolidBrush(Color.FromArgb(8, 26, 46)))
        using (var framePath = RoundedRectangle(frame, 28))
            graphics.FillPath(frameBrush, framePath);
        using (var borderPen = new Pen(Color.FromArgb(58, 119, 166), 1.5f))
        using (var framePath = RoundedRectangle(frame, 28))
            graphics.DrawPath(borderPen, framePath);

        var state = graphics.Save();
        using (var clip = RoundedRectangle(screen, 14))
        {
            graphics.SetClip(clip);
            graphics.DrawImage(screenshot, screen);
        }
        graphics.Restore(state);
        using var screenBorder = new Pen(Color.FromArgb(49, 112, 157), 1.2f);
        using var screenPath = RoundedRectangle(screen, 14);
        graphics.DrawPath(screenBorder, screenPath);
    }

    private static void DrawPill(Graphics graphics, string text, Rectangle bounds, Color fill, Color foreground)
    {
        using var path = RoundedRectangle(bounds, 19);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(72, foreground), 1f);
        graphics.DrawPath(pen, path);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var font = Font(13, FontStyle.Bold);
        using var textBrush = new SolidBrush(foreground);
        graphics.DrawString(text, font, textBrush, bounds, format);
    }

    private static void DrawText(Graphics graphics, string text, float x, float y, float width, float height, float size, FontStyle style, Color color)
    {
        using var font = Font(size, style);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit
        };
        graphics.DrawString(text, font, brush, new RectangleF(x, y, width, height), format);
    }

    private static Font Font(float size, FontStyle style) => new("Segoe UI", size, style, GraphicsUnit.Pixel);

    private static Bitmap NewCanvas() => new(HeroWidth, HeroHeight, PixelFormat.Format24bppRgb);

    private static Graphics Prepare(Bitmap bitmap)
    {
        var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        return graphics;
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void SaveScaled(Image source, string path, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Prepare(bitmap);
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        SavePng(bitmap, path);
    }

    private static void SaveComparison(Image candidateOne, Image candidateTwo, string path)
    {
        using var bitmap = new Bitmap(1040, 1196, PixelFormat.Format24bppRgb);
        using var graphics = Prepare(bitmap);
        graphics.Clear(Color.FromArgb(241, 245, 250));
        DrawText(graphics, "OpenNetLimit README hero review", 40, 25, 960, 42, 28, FontStyle.Bold, Color.FromArgb(12, 27, 47));
        DrawText(graphics, "Candidate 01: brand continuity", 40, 83, 960, 30, 19, FontStyle.Bold, Color.FromArgb(35, 72, 104));
        graphics.DrawImage(candidateOne, new Rectangle(40, 122, 960, 480));
        DrawText(graphics, "Candidate 02: product-led clarity", 40, 637, 960, 30, 19, FontStyle.Bold, Color.FromArgb(35, 72, 104));
        graphics.DrawImage(candidateTwo, new Rectangle(40, 676, 960, 480));
        SavePng(bitmap, path);
    }

    private static void SaveReferenceComparison(Image reference, Image candidateOne, Image candidateTwo, string path)
    {
        using var bitmap = new Bitmap(1040, 1750, PixelFormat.Format24bppRgb);
        using var graphics = Prepare(bitmap);
        graphics.Clear(Color.FromArgb(241, 245, 250));
        DrawText(graphics, "OpenNetLimit README hero review", 40, 25, 960, 42, 28, FontStyle.Bold, Color.FromArgb(12, 27, 47));
        DrawText(graphics, "Reference: previous social card", 40, 83, 960, 30, 19, FontStyle.Bold, Color.FromArgb(35, 72, 104));
        graphics.DrawImage(reference, new Rectangle(40, 122, 960, 480));
        DrawText(graphics, "Candidate 01: brand continuity", 40, 637, 960, 30, 19, FontStyle.Bold, Color.FromArgb(35, 72, 104));
        graphics.DrawImage(candidateOne, new Rectangle(40, 676, 960, 480));
        DrawText(graphics, "Candidate 02: product-led clarity", 40, 1191, 960, 30, 19, FontStyle.Bold, Color.FromArgb(35, 72, 104));
        graphics.DrawImage(candidateTwo, new Rectangle(40, 1230, 960, 480));
        SavePng(bitmap, path);
    }

    private static void SavePng(Image image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        image.Save(path, ImageFormat.Png);
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException("Arguments must use --name value pairs.");
            options[args[index][2..]] = args[++index];
        }
        return options;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OpenNetLimit.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Run this tool inside the OpenNetLimit repository or pass --repo.");
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required marketing source was not found.", path);
    }

    private static void RejectReleaseNumbers(IEnumerable<string> copy)
    {
        foreach (var value in copy)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(value, @"\bv?\d+\.\d+\.\d+\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException($"Visible hero copy contains a release number: {value}");
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
