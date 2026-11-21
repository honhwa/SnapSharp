using L = NimCap.Screenshot.Localization.UiText;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Plugins;
using SkiaSharp;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using ZXing;
using ZXing.Common;

namespace NimCap.Screenshot.Services;

public sealed partial class LocalRecognitionService
{
    private readonly SkiaImageService _images = new();
    public Task<RecognitionDocument> Recognize(BitmapSource image, AppSettings settings, CancellationToken token = default) =>
        settings.OcrBackend switch
        {
            "Windows" => RecognizeWindows(image, settings, token),
            "Tesseract" => RecognizeTesseract(image, settings, token),
            "PaddleOCR" => RecognizePaddle(image, settings, token),
            _ => throw new ArgumentException(L.Get("UnknownOCREngineSelectAnEngineAgain"))
        };

    public async Task<RecognitionDocument> RecognizePaddle(BitmapSource image, AppSettings settings, CancellationToken token = default)
    {
        if (!OnnxWorker.IsWorker)
            return OnnxWorker.ToDocument(await OnnxWorker.Execute("paddle", image, settings, token).ConfigureAwait(false));
        return await OnnxImageRuntime.Shared.Execute(settings, (models, cancellation) =>
        {
            OnnxImageRuntime.ValidateImage(image);
            OnnxImageRuntime.RequireFile(settings.OcrDictionary, L.Get("PaddleOCRDictionary"));
            if (new FileInfo(settings.OcrDictionary).Length > 16_000_000) throw new ArgumentException(L.Get("OCRDictionaryIsTooLarge"));
            var dictionary = File.ReadAllLines(settings.OcrDictionary, System.Text.Encoding.UTF8).Where(x => x.Length > 0).ToArray();
            if (dictionary.Length < 2 || dictionary.Length > 200_000) throw new ArgumentException(L.Get("InvalidOCRDictionaryEntryCount"));
            var detector = models.Open(settings.OcrDetectionModel, L.Get("PaddleOCRDetectionONNXModel"));
            var recognizer = models.Open(settings.OcrRecognitionModel, L.Get("PaddleOCRRecognitionONNXModel"));
            using var source = _images.ToSkBitmap(image);
            var scale = Math.Min(1, Math.Clamp(settings.OcrMaximumEdge, 320, 2048) / (double)Math.Max(source.Width, source.Height));
            var size = OnnxImageRuntime.ModelAccess.Size(detector, Math.Max(32, (int)Math.Ceiling(source.Height * scale / 32) * 32), Math.Max(32, (int)Math.Ceiling(source.Width * scale / 32) * 32));
            var detection = OnnxImageRuntime.ModelAccess.Run(detector, OnnxImageRuntime.Tensor(source, size.Height, size.Width, "ImageNet"), cancellation);
            var boxes = DetectBoxes(detection.Data, detection.Shape, source.Width, source.Height, cancellation);
            var blocks = new List<RecognizedBlock>();
            foreach (var bounds in boxes.Take(256))
            {
                cancellation.ThrowIfCancellationRequested();
                using var crop = new SKBitmap((int)bounds.Width, (int)bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(crop)) canvas.DrawBitmap(source, new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom), new SKRect(0, 0, crop.Width, crop.Height));
                var recSize = OnnxImageRuntime.ModelAccess.Size(recognizer, 48, Math.Clamp((int)Math.Ceiling(48d * crop.Width / crop.Height / 8) * 8, 32, 2048));
                var recognized = OnnxImageRuntime.ModelAccess.Run(recognizer, OnnxImageRuntime.Tensor(crop, recSize.Height, recSize.Width, "MinusOne", true), cancellation);
                var drawWidth = Math.Min(recSize.Width, Math.Max(1, (int)Math.Ceiling(crop.Width * (double)recSize.Height / crop.Height)));
                var (text, confidence, positions) = DecodeCtcWithPositions(recognized.Data, recognized.Shape, dictionary, bounds, drawWidth / (double)recSize.Width);
                if (!string.IsNullOrWhiteSpace(text)) blocks.Add(new RecognizedBlock(text, bounds, confidence) { TextSpans = positions });
            }
            return new RecognitionDocument(ReadingOrder(blocks), L.Get("LocalPaddleOCRONNX") + models.DeviceLabel);
        }, token).ConfigureAwait(false);
    }

    public static (string Text, double Confidence) DecodeCtc(float[] values, int[] shape, string[] dictionary)
        => DecodeCtcCore(values, shape, dictionary, null);

    public static (string Text, double Confidence, IReadOnlyList<RecognizedTextSpan> Positions) DecodeCtcWithPositions(
        float[] values, int[] shape, string[] dictionary, Rect bounds, double contentFraction = 1)
    {
        if (!double.IsFinite(contentFraction) || contentFraction <= 0 || contentFraction > 1) throw new ArgumentOutOfRangeException(nameof(contentFraction));
        var tokens = new List<(int Start, int Length, double Position)>();
        var (text, confidence) = DecodeCtcCore(values, shape, dictionary, tokens);
        var positions = new List<RecognizedTextSpan>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var left = i == 0 ? 0 : Math.Clamp((tokens[i - 1].Position + tokens[i].Position) / (2 * contentFraction), 0, 1);
            var right = i == tokens.Count - 1 ? 1 : Math.Clamp((tokens[i].Position + tokens[i + 1].Position) / (2 * contentFraction), 0, 1);
            if (right > left) positions.Add(new(tokens[i].Start, tokens[i].Length,
                new Rect(bounds.Left + bounds.Width * left, bounds.Top, bounds.Width * (right - left), bounds.Height)));
        }
        return (text, confidence, positions);
    }

    private static (string Text, double Confidence) DecodeCtcCore(float[] values, int[] shape, string[] dictionary, List<(int Start, int Length, double Position)>? positions)
    {
        if (shape.Length != 3 || shape[0] != 1 || shape[1] < 1) throw new NotSupportedException(L.Get("PaddleOCRRecognitionOutputMustBeA1"));
        var classes = shape[2];
        var labels = new[] { "" }.Concat(dictionary).ToList();
        if (classes == labels.Count + 1) labels.Add(" ");
        if (classes != labels.Count || values.Length != shape[1] * classes) throw new InvalidDataException(L.Format("DictionaryModelMismatch0ModelClasses1", classes, dictionary.Length));
        var text = new System.Text.StringBuilder(); double confidence = 0; var count = 0; var previous = -1; var tokenStart = 0;
        for (var t = 0; t < shape[1]; t++)
        {
            var best = 0; var sum = 0d; var probabilities = true;
            for (var c = 0; c < classes; c++) { var value = values[t * classes + c]; if (!float.IsFinite(value)) throw new InvalidDataException(L.Get("RecognitionOutputContainsInvalidValues")); if (value > values[t * classes + best]) best = c; if (value < 0 || value > 1) probabilities = false; sum += value; }
            if (best != 0 && best != previous)
            {
                tokenStart = t;
                positions?.Add((text.Length, labels[best].Length, (t + .5) / shape[1]));
                text.Append(labels[best]);
                double score;
                if (probabilities && Math.Abs(sum - 1) < .05) score = values[t * classes + best];
                else { var maximum = values[t * classes + best]; double denominator = 0; for (var c = 0; c < classes; c++) denominator += Math.Exp(values[t * classes + c] - maximum); score = 1 / denominator; }
                confidence += score; count++;
            }
            else if (best != 0 && positions is { Count: > 0 })
            {
                var last = positions[^1];
                positions[^1] = (last.Start, last.Length, (tokenStart + t + 1) / (2d * shape[1]));
            }
            previous = best;
        }
        return (text.ToString(), count == 0 ? 0 : confidence / count);
    }
    private static Rect[] DetectBoxes(float[] values, int[] shape, int imageWidth, int imageHeight, CancellationToken token)
    {
        if (shape.Length != 4 || shape[0] != 1 || shape[1] != 1 || shape[2] < 1 || shape[3] < 1 || (long)shape[2] * shape[3] > 4_194_304 || values.Length != shape[2] * shape[3])
            throw new NotSupportedException(L.Get("DetectionOutputMustBeA11"));
        var height = shape[2]; var width = shape[3]; var visited = new bool[values.Length]; var queue = new Queue<int>(); var boxes = new List<Rect>();
        for (var index = 0; index < values.Length; index++)
        {
            if (visited[index] || values[index] < .3f || !float.IsFinite(values[index])) continue;
            token.ThrowIfCancellationRequested(); queue.Enqueue(index); visited[index] = true;
            var minX = width; var minY = height; var maxX = 0; var maxY = 0; var area = 0; double score = 0;
            while (queue.TryDequeue(out var pixel))
            {
                if ((area & 4095) == 0) token.ThrowIfCancellationRequested();
                var x = pixel % width; var y = pixel / width; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); area++; score += values[pixel];
                void Add(int p) { if (!visited[p] && values[p] >= .3f && float.IsFinite(values[p])) { visited[p] = true; queue.Enqueue(p); } }
                if (x > 0) Add(pixel - 1); if (x + 1 < width) Add(pixel + 1); if (y > 0) Add(pixel - width); if (y + 1 < height) Add(pixel + width);
            }
            if (area < 4 || score / area < .5 || maxX - minX < 2 || maxY - minY < 1) continue;
            // DB predicts a shrunken text core. Restore margins before the 48px recognizer resize;
            // insufficient unclip cuts off glyphs on high-DPI/downsampled screenshots.
            var expand = Math.Max(1, (maxY - minY + 1) * .5);
            var left = Math.Max(0, (int)Math.Floor((minX - expand) * imageWidth / width)); var top = Math.Max(0, (int)Math.Floor((minY - expand) * imageHeight / height));
            var right = Math.Min(imageWidth, (int)Math.Ceiling((maxX + 1 + expand) * imageWidth / width)); var bottom = Math.Min(imageHeight, (int)Math.Ceiling((maxY + 1 + expand) * imageHeight / height));
            if (right > left && bottom > top) boxes.Add(new Rect(left, top, right - left, bottom - top));
            if (boxes.Count > 256) throw new InvalidOperationException(L.Get("MoreThan256TextRegionsRecognizeIn"));
        }
        return boxes.ToArray();
    }
    public static IReadOnlyList<RecognizedBlock> ReadingOrder(IEnumerable<RecognizedBlock> source)
    {
        var rows = new List<List<RecognizedBlock>>();
        foreach (var block in source.OrderBy(x => x.Bounds.Top))
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(block.Bounds.Top - row[0].Bounds.Top) > Math.Min(block.Bounds.Height, row[0].Bounds.Height) * .6) rows.Add([block]);
            else row.Add(block);
        }
        return rows.SelectMany(row => row.OrderBy(x => x.Bounds.Left)).ToArray();
    }
    private async Task<RecognitionDocument> RecognizeWindows(BitmapSource image, AppSettings settings, CancellationToken token)
    {
        OnnxImageRuntime.ValidateImage(image);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RecognitionTimeoutSeconds, 1, 120)));
        var engine = string.IsNullOrWhiteSpace(settings.WindowsOcrLanguage) ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(settings.WindowsOcrLanguage));
        if (engine is null) throw new InvalidOperationException(L.Get("WindowsOCRLanguagePackUnavailableInstallThe"));
        if (!image.IsFrozen) { image = image.Clone(); image.Freeze(); }
        var maximum = (int)OcrEngine.MaxImageDimension;
        // Enlarge small screen fonts before recognition. Tall captures are processed
        // in overlapping strips instead of shrinking every character to fit the engine.
        var scale = Math.Min(3d, maximum / (double)image.PixelWidth);
        var stripHeight = Math.Max(1, (int)Math.Floor(maximum / scale));
        var overlap = Math.Min(128, stripHeight / 4);
        var blocks = new List<RecognizedBlock>();
        for (var y = 0; y < image.PixelHeight;)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var height = Math.Min(stripHeight, image.PixelHeight - y);
            var last = y + height == image.PixelHeight;
            var crop = new CroppedBitmap(image, new Int32Rect(0, y, image.PixelWidth, height));
            var input = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
            input.Freeze();
            var converted = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[input.PixelWidth * input.PixelHeight * 4]; converted.CopyPixels(pixels, input.PixelWidth * 4, 0);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(pixels), BitmapPixelFormat.Bgra8, input.PixelWidth, input.PixelHeight, BitmapAlphaMode.Straight);
            var result = await engine.RecognizeAsync(bitmap).AsTask(timeout.Token).ConfigureAwait(false);
            var sx = image.PixelWidth / (double)input.PixelWidth; var sy = height / (double)input.PixelHeight;
            foreach (var line in result.Lines.Where(line => line.Words.Count > 0))
            {
                var left = line.Words.Min(x => x.BoundingRect.Left); var top = line.Words.Min(x => x.BoundingRect.Top);
                var right = line.Words.Max(x => x.BoundingRect.Right); var bottom = line.Words.Max(x => x.BoundingRect.Bottom);
                var center = (top + bottom) * sy / 2;
                // Each overlap belongs to exactly one strip; retain complete lines.
                if (y > 0 && center < overlap / 2d || !last && center >= height - overlap / 2d) continue;
                blocks.Add(new RecognizedBlock(line.Text, new Rect(left * sx, y + top * sy, (right - left) * sx, (bottom - top) * sy))
                {
                    TextSpans = WordPositions(line.Text, line.Words.Select(w => (w.Text,
                        new Rect(w.BoundingRect.Left * sx, y + w.BoundingRect.Top * sy, w.BoundingRect.Width * sx, w.BoundingRect.Height * sy))))
                });
            }
            if (last) break;
            y += height - overlap;
        }
        return new RecognitionDocument(ReadingOrder(blocks), "Windows.Media.Ocr / " + engine.RecognizerLanguage.LanguageTag);
    }

    private static IReadOnlyList<RecognizedTextSpan> WordPositions(string text, IEnumerable<(string Text, Rect Bounds)> words)
    {
        var positions = new List<RecognizedTextSpan>();
        var from = 0;
        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word.Text)) continue;
            var start = text.IndexOf(word.Text, from, StringComparison.Ordinal);
            if (start < 0) continue;
            positions.Add(new(start, word.Text.Length, word.Bounds));
            from = start + word.Text.Length;
        }
        return positions;
    }

    public Task<string?> ReadQrCode(BitmapSource image, bool autoRotate, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var input = Math.Max(image.PixelWidth, image.PixelHeight) > 4096 ? _images.CreateThumbnail(image, 4096) : image;
        var reader = new BarcodeReaderGeneric { AutoRotate = autoRotate, Options = new DecodingOptions
        { PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var text = reader.Decode(BarcodePixels(input), input.PixelWidth, input.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32)?.Text;
        token.ThrowIfCancellationRequested();
        return text;
    }, token);

    private static byte[] BarcodePixels(BitmapSource input)
    {
        var converted = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[input.PixelWidth * input.PixelHeight * 4];
        converted.CopyPixels(pixels, input.PixelWidth * 4, 0);
        return pixels;
    }

    public Task<RecognitionDocument> Barcodes(BitmapSource image, AppSettings settings, CancellationToken token = default) => Task.Run(() =>
    {
        OnnxImageRuntime.ValidateImage(image); token.ThrowIfCancellationRequested();
        var input = _images.CreateThumbnail(image, 2048);
        var reader = new BarcodeReaderGeneric { AutoRotate = settings.BarcodeRotate, Options = new DecodingOptions
        { TryHarder = false, PossibleFormats = [BarcodeFormat.QR_CODE, BarcodeFormat.CODE_128, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.CODE_39, BarcodeFormat.UPC_A] } };
        var result = reader.DecodeMultiple(BarcodePixels(input), input.PixelWidth, input.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32) ?? [];
        token.ThrowIfCancellationRequested();
        var scale = image.PixelWidth / (double)input.PixelWidth;
        return new RecognitionDocument(result.Take(64).Select(item =>
        {
            var points = item.ResultPoints ?? []; var left = points.Length == 0 ? 0 : points.Min(x => x.X) * scale; var top = points.Length == 0 ? 0 : points.Min(x => x.Y) * scale;
            var width = points.Length == 0 ? image.PixelWidth : Math.Max(4, (points.Max(x => x.X) - points.Min(x => x.X)) * scale); var height = points.Length == 0 ? image.PixelHeight : Math.Max(4, (points.Max(x => x.Y) - points.Min(x => x.Y)) * scale);
            return new RecognizedBlock(item.Text, new Rect(left, top, width, height), Kind: item.BarcodeFormat.ToString());
        }).ToArray(), "ZXing.Net");
    }, token);
}

