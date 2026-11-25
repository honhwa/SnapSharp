using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;

namespace NimCap.Screenshot.RegressionChecks;

internal static partial class Program
{
    // Captured samples carry independently reviewed offsets; never generate truth from Match().
    private sealed record VerticalSample(string Application, string Version, string Scenario,
        string[] Frames, int[] ExpectedOffsets, bool[] ExpectedReliable, ScrollTrim Trim, string? ExpectedImage);

    private static byte[] VerticalBytes(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }
    private static void ExportVerticalSample(string path, string application, string version, string scenario)
    {
        var project = new ScrollProjectService().Load(path); var session = project.Session;
        Require(session.Direction == ScrollDirection.Vertical && session.Frames.Count >= 2 && session.Offsets.All(o => o != 0),
            "Export a reviewed vertical project with at least two frames and no unresolved offsets.");
        var directory = Path.Combine(Root, "exported-sample"); Directory.CreateDirectory(directory);
        var images = new SkiaImageService();
        var files = Enumerable.Range(0, session.Frames.Count).Select(i => $"frame-{i:D3}.png").ToArray();
        for (var i = 0; i < files.Length; i++) images.Save(session.Frames[i], Path.Combine(directory, files[i]));
        File.WriteAllText(Path.Combine(directory, "sample.json"), JsonSerializer.Serialize(new VerticalSample(application, version, scenario,
            files, session.Offsets.ToArray(), session.Matches.Select(m => m.Reliable).ToArray(), project.FrameTrim, null), new JsonSerializerOptions { WriteIndented = true }));
        ReplayVerticalSamples(directory);
        Console.WriteLine("Review provenance and independent offsets before adding to the sample corpus: " + directory);
    }
    private static void ReplayVerticalSamples(string directory)
    {
        var manifests = Directory.GetFiles(Path.GetFullPath(directory), "*.json");
        Require(manifests.Length > 0, "No reviewed application samples found.");
        var images = new SkiaImageService(); var stitcher = new ScrollingStitcher();
        foreach (var manifest in manifests)
        {
            var artifactName = Path.GetFileName(Path.GetDirectoryName(manifest)) + "-" + Path.GetFileNameWithoutExtension(manifest);
            var sample = JsonSerializer.Deserialize<VerticalSample>(File.ReadAllText(manifest))!;
            Require(!string.IsNullOrWhiteSpace(sample.Application) && !string.IsNullOrWhiteSpace(sample.Version) &&
                !string.IsNullOrWhiteSpace(sample.Scenario) && sample.Frames.Length >= 2 && sample.Frames.Length <= 80 &&
                sample.ExpectedOffsets.Length == sample.Frames.Length - 1 && sample.ExpectedReliable.Length == sample.ExpectedOffsets.Length,
                "Sample provenance or expectations missing: " + manifest);
            BitmapSource Load(string path) => images.Decode(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(manifest)!, path)));
            var frames = sample.Frames.Select(Load).ToArray();
            BitmapSource Content(BitmapSource f) => images.Crop(f, new Int32Rect(sample.Trim.Left, sample.Trim.Top,
                f.PixelWidth - sample.Trim.Left - sample.Trim.Right, f.PixelHeight - sample.Trim.Top - sample.Trim.Bottom));
            var matches = frames.Skip(1).Select((f, i) => stitcher.Match(Content(frames[i]), Content(f), ScrollDirection.Vertical)).ToArray();
            for (var i = 0; i < matches.Length; i++)
                Require(matches[i].Reliable == sample.ExpectedReliable[i] && (!matches[i].Reliable || matches[i].Offset == sample.ExpectedOffsets[i]),
                    $"Application sample mismatch: {sample.Application}/{sample.Scenario}, seam {i}: {matches[i]}");
            var driver = new ScrollDriver(frames);
            var captured = new ScrollingCaptureService(driver).CaptureSessionAsync(
                new System.Drawing.Rectangle(0, 0, frames[0].PixelWidth, frames[0].PixelHeight),
                new ScrollCaptureOptions(ScrollDirection.Vertical, MaximumFrames: 80, MatchingTrim: sample.Trim),
                waitForManualScroll: _ => { if (driver.Index == frames.Length - 1) return Task.FromResult(false); driver.Index++; return Task.FromResult(true); })
                .GetAwaiter().GetResult();
            var firstUncertain = Array.FindIndex(sample.ExpectedReliable, reliable => !reliable);
            var expectedCount = firstUncertain < 0 ? frames.Length : firstUncertain + 2;
            Require(captured.Frames.Count == expectedCount && driver.Ended &&
                (firstUncertain < 0 || captured.Offsets[^1] == 0), "Real-sample capture handoff failed: " + manifest);
            var projects = new ScrollProjectService(); var projectPath = Path.Combine(Root, artifactName + ".ssscroll");
            projects.Save(projectPath, new(captured, sample.Trim, new()));
            Require(projects.Load(projectPath).Session.Frames.Count == expectedCount, "Application originals lost in project.");
            var session = new ScrollCaptureSession(frames, sample.ExpectedOffsets,
                matches.Select((m, i) => m with { Offset = sample.ExpectedOffsets[i], ManuallyAdjusted = !m.Reliable }).ToArray(), ScrollDirection.Vertical, sample.Scenario);
            var output = stitcher.Stitch(session, sample.Trim).Image;
            if (sample.ExpectedImage is not null) AssertVerticalPixels(output, Load(sample.ExpectedImage), sample.Scenario);
            images.Save(output, Path.Combine(Root, artifactName + ".png"));
            Console.WriteLine($"PASS reviewed sample: {sample.Application} {sample.Version}: {sample.Scenario}");
        }
    }
    private static void AssertVerticalPixels(BitmapSource actual, BitmapSource expected, string label) =>
        Require(actual.PixelWidth == expected.PixelWidth && actual.PixelHeight == expected.PixelHeight &&
            VerticalBytes(actual).Zip(VerticalBytes(expected)).All(p => Math.Abs(p.First - p.Second) <= 1), "Vertical output differs: " + label);

    private static void VerticalScenarioChecks()
    {
        var images = new SkiaImageService(); var stitcher = new ScrollingStitcher();
        foreach (var style in new[] { "browser-article", "electron-chat", "office-pages" })
        {
            var visual = new DrawingVisual();
            using (var c = visual.RenderOpen())
            {
                c.DrawRectangle(style == "electron-chat" ? Brushes.Black : Brushes.White, null, new Rect(0, 0, 480, 1800));
                for (var row = 0; row < 72; row++)
                {
                    var ink = style == "electron-chat" ? Brushes.LightGray : Brushes.Black;
                    c.DrawText(new FormattedText($"{style} {row:D3}: {(row * 7919):X} — paragraph {row % 7}",
                        System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, ink, 1), new Point(16, row * 24 + 3));
                    if (style == "office-pages" && row % 12 == 0) c.DrawRectangle(Brushes.Silver, null, new Rect(0, row * 24, 480, 2));
                }
            }
            var document = new RenderTargetBitmap(480, 1800, 96, 96, PixelFormats.Pbgra32); document.Render(visual); document.Freeze();
            var positions = new[] { 120, 193, 266, 193, 340 };
            var frames = positions.Select(p => images.Crop(document, new Int32Rect(0, p, 480, 320))).ToArray();
            var matches = frames.Skip(1).Select((f, i) => stitcher.Match(frames[i], f, ScrollDirection.Vertical)).ToArray();
            Require(matches.All(m => m.Reliable) && matches.Select(m => m.Offset).SequenceEqual(positions.Skip(1).Select((p, i) => p - positions[i])), style + " offsets failed");
            var session = new ScrollCaptureSession(frames, matches.Select(m => m.Offset).ToArray(), matches, ScrollDirection.Vertical, style);
            AssertVerticalPixels(stitcher.Stitch(session).Image, images.Crop(document, new Int32Rect(0, 120, 480, 540)), style);
            // A changing fixed banner is excluded from both matching and composition.
            var sticky = frames.Select((f, i) =>
            {
                using var bitmap = images.ToSkBitmap(f); using var canvas = new SkiaSharp.SKCanvas(bitmap);
                using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor((byte)(30 + i * 40), 60, 90) };
                canvas.DrawRect(0, 0, 480, 32, paint); canvas.Flush(); return images.ToBitmapSource(bitmap);
            }).ToArray();
            var trim = new ScrollTrim(Top: 32);
            var stickyMatches = sticky.Skip(1).Select((f, i) => stitcher.Match(images.Crop(sticky[i], new Int32Rect(0, 32, 480, 288)), images.Crop(f, new Int32Rect(0, 32, 480, 288)), ScrollDirection.Vertical)).ToArray();
            Require(stickyMatches.All(m => m.Reliable) && stickyMatches.Select(m => m.Offset).SequenceEqual(session.Offsets), style + " fixed banner failed");
            AssertVerticalPixels(stitcher.Stitch(session with { Frames = sticky, Matches = stickyMatches }, trim).Image,
                images.Crop(document, new Int32Rect(0, 152, 480, 508)), style + " trimmed");
            // A full viewport repaint (navigation, reflow or virtual-list replacement)
            // must not silently certify unrelated rows or lose the new original.
            var random = new Random(91); var replacement = new byte[480 * 320 * 4]; random.NextBytes(replacement);
            for (var p = 3; p < replacement.Length; p += 4) replacement[p] = 255;
            var repainted = BitmapSource.Create(480, 320, 96, 96, PixelFormats.Bgra32, null, replacement, 480 * 4); repainted.Freeze();
            var driver = new ScrollDriver([frames[0], repainted]);
            var interrupted = new ScrollingCaptureService(driver).CaptureSessionAsync(new System.Drawing.Rectangle(0, 0, 480, 320),
                new ScrollCaptureOptions(ScrollDirection.Vertical), waitForManualScroll: _ => { driver.Index = 1; return Task.FromResult(false); }).GetAwaiter().GetResult();
            Require(interrupted.Frames.Count == 2 && interrupted.Offsets[0] == 0 && !interrupted.Matches[0].Reliable,
                style + " dynamic replacement did not preserve the uncertain original");
            AssertVerticalPixels(stitcher.Stitch(interrupted).Image, frames[0], style + " dynamic trusted output");
        }
        Console.WriteLine("PASS: generated vertical browser/article, Electron/chat, Office/page scenarios and changing fixed banners. These are not real-application acceptance.");
    }

    private static void CaptureBrowserSample(string browser)
    {
        browser = Path.GetFullPath(browser);
        Require(File.Exists(browser), "Browser executable missing.");
        var directory = Path.Combine(Root, "browser-sample"); Directory.CreateDirectory(directory);
        var html = Path.Combine(directory, "article.html");
        File.WriteAllText(html, """
            <!doctype html><meta charset="utf-8"><style>
            *{box-sizing:border-box}html,body{overflow:hidden}body{margin:0;background:#fff;color:#222;font:16px Arial}
            header{position:fixed;top:0;left:0;width:100%;height:40px;background:#225588;color:white;z-index:3}
            article{padding:50px 30px;width:720px}p{height:70px;margin:0;border-bottom:1px solid #ccc}
            </style><header>Fixed navigation — scrolling compatibility fixture</header><article id="article"></article>
            <script>
            for(let i=0;i<80;i++){let p=document.createElement('p');p.textContent=`Section ${i}: value ${i*7919}. Browser-rendered paragraphs and unique markers.`;article.append(p)}
            article.style.transform=`translateY(-${Number(location.hash.slice(1))||0}px)`;
            </script>
            """);
        var files = new List<string>();
        foreach (var position in new[] { 0, 150, 300 })
        {
            var screenshot = Path.GetFullPath(Path.Combine(directory, $"frame-{position}.png"));
            var start = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
            foreach (var arg in new[] { "--headless", "--disable-gpu", "--no-first-run", "--hide-scrollbars", "--window-size=800,600", "--run-all-compositor-stages-before-draw",
                "--virtual-time-budget=1000", "--user-data-dir=" + Path.GetFullPath(Path.Combine(directory, "profile")),
                "--screenshot=" + screenshot, new Uri(html).AbsoluteUri + "#" + position }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Browser screenshot exceeded 30 seconds."); }
            Require(process.ExitCode == 0 && File.Exists(screenshot), $"Browser screenshot failed (exit {process.ExitCode}): {error.GetAwaiter().GetResult()}"); files.Add(Path.GetFileName(screenshot));
        }
        var version = FileVersionInfo.GetVersionInfo(browser).FileVersion ?? "unknown";
        File.WriteAllText(Path.Combine(directory, "browser.json"), JsonSerializer.Serialize(new VerticalSample(
            Path.GetFileName(browser), version, "Browser-rendered translated viewports; fixed header excluded; not real wheel/scroll acceptance",
            files.ToArray(), [150, 150], [true, true], new ScrollTrim(Top: 40), null), new JsonSerializerOptions { WriteIndented = true }));
        ReplayVerticalSamples(directory);
        Console.WriteLine("Browser samples: " + directory);
    }
}
