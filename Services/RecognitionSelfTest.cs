using NimCap.Screenshot.Models;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ZXing;
using ZXing.Common;

namespace NimCap.Screenshot.Services;

internal static class RecognitionSelfTest
{
    internal static string TestCaptureQr()
    {
        const string website = "https://example.com/path?q=nimcap#test";
        const string plainText = "NimCap screenshot test";
        var recognition = new LocalRecognitionService();
        string? Decode(string content, BarcodeFormat format)
        {
            var barcode = new BarcodeWriterPixelData { Format = format, Options = new EncodingOptions { Width = 400, Height = 300 } }.Write(content);
            var bitmap = BitmapSource.Create(barcode.Width, barcode.Height, 96, 96, PixelFormats.Bgra32, null, barcode.Pixels, barcode.Width * 4);
            bitmap.Freeze();
            return recognition.ReadQrCode(bitmap, true).GetAwaiter().GetResult();
        }

        Require(Decode(website, BarcodeFormat.QR_CODE) == website, "Screenshot QR URL decode failed.");
        Require(Decode(plainText, BarcodeFormat.QR_CODE) == plainText, "Screenshot QR text decode failed.");
        Require(Decode(plainText, BarcodeFormat.CODE_128) is null, "A regular barcode triggered screenshot QR handling.");
        foreach (var text in new[] { plainText, "file:///C:/Windows", "myapp://open", "https://", "https://example.com and more" })
            Require(CaptureQrActions.WebUrl(text) is null, "Non-web QR payload offered to browser.");
        Require(CaptureQrActions.WebUrl("http://example.com/") is not null, "HTTP website rejected.");

        var steps = new List<string>();
        void Verify(string text, bool allow, bool isWebsite, string[] expected)
        {
            steps.Clear();
            var result = CaptureQrActions.Handle(text, url => { steps.Add("prompt:" + url.AbsoluteUri); return allow; },
                copied => steps.Add("copy:" + copied), () => steps.Add("close"),
                url => steps.Add("open:" + url.AbsoluteUri), _ => steps.Add("status"));
            Require(result == (isWebsite, true) && steps.SequenceEqual(expected), "Unexpected screenshot QR action sequence: " + string.Join(", ", steps));
        }
        Verify(website, true, true, ["prompt:" + website, "copy:" + website, "close", "open:" + website]);
        Verify(website, false, true, ["prompt:" + website, "copy:" + website, "status"]);
        Verify(plainText, false, false, ["copy:" + plainText, "status"]);
        return "PASS: QR text and web URL decoding, non-QR rejection, confirmation closes capture before opening, decline copies without closing.";
    }