public sealed class OfflineRecognitionPlugin(Func<AppSettings> settings, bool barcode = false) : IRecognitionPlugin, INimCapPlugin
{
    public PluginMetadata Metadata => new(Id, barcode ? L.Get("LocalBarcodes") : L.Get("OfflineOCR"), new Version(1, 0), "NimCap.Screenshot", [PluginCapability.Recognizer]);
    public void Register(IPluginRegistry registry) => registry.RegisterRecognizer(this);
    public string Id => barcode ? "nimcap.screenshot.barcode" : "nimcap.screenshot.ocr";
    public async ValueTask<IReadOnlyList<RecognitionResult>> RecognizeAsync(BitmapSource image, PluginExecutionContext context, CancellationToken cancellationToken = default)
    {
        var service = new LocalRecognitionService();
        var result = await (barcode ? service.Barcodes(image, settings(), cancellationToken) : service.Recognize(image, settings(), cancellationToken));
        return result.Blocks.Select(block => new RecognitionResult(block.Kind, block.Text, new Dictionary<string, object?>
        { ["x"] = block.Bounds.X, ["y"] = block.Bounds.Y, ["width"] = block.Bounds.Width, ["height"] = block.Bounds.Height, ["confidence"] = block.Confidence })).ToArray();
    }
}