    internal static string TestSpecialistModels(string? source, string device)
    {
        var root = Path.Combine(AppPaths.Root, "models");
        var progress = new LocalTranslationSetupService.InlineProgress(value => File.WriteAllText(Path.Combine(AppPaths.DiagnosticsRoot, "specialist-model-test.txt"), value));
        var tablePath = Task.Run(() => SpecialistModelDownloadService.Install(root, true, progress, default, source)).GetAwaiter().GetResult();
        var ocr = Directory.GetDirectories(root, "ppocr-v6-small-*").FirstOrDefault(d => File.Exists(Path.Combine(d, "dictionary.txt")))
            ?? Directory.GetDirectories(root, "ppocr-v4-zh-en-*").FirstOrDefault(d => File.Exists(Path.Combine(d, "dictionary.txt")))
            ?? throw new InvalidOperationException("先配置推荐 PaddleOCR 模型再运行专项自检。");
        var settings = new AppSettings { TableStructureModel = tablePath, OcrDetectionModel = Path.Combine(ocr, "detection.onnx"),
            OcrRecognitionModel = Path.Combine(ocr, "recognition.onnx"), OcrDictionary = Path.Combine(ocr, "dictionary.txt"), RecognitionTimeoutSeconds = 120, OnnxDevice = device };
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 720, 360));
            var pen = new Pen(Brushes.Black, 2);
            for (var row = 0; row <= 4; row++) draw.DrawLine(pen, new Point(20, 20 + row * 80), new Point(700, 20 + row * 80));
            for (var col = 0; col <= 3; col++) draw.DrawLine(pen, new Point(20 + col * 680d / 3, 20), new Point(20 + col * 680d / 3, 340));
            string[] values = ["Item", "Count", "Price", "Apple", "123", "20", "Pear", "456", "30", "Total", "579", "50"];
            for (var i = 0; i < values.Length; i++) draw.DrawText(new FormattedText(values[i], System.Globalization.CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                new Typeface("Arial"), 28, Brushes.Black, 1), new Point(38 + i % 3 * 680d / 3, 42 + i / 3 * 80));
        }
        var image = new RenderTargetBitmap(720, 360, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
        var service = new LocalRecognitionService();
        var table = service.RecognizeTable(image, settings).GetAwaiter().GetResult();
        var size = RecognitionExport.Validate(table.Cells);
        Require(size.Rows == 4 && size.Columns == 3 && table.Cells.Any(x => x.Text.Contains("123")),
            "Real table recognition failed: " + size + "\n" + System.Text.Json.JsonSerializer.Serialize(table.Cells));
        File.WriteAllText(Path.Combine(AppPaths.DiagnosticsRoot, "specialist-table-result.txt"), "PASS: real SLANet 4x3 + OCR cells\n" + System.Text.Json.JsonSerializer.Serialize(table.Cells));
        var formulaDirectory = Task.Run(() => SpecialistModelDownloadService.Install(root, false, progress, default, source)).GetAwaiter().GetResult();
        settings.FormulaModelDirectory = formulaDirectory;
        BitmapSource formulaImage;
        if (source is not null && File.Exists(Path.Combine(source, "formula-sample.png")))
        {
            using var input = File.OpenRead(Path.Combine(source, "formula-sample.png"));
            formulaImage = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; formulaImage.Freeze();
        }
        else
        {
            var formulaVisual = new DrawingVisual();
            using (var draw = formulaVisual.RenderOpen())
            {
                draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 280, 70));
                draw.DrawText(new FormattedText("x² + y² = 1", System.Globalization.CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                    new Typeface("Cambria Math"), 34, Brushes.Black, 1), new Point(10, 10));
            }
            var generated = new RenderTargetBitmap(280, 70, 96, 96, PixelFormats.Pbgra32); generated.Render(formulaVisual); generated.Freeze(); formulaImage = generated;
        }
        var formula = service.RecognizeFormula(formulaImage, settings).GetAwaiter().GetResult();
        var latex = new string(formula.Text.Where(c => !char.IsWhiteSpace(c) && c is not ('{' or '}')).ToArray());
        Require(latex == "x^2+y^2=1", "Real formula recognition failed: " + formula.Text);
        var additional = new StringBuilder();
        if (source is not null)
        foreach (var name in new[] { "1.png", "5.png", "6.png" })
        {
            var path = Path.Combine(source, "formula-" + name);
            if (!File.Exists(path)) continue;
            using var stream = File.OpenRead(path);
            var sample = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; sample.Freeze();
            var result = service.RecognizeFormula(sample, settings).GetAwaiter().GetResult();
            string[] expected = name switch { "1.png" => ["\\exp", "\\int", "\\sum", "\\frac"], "5.png" => ["\\frac", "\\sqrt", "\\pm"], _ => ["\\frac", "x", "y", "="] };
            Require(expected.All(part => result.Text.Contains(part, StringComparison.Ordinal)), "Formula fixture " + name + " failed: " + result.Text);
            additional.AppendLine().Append(name).Append(": ").Append(result.Text);
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { service.RecognizeFormula(formulaImage, settings, cancellation.Token).GetAwaiter().GetResult(); throw new Exception("Formula cancellation ignored."); } catch (OperationCanceledException) { }
        return "PASS: pinned table/formula sizes + SHA256 + install/load; real " + device + " SLANet 4x3 + OCR cell matching and LaTeX encoder/decoder, cancellation.\n" +
            System.Text.Json.JsonSerializer.Serialize(table.Cells) + "\n" + formula.Text + additional;
    }
    internal static string TestTesseractModels(string? localDirectory)
    {
        var root = Path.Combine(AppPaths.Root, "models");
        var directory = Task.Run(() => localDirectory is null
            ? new TesseractModelDownloadService().Download(root, "chi_sim+eng", new Progress<string>(), default)
            : Task.FromResult(TesseractModelDownloadService.Install(TesseractModelDownloadService.Models.ToDictionary(x => x.Language,
                x => File.ReadAllBytes(Path.Combine(localDirectory, x.Language + ".traineddata"))), root, "chi_sim+eng", default))).GetAwaiter().GetResult();
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 720, 180));
            draw.DrawText(new FormattedText("Hello 世界 123", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei"), 48, Brushes.Black, 1), new Point(30, 55));
        }
        var image = new RenderTargetBitmap(720, 180, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
        var settings = new AppSettings { OcrBackend = "Tesseract", TesseractDataDirectory = directory, TesseractLanguages = "chi_sim+eng", RecognitionTimeoutSeconds = 60 };
        var service = new LocalRecognitionService();
        var document = service.Recognize(image, settings).GetAwaiter().GetResult();
        Require(document.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase) && document.Text.Contains("世界") && document.Text.Contains("123"), "Real Tesseract Chinese/English recognition failed: " + document.Text);
        Require(document.Blocks.All(x => x.Bounds.Left >= 0 && x.Bounds.Top >= 0 && x.Bounds.Right <= 720 && x.Bounds.Bottom <= 180), "Tesseract pixel bounds failed.");
        settings.OcrMaximumEdge = 360;
        var scaled = service.Recognize(image, settings).GetAwaiter().GetResult();
        Require(scaled.Blocks.Count > 0 && scaled.Blocks.All(x => x.Bounds.Right <= 720 && x.Bounds.Bottom <= 180), "Tesseract resized-image bounds failed.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { service.Recognize(image, settings, cancellation.Token).GetAwaiter().GetResult(); throw new Exception("Tesseract cancellation ignored."); } catch (OperationCanceledException) { }
        return "PASS: fixed SHA256 and sizes, real Tesseract native engine, Chinese/English recognition, resized coordinates and cancellation.\n" + document.Text + "\n" + directory;
    }
    internal static string TestCutoutModel(string? localModel)
    {
        var root = Path.Combine(AppPaths.Root, "models");
        var path = Task.Run(() => localModel is null
            ? new CutoutModelDownloadService().Download(root, new Progress<string>(), default)
            : Task.FromResult(CutoutModelDownloadService.Install(File.ReadAllBytes(localModel), root, default))).GetAwaiter().GetResult();
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 320));
            draw.DrawEllipse(Brushes.Red, null, new Point(160, 160), 75, 110);
        }
        var image = new RenderTargetBitmap(320, 320, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
        var settings = new AppSettings { CutoutModel = path, CutoutProfile = "U2Net", CutoutInputSize = 320 };
        var output = OnnxImageRuntime.Shared.RemoveBackground(image, settings).GetAwaiter().GetResult();
        Require(output.PixelWidth == 320 && output.PixelHeight == 320, "Real cutout model dimensions failed.");
        var converted = new FormatConvertedBitmap(output, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[320 * 320 * 4]; converted.CopyPixels(pixels, 320 * 4, 0);
        var alpha = Enumerable.Range(0, 320 * 320).Select(i => pixels[i * 4 + 3]).ToArray();
        Require(alpha.Min() < 10 && alpha.Max() > 245, "Real cutout model did not produce foreground/background alpha.");
        return "PASS: pinned U2Net small SHA256, installation, input/output contract and real inference; visual quality requires manual review.\n" + path;
    }
    internal static string DownloadModels(string device = "CPU", int deviceId = 0)
    {
        var root = Path.Combine(AppPaths.Root, "models");
        var paths = Task.Run(() => new OcrModelDownloadService().Download(root, new Progress<string>(), CancellationToken.None)).GetAwaiter().GetResult();
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 120));
            draw.DrawText(new FormattedText("Hello 世界 123", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei"), 42, Brushes.Black, 1), new Point(24, 30));
        }
        var image = new RenderTargetBitmap(640, 120, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
        var settings = new AppSettings { OcrDetectionModel = paths.Detection, OcrRecognitionModel = paths.Recognition, OcrDictionary = paths.Dictionary, OnnxDevice = device, OnnxDeviceId = deviceId };
        var document = new LocalRecognitionService().RecognizePaddle(image, settings).GetAwaiter().GetResult();
        var text = string.Join(" ", document.Blocks.Select(x => x.Text));
        Require(text.Contains("Hello", StringComparison.OrdinalIgnoreCase) && text.Contains("世界") && text.Contains("123"), "Downloaded Chinese/English model recognition failed: " + text);
        var report = new StringBuilder("PASS: downloaded PP-OCRv6 Small + matching dictionary; " + document.Backend + "; recognized " + text);
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        {
            var sample = new DrawingVisual();
            using (var draw = sample.RenderOpen())
            {
                draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 840, 240));
                string[] lines = ["订单编号 123456", "Hello NimCap Screenshot 2026", "金额 99.50 CNY"];
                for (var i = 0; i < lines.Length; i++) draw.DrawText(new FormattedText(lines[i], System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei"), 30, Brushes.Black, 1), new Point(24, 18 + i * 70));
            }
            var sampleImage = new RenderTargetBitmap((int)(840 * scale), (int)(240 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            sampleImage.Render(sample); sampleImage.Freeze();
            var sampleResult = new LocalRecognitionService().RecognizePaddle(sampleImage, settings).GetAwaiter().GetResult();
            Require(new[] { "123456", "NimCap.Screenshot", "2026", "99.50" }.All(value => sampleResult.Text.Contains(value, StringComparison.OrdinalIgnoreCase)),
                "Multiline/scaled OCR failed at " + scale + ": " + sampleResult.Text);
            Require(sampleResult.Blocks.Count >= 3 && sampleResult.Blocks.All(x => x.Bounds.Left >= 0 && x.Bounds.Top >= 0 && x.Bounds.Right <= sampleImage.PixelWidth && x.Bounds.Bottom <= sampleImage.PixelHeight),
                "Multiline/scaled OCR bounds failed.");
            report.AppendLine().Append(scale).Append("x multiline screenshot: ").Append(sampleResult.Text.Replace(Environment.NewLine, " | "));
        }
        return report.AppendLine().Append(paths).ToString();
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    public static bool Run(string directory)
    {
        var fresh = new AppSettings();
        CutoutModels.Migrate(fresh);
        Require(fresh.CutoutDefaultModelId == "birefnet-general-lite" && CutoutModels.All.Count == 6, "Cutout catalog/default failed.");
        Require(CutoutModels.All.Select(x => x.Id).Distinct().Count() == CutoutModels.All.Count &&
            CutoutModels.All.All(x => Uri.TryCreate(x.Source, UriKind.Absolute, out var uri) && uri.Scheme == "https" && x.Hash.Length is 32 or 64), "Cutout catalog source/checksum failed.");
        var general = CutoutModels.All.Single(x => x.Id == "birefnet-general");
        var hr = CutoutModels.All.Single(x => x.Id == "birefnet-hr-matting");
        Require(general.Profile == "BiRefNet" && general.InputSize == 1024 && hr.Profile == "BiRefNetAlpha" && hr.InputSize == 2048 && hr.Hash.Length == 64, "High quality model contracts failed.");
        fresh.CutoutDefaultModelId = hr.Id;
        fresh.CutoutModels[hr.Id] = new() { Path = "hr.onnx", Profile = hr.Profile, InputSize = hr.InputSize };
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(fresh))!;
        CutoutModels.Migrate(restored);
        var hrSettings = CutoutModels.ForModel(restored, hr.Id);
        Require(restored.CutoutDefaultModelId == hr.Id && hrSettings.CutoutProfile == "BiRefNetAlpha" && hrSettings.CutoutInputSize == 2048 && hrSettings.CutoutModel == "hr.onnx", "HR model persistence failed.");
        var legacy = new AppSettings { CutoutModel = "legacy.onnx", CutoutProfile = "U2Net", CutoutInputSize = 320 };
        CutoutModels.Migrate(legacy);
        CutoutModels.Migrate(legacy);
        Require(legacy.CutoutDefaultModelId == "u2netp" && legacy.CutoutModels.Count == 1, "Legacy cutout migration failed.");
        legacy.CutoutModels["birefnet-matting"] = new() { Path = "matting.onnx", Profile = "BiRefNetMatting", InputSize = 1024 };
        var snapshot = CutoutModels.ForModel(legacy, "birefnet-matting");
        Require(snapshot.CutoutModel == "matting.onnx" && snapshot.CutoutProfile == "BiRefNetMatting" && snapshot.CutoutInputSize == 1024 && legacy.CutoutModel == "legacy.onnx" && legacy.CutoutDefaultModelId == "u2netp", "Cutout selection changed default.");
        Require(CutoutModels.ForModel(legacy, "birefnet-portrait").CutoutModel == "", "Missing model silently reused another model.");
        var det = Path.Combine(directory, "test-det.onnx"); var rec = Path.Combine(directory, "test-rec.onnx"); var mask = Path.Combine(directory, "test-mask.onnx"); var dict = Path.Combine(directory, "test-dict.txt");
        var map = new float[1024]; for (var y = 8; y < 20; y++) for (var x = 6; x < 26; x++) map[y * 32 + x] = 1;
        ConstantModel(det, [1, 3, 32, 32], [1, 1, 32, 32], map);
        ConstantModel(rec, [1, 3, 48, 64], [1, 4, 3], [0, 9, 0, 0, 9, 0, 9, 0, 0, 0, 0, 9]);
        ConstantModel(mask, [1, 3, 32, 32], [1, 1, 32, 32], Enumerable.Range(0, 1024).Select(i => i % 32 < 16 ? 0f : 1f).ToArray());
        File.WriteAllText(dict, "中\nA\n", new UTF8Encoding(false));
        var settings = new AppSettings { OcrDetectionModel = det, OcrRecognitionModel = rec, OcrDictionary = dict, CutoutModel = mask, RecognitionThreads = 1 };
        var pixels = new byte[32 * 32 * 4]; for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 70; pixels[i + 1] = 110; pixels[i + 2] = 220; pixels[i + 3] = 128; }
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128); image.Freeze();
        var service = new LocalRecognitionService();
        var document = service.RecognizePaddle(image, settings).GetAwaiter().GetResult();
        Require(document.Text == "中A" && document.Blocks.Count == 1 && document.Blocks[0].Bounds.Right <= 32, "Real ONNX detection/CTC pipeline failed.");
        var cutout = OnnxImageRuntime.Shared.RemoveBackground(image, settings, default).GetAwaiter().GetResult();
        var output = new FormatConvertedBitmap(cutout, PixelFormats.Bgra32, null, 0); output.CopyPixels(pixels, 128, 0);
        Require(pixels[(16 * 32 + 2) * 4 + 3] < 3 && Math.Abs(pixels[(16 * 32 + 29) * 4 + 3] - 128) <= 2, "Cutout alpha contract failed.");
        settings.CutoutProfile = "BiRefNet";
        cutout = OnnxImageRuntime.Shared.RemoveBackground(image, settings).GetAwaiter().GetResult();
        new FormatConvertedBitmap(cutout, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 128, 0);
        Require(pixels[(16 * 32 + 2) * 4 + 3] < 3 && Math.Abs(pixels[(16 * 32 + 29) * 4 + 3] - 128) <= 2, "BiRefNet sigmoid/min-max failed.");
        settings.CutoutProfile = "BiRefNetMatting";
        cutout = OnnxImageRuntime.Shared.RemoveBackground(image, settings).GetAwaiter().GetResult();
        new FormatConvertedBitmap(cutout, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 128, 0);
        Require(Math.Abs(pixels[(16 * 32 + 2) * 4 + 3] - 64) <= 2 && Math.Abs(pixels[(16 * 32 + 29) * 4 + 3] - 94) <= 2, "Matting soft alpha was stretched instead of preserved.");
        var alphaModel = Path.Combine(directory, "test-alpha.onnx");
        ConstantModel(alphaModel, [1, 3, 32, 32], [1, 1, 32, 32], Enumerable.Range(0, 1024).Select(i => i % 32 < 16 ? .25f : .75f).ToArray());
        settings.CutoutModel = alphaModel; settings.CutoutProfile = "BiRefNetAlpha";
        cutout = OnnxImageRuntime.Shared.RemoveBackground(image, settings).GetAwaiter().GetResult();
        new FormatConvertedBitmap(cutout, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 128, 0);
        Require(Math.Abs(pixels[(16 * 32 + 2) * 4 + 3] - 32) <= 2 && Math.Abs(pixels[(16 * 32 + 29) * 4 + 3] - 96) <= 2, "HR alpha must not receive a second sigmoid or min-max.");
        settings.CutoutModel = mask;
        settings.CutoutProfile = "U2Net";
        using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); try { service.RecognizePaddle(image, settings, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { } }
        try { LocalRecognitionService.DecodeCtc([0, 1], [1, 1, 2], ["中", "A"]); throw new Exception("Dictionary mismatch accepted."); } catch (InvalidDataException) { }
        try { OnnxImageRuntime.RequireFile(Path.Combine(directory, "missing.onnx"), "model"); throw new Exception("Missing model accepted."); } catch (FileNotFoundException) { }
        foreach (var format in new[] { BarcodeFormat.QR_CODE, BarcodeFormat.CODE_128, BarcodeFormat.EAN_13 })
        {
            var text = format == BarcodeFormat.EAN_13 ? "5901234123457" : "NimCap.Screenshot-123";
            var barcode = new BarcodeWriterPixelData { Format = format, Options = new EncodingOptions { Width = 480, Height = format == BarcodeFormat.QR_CODE ? 480 : 160, Margin = 16 } }.Write(text);
            var bitmap = BitmapSource.Create(barcode.Width, barcode.Height, 96, 96, PixelFormats.Bgra32, null, barcode.Pixels, barcode.Width * 4); bitmap.Freeze();
            Require(service.Barcodes(bitmap, settings).GetAwaiter().GetResult().Blocks.Any(x => x.Text == text), "Barcode decode failed: " + format);
        }
        RecognizedCell[] cells = [new() { Row = 1, Column = 1, ColumnSpan = 2, Text = "=1+1" }, new() { Row = 2, Column = 1, Text = "a,\"b\"\n中" }];
        var csv = Encoding.UTF8.GetString(RecognitionExport.Csv(cells)); Require(csv.Contains("'=1+1") && csv.Contains("a,\"\"b\"\""), "CSV escaping failed.");
        using (var zip = new ZipArchive(new MemoryStream(RecognitionExport.Xlsx(cells))))
        {
            foreach (var entry in zip.Entries) { using var stream = entry.Open(); XDocument.Load(stream); }
            using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var xml = XDocument.Load(sheet).ToString();
            Require(xml.Contains("A1:B1") && xml.Contains("inlineStr") && !xml.Contains("<f>"), "XLSX merge/string safety failed.");
        }
        try { RecognitionExport.Validate([new() { Row = 1, Column = 1, ColumnSpan = 2 }, new() { Row = 1, Column = 2 }]); throw new Exception("Overlap accepted."); } catch (ArgumentException) { }
        foreach (var screen in new[] { new System.Drawing.Rectangle(-1920, -200, 1920, 1080), new System.Drawing.Rectangle(0, 0, 320, 100) })
        foreach (var point in new[] { screen.Location, new System.Drawing.Point(screen.Right - 1, screen.Bottom - 1) })
        { var region = ScreenWordService.Region(point, screen); Require(screen.Contains(region) && region.Width <= 480 && region.Height <= 140, "Screen-word physical bounds failed."); }
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        foreach (var work in new[] { new System.Drawing.Rectangle(-1920, -200, 1920, 1040), new System.Drawing.Rectangle(0, 0, 2560, 1400) })
        foreach (var point in new[] { work.Location, new System.Drawing.Point(work.Right - 1, work.Bottom - 1) })
        {
            var region = ScreenWordService.Region(point, work); var popup = ScreenWordService.PopupRegion(region, work, scale);
            Require(popup is { } rect && work.Contains(rect) && !rect.IntersectsWith(region) && rect.Height == (int)Math.Ceiling(130 * scale), "Screen-word mixed DPI popup overlaps capture or leaves work area.");
        }
        Require(ScreenWordService.PopupRegion(new System.Drawing.Rectangle(0, 0, 320, 100), new System.Drawing.Rectangle(0, 0, 320, 100), 2) is null, "Screen-word must hide if no room outside capture.");
        TestStructureDecoders();
        Require(OcrModelDownloadService.Packages.Select(x => x.Id).SequenceEqual(new[] { "tiny", "small", "medium" }) &&
            OcrModelDownloadService.DefaultPackage.Id == "small" &&
            OcrModelDownloadService.Packages.SelectMany(x => x.Assets).All(x => x.Size > 1_000 && x.Hash.Length == 64),
            "PP-OCRv6 package catalog failed.");
        try { CutoutModelDownloadService.Install([1, 2, 3], directory, default); throw new Exception("Invalid cutout model accepted."); } catch (InvalidDataException) { }
        try { TesseractModelDownloadService.Validate(TesseractModelDownloadService.Models[0], [1, 2, 3]); throw new Exception("Invalid traineddata accepted."); } catch (InvalidDataException) { }
        try { LocalRecognitionService.ValidateTesseractData(directory, "../eng"); throw new Exception("Unsafe traineddata language accepted."); } catch (ArgumentException) { }
        Require(new AppSettings().TranslationSource == "en" && new AppSettings().TranslationTarget == "zh", "Translation defaults failed.");
        Require(LocalTranslationSetupService.Models.Select(model => model.Id).SequenceEqual(
                new[] { "qwen35-4b", "qwen35-2b", "qwen35-9b", "qwen35-27b-q4", "qwen35-27b-q5" }) &&
            LocalTranslationSetupService.Models.All(model => model.Revision.Length == 40 && model.Hash.Length == 64 && model.Size > 1_000_000_000) &&
            LocalTranslationSetupService.AvailableModels("CPU", 0, 8UL << 30).Select(model => model.Id).SequenceEqual(new[] { "qwen35-4b", "qwen35-2b" }) &&
            LocalTranslationSetupService.AvailableModels("Vulkan", LocalTranslationSetupService.Q5MinimumVideoMemory, 0).Any(model => model.Id == "qwen35-27b-q5") &&
            OcrModelDownloadService.SupportsDevice("tiny", "CPU") && !OcrModelDownloadService.SupportsDevice("tiny", "DirectML") &&
            !OcrModelDownloadService.SupportsDevice("medium", "CPU") && OcrModelDownloadService.SupportsDevice("medium", "DirectML") &&
            LocalTranslationSetupService.Version == "b12470", "Qwen3.5 local model catalog or hardware filter failed.");
        Task.Run(TestPrivacy).GetAwaiter().GetResult();
        Task.Run(async () =>
        {
            var local = new AppSettings { TranslationProvider = "Local", AllowOnlineTranslation = false };
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await LocalTranslationService.Translate("Hello", local, cancelled.Token); throw new Exception("Local cancellation ignored."); }
            catch (OperationCanceledException) { }
            try { await LocalTranslationService.Translate("Hello", local, default); throw new Exception("Missing local runtime accepted."); }
            catch (InvalidOperationException) { }
            try { await LocalTranslationService.Translate(new string('x', 6001), local, default); throw new Exception("Local size limit ignored."); }
            catch (ArgumentException) { }
            foreach (var provider in new[] { "Local", "MyMemory", "Google", "LibreTranslate" })
                Require(new Uri(RecognitionProviderCatalog.Translation(provider).Website).Scheme == "https", "Provider site must be HTTPS.");
        }).GetAwaiter().GetResult();
        return true;
    }
    private static void TestStructureDecoders()
    {
        string[] labels = ["sos", "<tr>", "<td", " colspan=\"2\"", ">", "</td>", "<td></td>", "</tr>", "eos", " rowspan=\"2\""];
        int[] sequence = [1, 2, 3, 4, 5, 6, 7, 1, 6, 6, 6, 7, 8];
        var probabilities = new float[sequence.Length * labels.Length]; var boxes = new float[sequence.Length * 8];
        for (var t = 0; t < sequence.Length; t++)
        {
            probabilities[t * labels.Length + sequence[t]] = 1;
            float[] rectangle = [.05f, .05f, .45f, .05f, .45f, .45f, .05f, .45f];
            rectangle.CopyTo(boxes, t * 8);
        }
        var cells = LocalRecognitionService.DecodeTable(probabilities, [1, sequence.Length, labels.Length], boxes, [1, sequence.Length, 8], labels, 800, 400);
        Require(cells.Length == 5 && cells[0].Cell.ColumnSpan == 2 && cells[1].Cell.Column == 3 && cells[4].Cell.Row == 2 && cells[4].Cell.Column == 3,
            "SLANet merged-column structure decoding failed.");
        sequence = [1, 2, 9, 4, 5, 6, 7, 1, 6, 7, 8];
        probabilities = new float[sequence.Length * labels.Length]; boxes = new float[sequence.Length * 8];
        for (var t = 0; t < sequence.Length; t++) probabilities[t * labels.Length + sequence[t]] = 1;
        cells = LocalRecognitionService.DecodeTable(probabilities, [1, sequence.Length, labels.Length], boxes, [1, sequence.Length, 8], labels, 800, 400);
        Require(cells[0].Cell.RowSpan == 2 && cells[2].Cell.Row == 2 && cells[2].Cell.Column == 2, "SLANet row-span occupancy failed.");
        probabilities[0] = float.NaN;
        try { LocalRecognitionService.DecodeTable(probabilities, [1, sequence.Length, labels.Length], boxes, [1, sequence.Length, 8], labels, 800, 400); throw new Exception("Invalid table output accepted."); } catch (InvalidDataException) { }
        var latex = LocalRecognitionService.DecodeFormulaTokens([1, 3, 4, 5, 2], new Dictionary<int, string> { [3] = "\\frac", [4] = "Ġ{x}", [5] = "Ġ{2}" });
        Require(latex == "\\frac {x} {2}", "LaTeX vocabulary decoding failed.");
        try { LocalRecognitionService.DecodeFormulaTokens([99], new Dictionary<int, string>()); throw new Exception("Unknown formula token accepted."); } catch (InvalidDataException) { }
    }
    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Requests;
        public bool GoogleRedirect, GoogleError, GoogleOversized, GoogleWaitForCancellation;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.Host == "translate.googleapis.com")
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/translate_a/single", "GTranslate endpoint changed.");
                Require((await request.Content!.ReadAsStringAsync(cancellationToken)).Contains("q=hello"), "GTranslate text encoding failed.");
                if (GoogleWaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (GoogleRedirect) { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://example.com/redirect"); return redirect; }
                if (GoogleError) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                if (GoogleOversized) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1_000_001)) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"sentences\":[{\"trans\":\"你好\",\"orig\":\"hello\"}],\"src\":\"en\",\"confidence\":1.0}") };
            }
            if (request.RequestUri!.Host == "api.mymemory.translated.net")
            {
                Require(request.Method == HttpMethod.Get && request.RequestUri.Query.Contains("langpair=en%7Czh") && request.RequestUri.Query.Contains("q=hello"), "MyMemory request failed.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"你好 &amp; 世界\"}}") };
            }
            if (request.RequestUri!.AbsolutePath == "/translate")
            {
                using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Require(json.RootElement.GetProperty("source").GetString() == "en" && json.RootElement.GetProperty("target").GetString() == "zh", "Translation direction failed.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"translatedText\":\"测试\",\"url\":\"https://example.com/result\"}") };
        }
    }
    private static async Task TestPrivacy()
    {
        var handler = new FakeHandler(); using var online = new OnlineRecognitionService(handler); var settings = new AppSettings { TranslationProvider = "LibreTranslate", TranslationEndpoint = "https://example.com/translate", ImageSearchEndpoint = "https://example.com/search" };
        for (var i = 0; i < 2; i++)
        {
            settings.AllowOnlineTranslation = settings.AllowImageSearchUpload = i == 1;
            try { await online.Translate("private", settings, i == 0, default); throw new Exception("Denied text sent."); } catch (InvalidOperationException) { }
            try { await online.Search([1, 2], settings, i == 0, default); throw new Exception("Denied image sent."); } catch (InvalidOperationException) { }
        }
        Require(handler.Requests == 0, "Privacy policy made requests.");
        Require(await online.Translate("hello", settings, true, default) == "测试", "Translation response failed.");
        Require((await online.Search([1, 2], settings, true, default)).Host == "example.com" && handler.Requests == 2, "Search response failed.");
        settings.TranslationProvider = "MyMemory";
        settings.AllowOnlineTranslation = false;
        try { await online.Translate("hello", settings, true, default); throw new Exception("Public provider bypassed policy."); } catch (InvalidOperationException) { }
        settings.AllowOnlineTranslation = true;
        try { await online.Translate("hello", settings, false, default); throw new Exception("Public provider bypassed consent."); } catch (InvalidOperationException) { }
        try { await online.Translate(new string('中', 167), settings, true, default); throw new Exception("Byte limit ignored."); } catch (ArgumentException) { }
        Require(handler.Requests == 2, "Denied public translation made requests.");
        Require(await online.Translate("hello", settings, true, default) == "你好 & 世界", "MyMemory parsing failed.");
        Require(OnlineRecognitionService.TranslationUri(settings).Host == "api.mymemory.translated.net", "Public provider consent endpoint failed.");
        settings.TranslationProvider = "Google";
        settings.AllowOnlineTranslation = false;
        try { await online.Translate("hello", settings, true, default); throw new Exception("GTranslate bypassed saved policy."); } catch (InvalidOperationException) { }
        settings.AllowOnlineTranslation = true;
        try { await online.Translate("hello", settings, false, default); throw new Exception("GTranslate bypassed consent."); } catch (InvalidOperationException) { }
        try { await online.Translate(new string('a', 5001), settings, true, default); throw new Exception("GTranslate limit ignored."); } catch (ArgumentException) { }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await online.Translate("hello", settings, true, cancelled.Token); throw new Exception("GTranslate cancellation ignored."); } catch (OperationCanceledException) { }
        }
        Require(handler.Requests == 3, "Denied GTranslate request sent data.");
        Require(await online.Translate("hello", settings, true, default) == "你好", "Real GTranslate package parsing failed.");
        Require(OnlineRecognitionService.TranslationUri(settings).Host == "translate.googleapis.com", "GTranslate consent address failed.");
        handler.GoogleRedirect = true;
        try { await online.Translate("hello", settings, true, default); throw new Exception("GTranslate redirect followed."); } catch (HttpRequestException) { }
        handler.GoogleRedirect = false; handler.GoogleError = true;
        try { await online.Translate("hello", settings, true, default); throw new Exception("GTranslate rate limit ignored."); } catch (HttpRequestException) { }
        handler.GoogleError = false; handler.GoogleOversized = true;
        try { await online.Translate("hello", settings, true, default); throw new Exception("GTranslate response limit ignored."); } catch (InvalidDataException) { }
        handler.GoogleOversized = false; handler.GoogleWaitForCancellation = true;
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            try { await online.Translate("hello", settings, true, cancelled.Token); throw new Exception("Active GTranslate request did not cancel."); } catch (OperationCanceledException) { }
        }
        try { OnlineRecognitionService.Endpoint("http://example.com"); throw new Exception("Plaintext external endpoint accepted."); } catch (ArgumentException) { }
    }
    // Minimal ONNX protobuf writer: test weights only; production models are never generated or downloaded.
    private static byte[] Var(ulong value) { var b = new List<byte>(); while (value > 127) { b.Add((byte)((value & 127) | 128)); value >>= 7; } b.Add((byte)value); return b.ToArray(); }
    private static byte[] V(int field, ulong value) => Var((ulong)(field << 3)).Concat(Var(value)).ToArray();
    private static byte[] B(int field, byte[] data) => Var((ulong)(field << 3 | 2)).Concat(Var((ulong)data.Length)).Concat(data).ToArray();
    private static byte[] S(int field, string text) => B(field, Encoding.UTF8.GetBytes(text));
    private static byte[] Join(params byte[][] values) => values.SelectMany(x => x).ToArray();
    private static byte[] Info(string name, int[] shape) => Join(S(1, name), B(2, B(1, Join(V(1, 1), B(2, Join(shape.Select(d => B(1, V(1, (ulong)d))).ToArray()))))));
    private static void ConstantModel(string path, int[] input, int[] output, float[] values)
    {
        var tensor = Join(B(1, Join(output.Select(d => Var((ulong)d)).ToArray())), V(2, 1), B(9, values.SelectMany(BitConverter.GetBytes).ToArray()));
        var attribute = Join(S(1, "value"), B(5, tensor), V(20, 4));
        var node = Join(S(2, "result"), S(4, "Constant"), B(5, attribute));
        var graph = Join(B(1, node), S(2, "fixture"), B(11, Info("image", input)), B(12, Info("result", output)));
        File.WriteAllBytes(path, Join(V(1, 8), S(2, "NimCap.Screenshot-self-test"), B(7, graph), B(8, V(2, 13))));
    }
}
