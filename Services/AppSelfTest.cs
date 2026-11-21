using NimCap.Screenshot.Models;
using NimCap.Screenshot.Plugins;
using NimCap.Screenshot.ViewModels;
using NimCap.Screenshot.Views;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NimCap.Screenshot.Services;

// Explicit diagnostic commands run before the normal application controller starts.
internal static class AppSelfTest
{
    internal static bool TryRun(Application app, string[] args)
    {
        if (args is ["--capture-qr-self-test", var qrOutput])
            return Report(app, Path.GetFullPath(qrOutput), RecognitionSelfTest.TestCaptureQr);
        if (args is ["--shell-self-test", var shellOutput])
        {
            var directory = Path.GetFullPath(shellOutput);
            return Report(app, Path.Combine(directory, "shell-checks.txt"), () => ShellSelfTest.Run(directory), themed: true);
        }
        if (args is ["--watermark-self-test" or "--slbr-self-test", var models, var output])
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var directory = Path.GetFullPath(output);
            return Report(app, Path.Combine(directory, "watermark-checks.txt"), () => args[0] == "--slbr-self-test"
                ? SlbrSelfTest.Run(Path.GetFullPath(models), directory)
                : WatermarkSelfTest.Run(Path.GetFullPath(models), directory), themed: true);
        }
        var localization = Index("--localization-self-test");
        if (localization >= 0)
        {
            var directory = localization + 1 < args.Length ? Path.GetFullPath(args[localization + 1]) : AppPaths.DiagnosticsRoot;
            return Report(app, Path.Combine(directory, "localization-self-test.txt"), () => LocalizationSelfTest.Run(directory), themed: true);
        }
        if (Index("--license-self-test") >= 0)
        {
            var passed = false;
            return Report(app, Diagnostic("license-self-test.txt"), () => (passed = ThirdPartyLicenseService.RunSelfTest()) ? "PASS" : "FAIL",
                exitCode: () => passed ? 0 : 1);
        }
        if (Index("--recording-capture-self-test") >= 0)
        {
            _ = CheckRecordingCapture(app);
            return true;
        }
        if (Index("--recording-device-self-test") >= 0)
            return Report(app, Diagnostic("recording-device-test.txt"), () => Task.Run(RecordingSelfTest.TestDevices).GetAwaiter().GetResult());
        var translation = Index("--local-translation-self-test");
        if (translation >= 0)
            return Report(app, Diagnostic("local-translation-test.txt"), () =>
            {
                var root = translation + 1 < args.Length ? args[translation + 1] : Path.Combine(AppPaths.Root, "models", "local-translation");
                var device = translation + 2 < args.Length ? args[translation + 2] : "CPU";
                var model = translation + 3 < args.Length
                    ? LocalTranslationSetupService.Models.FirstOrDefault(x => x.Id == args[translation + 3]) ?? throw new ArgumentException("Unknown local translation model.")
                    : LocalTranslationSetupService.Models.First(x => x.Id == "qwen35-4b");
                return Task.Run(async () =>
                {
                    var install = await LocalTranslationSetupService.Install(root, device, model, new LocalTranslationSetupService.InlineProgress(
                        text => File.WriteAllText(Diagnostic("local-translation-test.txt"), text)), CancellationToken.None);
                    var settings = new AppSettings { LocalTranslationModel = install.Model, LocalTranslationRuntime = install.Runtime,
                        LocalTranslationDevice = device, TranslationSource = "en", TranslationTarget = "zh" };
                    var translated = await LocalTranslationService.Translate("Good morning. Thank you for your help.", settings, CancellationToken.None);
                    if (!translated.Any(c => c is >= '\u4e00' and <= '\u9fff')) throw new InvalidDataException("Expected Chinese translation.");
                    return $"PASS: pinned runtime/model download, SHA256, real local {device} translation, child-process cleanup.\n" + translated;
                }).GetAwaiter().GetResult();
            });
        if (Index("--recording-setup-self-test") >= 0)
            return Report(app, Diagnostic("recording-setup-test.txt"), () =>
            {
                var path = Task.Run(() => RecordingSetupService.DownloadVisualCpp(new Progress<string>(), CancellationToken.None)).GetAwaiter().GetResult();
                return "PASS: Microsoft official runtime download and Windows Authenticode trust/publisher verification. Installer not executed.\n" + path + "\n" + RecordingSetupService.Check().Description;
            });
        var tesseract = Index("--tesseract-model-self-test");
        if (tesseract >= 0)
            return Report(app, Diagnostic("tesseract-model-test.txt"), () => RecognitionSelfTest.TestTesseractModels(tesseract + 1 < args.Length ? args[tesseract + 1] : null));
        var specialist = Index("--specialist-model-self-test");
        if (specialist >= 0)
            return Report(app, Diagnostic("specialist-model-test.txt"), () => RecognitionSelfTest.TestSpecialistModels(
                specialist + 1 < args.Length ? args[specialist + 1] : null, specialist + 2 < args.Length ? args[specialist + 2] : "CPU"));
        var cutout = Index("--cutout-model-self-test");
        if (cutout >= 0)
            return Report(app, Diagnostic("cutout-model-test.txt"), () => RecognitionSelfTest.TestCutoutModel(cutout + 1 < args.Length ? args[cutout + 1] : null));
        var ocr = Index("--ocr-model-self-test");
        if (ocr >= 0)
            return Report(app, Diagnostic("ocr-model-test.txt"), () => RecognitionSelfTest.DownloadModels(
                ocr + 1 < args.Length ? args[ocr + 1] : "CPU", ocr + 2 < args.Length ? int.Parse(args[ocr + 2]) : 0));
        if (Index("--self-test") >= 0) { app.Shutdown(RunSelfTest()); return true; }
        if (Index("--capture-self-test") >= 0) { app.Shutdown(RunCaptureSelfTest()); return true; }
        return false;

        int Index(string command) => Array.FindIndex(args, argument => argument.Equals(command, StringComparison.OrdinalIgnoreCase));
        static string Diagnostic(string name) => Path.Combine(AppPaths.DiagnosticsRoot, name);
    }

    private static bool Report(Application app, string path, Func<string> check, bool themed = false, Func<int>? exitCode = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var theme = themed ? new ThemeService() : null;
            theme?.Start();
            File.WriteAllText(path, check());
            app.Shutdown(exitCode?.Invoke() ?? 0);
        }
        catch (Exception ex) { File.WriteAllText(path, ex.ToString()); app.Shutdown(1); }
        return true;
    }

    private static async Task CheckRecordingCapture(Application app)
    {
        var path = Path.Combine(AppPaths.DiagnosticsRoot, "recording-capture-test.txt");
        Directory.CreateDirectory(AppPaths.DiagnosticsRoot);
        try { File.WriteAllText(path, await RecordingCaptureSelfTest.Run()); app.Shutdown(0); }
        catch (Exception ex) { File.WriteAllText(path, ex.ToString()); app.Shutdown(1); }
    }

    private static int RunSelfTest()
    {
        // Existing UI fixtures assert Chinese text; localization has its own bilingual checks.
        LocalizationService.Apply("zh-CN");
        var testDirectory = Path.Combine(Path.GetTempPath(), "NimCap.Screenshot-SelfTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var theme = new ThemeService();
            theme.Start();
            Directory.CreateDirectory(AppPaths.DiagnosticsRoot);
            void Stage(string name) => File.WriteAllText(Path.Combine(AppPaths.DiagnosticsRoot, "self-test.txt"), "RUNNING: " + name);
            bool Check(string name, Func<bool> test) { Stage(name); return test(); }
            Stage("initial-render");
            var pixels = Enumerable.Repeat((byte)210, 96 * 64 * 4).ToArray();
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var source = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Pbgra32, null, pixels, 96 * 4);
            var annotations = new Annotation[]
            {
                new RegionEffectAnnotation(AnnotationTool.Mosaic, new Rect(4, 4, 28, 24), 8),
                new RegionEffectAnnotation(AnnotationTool.Blur, new Rect(36, 4, 28, 24), 6),
                new PenAnnotation([new Point(2, 50), new Point(90, 40)], Colors.Red, 4) { StrokeStyle = AnnotationStrokeStyle.Dot },
                new ShapeAnnotation(AnnotationTool.Line, new Point(2, 34), new Point(28, 34), Colors.Blue, 3) { StrokeStyle = AnnotationStrokeStyle.Dash },
                new ShapeAnnotation(AnnotationTool.Rectangle, new Point(4, 36), new Point(24, 48), Colors.Green, 2, Color.FromArgb(90, 0, 255, 0)),
                new PolygonAnnotation([new Point(30, 34), new Point(44, 48), new Point(54, 32)], Colors.Purple, 2, Color.FromArgb(80, 128, 0, 128)),
                new BubbleAnnotation(new Rect(56, 34, 34, 20), new Point(72, 60), "Hi", Colors.Black, 2, Colors.White, 10),
                new BubbleAnnotation(new Rect(2, 2, 8, 8), new Point(6, 14), "x", Colors.Black, 1, Colors.White, 4),
                new NumberAnnotation(new Point(78, 18), 1, Colors.Red, 10),
                new WatermarkAnnotation(new Point(5, 5), "test", Colors.Black, 12, 0.4, -15, true, 96, 64),
                new MagnifierAnnotation(new Point(16, 16), new Rect(66, 4, 24, 18), 3),
                new SpotlightAnnotation(new Rect(8, 8, 80, 48), 0.25)
            };
            var bubbleVisual = new DrawingVisual();
            using (var context = bubbleVisual.RenderOpen()) annotations.OfType<BubbleAnnotation>().Last().Draw(context);
            var images = new SkiaImageService();
            foreach (var annotation in annotations)
            {
                Stage("render-" + annotation.GetType().Name + "-" + (annotation is RegionEffectAnnotation effect ? effect.Tool : AnnotationTool.Select));
                images.Render(source, annotations.Take(Array.IndexOf(annotations, annotation) + 1));
            }
            Stage("combined-render");
            var output = images.Render(source, annotations);
            Stage("initial-export");
            Directory.CreateDirectory(testDirectory);
            var paths = new[] { Path.Combine(testDirectory, "test.png"), Path.Combine(testDirectory, "test.jpg"), Path.Combine(testDirectory, "test.webp") };
            foreach (var path in paths) images.Save(output, path, 85);
            var checks = new Dictionary<string, bool>
            {
                ["render-size"] = output.PixelWidth == 96 && output.PixelHeight == 64,
                ["encoded-files"] = paths.All(x => new FileInfo(x).Length > 0),
                ["output-files-and-window"] = Check("output-files-and-window", () => OutputSelfTest.Run(source, testDirectory)),
                ["pin-workspace"] = Check("pin-workspace", () => PinSelfTest.Run(source, testDirectory)),
                ["pin-tray-menu"] = Check("pin-tray-menu", TrayService.RunPinMenuSelfTest),
                ["pin-hotkey-routing"] = Check("pin-hotkey-routing", GlobalHotkeyHost.RunPinRoutingSelfTest),
                ["settings-pages"] = Check("settings-pages", SettingsWindow.RunLayoutSelfTest),
                ["third-party-licenses"] = Check("third-party-licenses", ThirdPartyLicenseService.RunSelfTest),
                ["all-window-themes"] = Check("all-window-themes", () => WindowThemeSelfTest.Run(source)),
                ["recording-clock-gif-native-mp4"] = Check("recording-clock-gif-native-mp4", () => RecordingSelfTest.Run(testDirectory)),
                ["recording-window"] = Check("recording-window", RecordingWindow.RunLayoutSelfTest),
                ["recording-setup-window"] = Check("recording-setup-window", RecordingSetupWindow.RunLayoutSelfTest),
                ["recognition-onnx-barcodes-exports-privacy"] = Check("recognition-onnx-barcodes-exports-privacy", () => RecognitionSelfTest.Run(testDirectory)),
                ["recognition-window"] = Check("recognition-window", () => RecognitionWindow.RunLayoutSelfTest(source)),
                ["scrolling-workflow"] = Check("scrolling-workflow", () => ScrollingSelfTest.Run(testDirectory)),
                ["annotation-history"] = Check("annotation-history", () => RunAnnotationHistorySelfTest(source)),
                ["tapered-arrows"] = Check("tapered-arrows", () => RunTaperedArrowSelfTest()),
                ["arrow-menu-previews"] = Check("arrow-menu-previews", () => RunArrowPreviewSelfTest()),
                ["toolbar-styles"] = Check("toolbar-styles", () => RunToolbarStyleSelfTest()),
                ["toolbar-icons"] = Check("toolbar-icons", () => RunToolbarIconSelfTest()),
                ["region-reselection"] = Check("region-reselection", () => SelectionWindow.RunReselectionSelfTest(source)),
                ["selection-double-click-copy"] = Check("selection-double-click-copy", () => SelectionWindow.RunDoubleClickCopySelfTest(source)),
                ["scrolling-region-selection"] = Check("scrolling-region-selection", () => SelectionWindow.RunScrollingSelectionSelfTest(source)),
                ["screen-color-picker"] = Check("screen-color-picker", SelectionWindow.RunColorPickerSelfTest),
                ["secondary-toolbar-layouts"] = Check("secondary-toolbar-layouts", () => SelectionWindow.RunSecondaryToolbarLayoutSelfTest(source)),
                ["primary-toolbar-groups"] = Check("primary-toolbar-groups", () => SelectionWindow.RunPrimaryToolbarSelfTest(source)),
                ["effect-interactions"] = Check("effect-interactions", () => AnnotationSurface.RunEffectInteractionSelfTest()),
                ["parameter-dialogs"] = Check("parameter-dialogs", RunParameterDialogSelfTest),
                ["coordinates"] = Check("coordinates", () => RunCoordinateSelfTest()),
                ["editing"] = Check("editing", () => RunEditingSelfTest(source, testDirectory)),
                ["plugins"] = Check("plugins", () => RunPluginSelfTest(source, testDirectory)),
                ["launch-trigger"] = Check("launch-trigger", () => RunTriggerSelfTest()),
                ["theme"] = Check("theme", () => RunThemeSelfTest())
            };
            var failed = checks.Where(item => !item.Value).Select(item => item.Key).ToArray();
            Directory.CreateDirectory(AppPaths.DiagnosticsRoot);
            File.WriteAllText(Path.Combine(AppPaths.DiagnosticsRoot, "self-test.txt"),
                failed.Length == 0 ? "PASS" : "FAIL: " + string.Join(", ", failed));
            return failed.Length == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DiagnosticsRoot);
                File.WriteAllText(Path.Combine(AppPaths.DiagnosticsRoot, "self-test.txt"), ex.ToString());
            }
            catch { }
            return 1;
        }
        finally
        {
            try { Directory.Delete(testDirectory, true); } catch { }
        }
    }

    private static bool RunParameterDialogSelfTest()
    {
        foreach (var dialog in new Window[] { new WatermarkDialog(), new TextInputDialog(), new CanvasExpandDialog(128, 96) })
        {
            try
            {
                if (dialog.Content is not FrameworkElement content) return false;
                content.Measure(new System.Windows.Size(440, 340));
                content.Arrange(new Rect(0, 0, 440, 340));
                var image = new RenderTargetBitmap(440, 340, 96, 96, PixelFormats.Pbgra32);
                image.Render(content);
                if (dialog is WatermarkDialog)
                {
                    var slider = (System.Windows.Controls.Slider)dialog.FindName("FontSizeSlider");
                    if (slider.Template.FindName("PART_Track", slider) is null) return false;
                    slider.Value = 60;
                    if (((WatermarkDialog)dialog).WatermarkFontSize != 60) return false;
                }
            }
            finally { dialog.Close(); }
        }
        return true;
    }

    private static bool RunToolbarIconSelfTest()
    {
        var resources = new ResourceDictionary
        {
            Source = new Uri("/NimCap.Screenshot;component/Views/ToolbarStyles.xaml", UriKind.Relative)
        };
        foreach (var kind in new[] { MahApps.Metro.IconPacks.PackIconLucideKind.ArrowUpRight,
            MahApps.Metro.IconPacks.PackIconLucideKind.Square, MahApps.Metro.IconPacks.PackIconLucideKind.Circle,
            MahApps.Metro.IconPacks.PackIconLucideKind.Minus, MahApps.Metro.IconPacks.PackIconLucideKind.SlidersHorizontal })
        {
            var icon = new MahApps.Metro.IconPacks.PackIconLucide
            {
                Kind = kind, Style = (Style)resources["ToolbarIcon"], Foreground = Brushes.White
            };
            icon.Measure(new System.Windows.Size(18, 18));
            icon.Arrange(new Rect(0, 0, 18, 18));
            icon.UpdateLayout();
            var bitmap = new RenderTargetBitmap(18, 18, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(icon);
            var pixels = new byte[18 * 18 * 4];
            bitmap.CopyPixels(pixels, 18 * 4, 0);
            if (!pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 128))
                throw new InvalidOperationException($"Toolbar icon {kind} rendered empty: size={icon.ActualWidth}x{icon.ActualHeight}, data={icon.Data}, children={VisualTreeHelper.GetChildrenCount(icon)}");
        }
        return true;
    }

    private static bool RunToolbarStyleSelfTest()
    {
        var resources = new ResourceDictionary
        {
            Source = new Uri("/NimCap.Screenshot;component/Views/ToolbarStyles.xaml", UriKind.Relative)
        };
        var panel = new System.Windows.Controls.StackPanel { Resources = resources };
        var button = new System.Windows.Controls.Button { Style = (Style)resources["ToolbarButton"], Content = "test" };
        var field = new System.Windows.Controls.TextBox { Style = (Style)resources["ToolbarTextBox"], Text = "1" };
        var check = new System.Windows.Controls.CheckBox { Style = (Style)resources["ToolbarCheckBox"], Content = "test", IsChecked = true };
        var combo = new System.Windows.Controls.ComboBox { Style = (Style)resources["ToolbarComboBox"] };
        combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "1, 2, 3" });
        combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "I, II, III" });
        combo.SelectedIndex = 0;
        var controls = new System.Windows.Controls.Control[] { button, field, check, combo };
        foreach (var control in controls) panel.Children.Add(control);
        panel.Measure(new System.Windows.Size(160, 160));
        panel.Arrange(new Rect(0, 0, 160, 160));
        panel.UpdateLayout();
        foreach (var control in controls)
            if (control.Template is null || Math.Abs(control.ActualHeight - 32) > 0.1) return false;
        if (field.Template.FindName("PART_ContentHost", field) is null ||
            combo.Template.FindName("PART_Popup", combo) is null) return false;
        combo.SelectedIndex = 1;
        check.IsChecked = false;
        var image = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32);
        image.Render(panel);
        return combo.SelectedIndex == 1 && check.IsChecked == false;
    }

    private static bool RunArrowPreviewSelfTest()
    {
        foreach (var style in Enum.GetValues<AnnotationArrowStyle>())
        {
            var preview = new ArrowStylePreview { ArrowStyle = style, Foreground = Brushes.White };
            preview.Measure(new System.Windows.Size(64, 22));
            preview.Arrange(new Rect(0, 0, 64, 22));
            var image = new RenderTargetBitmap(64, 22, 96, 96, PixelFormats.Pbgra32);
            image.Render(preview);
            var pixels = new byte[64 * 22 * 4];
            image.CopyPixels(pixels, 64 * 4, 0);
            if (!pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 128)) return false;
            // All options share the same horizontal footprint, without touching the edge.
            for (var y = 0; y < 22; y++)
                if (pixels[(y * 64) * 4 + 3] != 0 || pixels[(y * 64 + 63) * 4 + 3] != 0) return false;
        }
        return true;
    }

    private static bool RunTaperedArrowSelfTest()
    {
        foreach (var style in new[] { AnnotationArrowStyle.TaperedOutline, AnnotationArrowStyle.TaperedFilled })
        {
            var arrow = new ShapeAnnotation(AnnotationTool.Arrow,
                new Point(20, 40), new Point(140, 40), Colors.Red, 6) { ArrowStyle = style };
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) arrow.Draw(context);
            var preview = new RenderTargetBitmap(160, 80, 96, 96, PixelFormats.Pbgra32);
            preview.Render(visual);
            var pixels = new byte[160 * 80 * 4];
            preview.CopyPixels(pixels, 160 * 4, 0);
            using var bitmap = new SkiaSharp.SKBitmap(160, 80);
            using var canvas = new SkiaSharp.SKCanvas(bitmap);
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            arrow.DrawSkia(canvas);
            var filled = style == AnnotationArrowStyle.TaperedFilled;
            if ((pixels[(40 * 160 + 100) * 4 + 3] > 128) != filled ||
                (bitmap.GetPixel(100, 40).Alpha > 128) != filled) return false;
            if (!pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 128) ||
                !bitmap.Pixels.Any(pixel => pixel.Alpha > 128)) return false;

            // Degenerate and very short arrows must remain safe in both renderers.
            foreach (var end in new[] { arrow.Start, arrow.Start + new Vector(0.2, 0),
                new Point(20, 70), new Point(2, 4) })
            {
                var shortArrow = arrow with { End = end };
                using (var context = visual.RenderOpen()) shortArrow.Draw(context);
                shortArrow.DrawSkia(canvas);
            }
        }
        return true;
    }

    private static bool RunAnnotationHistorySelfTest(BitmapSource source)
    {
        var viewModel = new EditorViewModel(source);
        var completed = new ShapeAnnotation(AnnotationTool.Rectangle,
            new Point(2, 2), new Point(18, 14), Colors.Green, 2);
        viewModel.SelectedTool = AnnotationTool.Rectangle;
        viewModel.CompleteAnnotation(completed);
        if (viewModel.SelectedTool != AnnotationTool.Select || !Equals(viewModel.SelectedAnnotation, completed)) return false;
        viewModel.UndoCommand.Execute(null);

        var original = new ShapeAnnotation(AnnotationTool.Line, new Point(1, 1), new Point(20, 20), Colors.Red, 2);
        viewModel.Add(original);
        var edited = AnnotationEditing.Restyle(original, Colors.Blue, 5, null, 24,
            AnnotationStrokeStyle.Solid);
        viewModel.Replace(original, edited);
        viewModel.UndoCommand.Execute(null);
        if (viewModel.Annotations.Count != 1 || !Equals(viewModel.Annotations[0], original)) return false;
        viewModel.RedoCommand.Execute(null);
        if (viewModel.Annotations.Count != 1 || !Equals(viewModel.Annotations[0], edited)) return false;
        viewModel.Remove(edited);
        viewModel.UndoCommand.Execute(null);
        if (viewModel.Annotations.Count != 1 || !Equals(viewModel.Annotations[0], edited)) return false;

        var styleModel = new EditorViewModel(source);
        styleModel.Add(new ShapeAnnotation(AnnotationTool.Rectangle,
            new Point(4, 4), new Point(24, 18), Colors.Red, 2, Color.FromArgb(80, 255, 0, 0)));
        styleModel.SetSelectedStrokeThickness(9);
        styleModel.SetSelectedColor(Colors.Orange, recolorFill: false);
        styleModel.SetSelectedFill(Color.FromArgb(80, 255, 255, 0));
        styleModel.SetSelectedStrokeStyle(AnnotationStrokeStyle.DashDot);
        var styleUpdated = styleModel.SelectedAnnotation is ShapeAnnotation selectedShape &&
               selectedShape is
        {
            StrokeThickness: 9,
            StrokeColor: var strokeColor,
            StrokeStyle: AnnotationStrokeStyle.DashDot
        } && strokeColor == Colors.Orange &&
           selectedShape.Thickness == 9 && selectedShape.Color == Colors.Orange &&
           selectedShape.FillColor == Color.FromArgb(80, 255, 255, 0);
        if (!styleUpdated) return false;

        var textModel = new EditorViewModel(source);
        if (textModel.FontSize != 96 || textModel.TextOutlineColor != Colors.White ||
            textModel.TextOutlineThickness != 8) return false;
        var plainText = new TextAnnotation(new Point(8, 8), "outlined", Colors.Red, 16);
        var initialTextBounds = AnnotationEditing.Bounds(plainText);
        textModel.Add(plainText);
        textModel.SetSelectedFontSize(40);
        textModel.SetSelectedTextOutline(Colors.White, 4);
        textModel.SetSelectedFontFamily("Arial");
        textModel.SetSelectedColor(Colors.Orange, recolorFill: false);
        if (textModel.SelectedAnnotation is not TextAnnotation
            {
                FontSize: 40,
                TextOutlineColor: var outlineColor,
                TextOutlineThickness: 4,
                StrokeColor: var textColor
            } outlinedText || outlineColor != Colors.White || textColor != Colors.Orange ||
            outlinedText.FontFamilyName != "Arial" ||
            AnnotationEditing.Bounds(outlinedText).Width <= initialTextBounds.Width ||
            AnnotationEditing.Bounds(outlinedText).Height <= initialTextBounds.Height)
            return false;

        var arrowModel = new EditorViewModel(source);
        arrowModel.Add(new ShapeAnnotation(AnnotationTool.Arrow,
            new Point(6, 8), new Point(40, 20), Colors.Red, 3));
        arrowModel.SetSelectedArrowStyle(AnnotationArrowStyle.BothFilled);
        arrowModel.SetSelectedStrokeThickness(7);
        arrowModel.SetSelectedColor(Colors.Blue, recolorFill: false);
        arrowModel.SetSelectedStrokeStyle(AnnotationStrokeStyle.LongDash);
        return arrowModel.SelectedAnnotation is ShapeAnnotation
        {
            Tool: AnnotationTool.Arrow,
            ArrowStyle: AnnotationArrowStyle.BothFilled,
            StrokeThickness: 7,
            StrokeColor: var arrowColor,
            StrokeStyle: AnnotationStrokeStyle.LongDash
        } && arrowColor == Colors.Blue;
    }

    private static bool RunEditingSelfTest(BitmapSource source, string testDirectory)
    {
        var images = new SkiaImageService();
        var rotatedImage = images.Rotate90(source, true);
        var flippedImage = images.Flip(source, true);
        var expandedImage = images.ExpandCanvas(source, 128, 96, 16, 12, Colors.White);
        if (rotatedImage.PixelWidth != source.PixelHeight || rotatedImage.PixelHeight != source.PixelWidth ||
            flippedImage.PixelWidth != source.PixelWidth || flippedImage.PixelHeight != source.PixelHeight ||
            expandedImage.PixelWidth != 128 || expandedImage.PixelHeight != 96) return false;

        var first = new ShapeAnnotation(AnnotationTool.Rectangle, new Point(5, 5), new Point(25, 20), Colors.Red, 2)
            { StrokeStyle = AnnotationStrokeStyle.Dash };
        var second = new TextAnnotation(new Point(32, 12), "template", Colors.Blue, 12) { Rotation = 30 };
        var model = new EditorViewModel(source);
        model.Add(first);
        model.Add(second);
        model.MoveLayerTo(0);
        if (!Equals(model.Annotations[0], second)) return false;
        model.CopySelected();
        model.PasteSelected();
        if (model.Annotations.Count != 3) return false;
        model.UndoCommand.Execute(null);
        if (model.Annotations.Count != 2) return false;

        var templatePath = Path.Combine(testDirectory, "annotations.sstemplate");
        var templates = new AnnotationTemplateService();
        templates.Save(templatePath, model.Annotations, source.PixelWidth, source.PixelHeight);
        var loaded = templates.Load(templatePath, 192, 128);
        if (loaded.Count != 2 || loaded.All(annotation => Math.Abs(annotation.Rotation) < 0.001) ||
            loaded.OfType<ShapeAnnotation>().Single().StrokeStyle != AnnotationStrokeStyle.Dash) return false;

        var restyled = AnnotationEditing.Restyle(first, Colors.Green, 6, Colors.Yellow, 24, AnnotationStrokeStyle.Dot);
        if (restyled is not ShapeAnnotation { StrokeStyle: AnnotationStrokeStyle.Dot, StrokeThickness: 6 } styled ||
            styled.StrokeColor != Colors.Green || styled.FillColor != Colors.Yellow) return false;

        var resizedNumber = AnnotationEditing.Restyle(
            new NumberAnnotation(new Point(20, 20), 1, Colors.Red, 18),
            Colors.Blue, 8, null, 24, AnnotationStrokeStyle.Solid);
        if (resizedNumber is not NumberAnnotation { StrokeColor: var numberColor, Radius: 24 } || numberColor != Colors.Blue)
            return false;

        var transparentBubble = AnnotationEditing.Restyle(
            new BubbleAnnotation(new Rect(8, 8, 60, 32), new Point(38, 52), "bubble", Colors.Black, 2, Colors.White, 14),
            Colors.Red, 4, null, 20, AnnotationStrokeStyle.Dash);
        if (transparentBubble is not BubbleAnnotation { FillColor.A: 0, StrokeThickness: 4, FontSize: 20,
                StrokeStyle: AnnotationStrokeStyle.Dash })
            return false;

        var scaledShape = AnnotationEditing.Scale(first, 1.5);
        var scaledBounds = AnnotationEditing.UnrotatedBounds(scaledShape);
        if (scaledShape is not ShapeAnnotation || Math.Abs(scaledBounds.Width - 30) > 0.01 ||
            Math.Abs(scaledBounds.Height - 22.5) > 0.01 ||
            (new Point(scaledBounds.Left + scaledBounds.Width / 2, scaledBounds.Top + scaledBounds.Height / 2) -
             new Point(15, 12.5)).Length > 0.01)
            return false;

        var scaledNumber = AnnotationEditing.Scale(
            new NumberAnnotation(new Point(20, 20), 1, Colors.Red, 18), 1.1);
        if (scaledNumber is not NumberAnnotation { Radius: var scaledRadius } || Math.Abs(scaledRadius - 19.8) > 0.01)
            return false;

        if (new NumberAnnotation(new Point(), 28, Colors.Red, 12, AnnotationNumberStyle.UpperRoman).DisplayText != "XXVIII" ||
            new NumberAnnotation(new Point(), 27, Colors.Red, 12, AnnotationNumberStyle.LowerAlpha).DisplayText != "aa" ||
            new NumberAnnotation(new Point(), 27, Colors.Red, 12, AnnotationNumberStyle.UpperAlpha).DisplayText != "AA")
            return false;

        var numberTemplatePath = Path.Combine(testDirectory, "number-style.sstemplate");
        templates.Save(numberTemplatePath,
            [new NumberAnnotation(new Point(20, 20), 8, Colors.Blue, 14, AnnotationNumberStyle.UpperRoman)],
            source.PixelWidth, source.PixelHeight);
        var loadedNumber = templates.Load(numberTemplatePath, source.PixelWidth, source.PixelHeight).SingleOrDefault();
        if (loadedNumber is not NumberAnnotation { Number: 8, NumberStyle: AnnotationNumberStyle.UpperRoman, DisplayText: "VIII" })
            return false;

        var rotated = first with { Rotation = 45 };
        var center = new Point(15, 12.5);
        if (!AnnotationEditing.HitTest(rotated, center)) return false;

        var framesPerSecond = AnnotationSurface.BenchmarkCachedPreview();
        Directory.CreateDirectory(AppPaths.DiagnosticsRoot);
        File.WriteAllText(AppPaths.RenderBenchmark,
            $"NimCap Screenshot 4K cached preview command benchmark{Environment.NewLine}" +
            $"UTC: {DateTime.UtcNow:O}{Environment.NewLine}" +
            $"Frames: 600{Environment.NewLine}" +
            $"FPS: {framesPerSecond:F1}{Environment.NewLine}" +
            $"Target: 60 FPS{Environment.NewLine}");
        return framesPerSecond >= 60;
    }

    private static bool RunThemeSelfTest()
    {
        string[] requiredResources =
        [
            "WindowBackgroundBrush", "SurfaceBrush", "SurfaceAltBrush", "ControlBrush",
            "ControlHoverBrush", "TextBrush", "TextSecondaryBrush", "BorderBrush",
            "OverlaySurfaceBrush", "OverlayTextBrush", "AccentForegroundBrush",
            "ToolbarBackgroundBrush", "ToolbarBorderBrush", "ToolbarSeparatorBrush",
            "ToolbarActiveBrush", "ToolbarActiveHoverBrush"
        ];
        return requiredResources.All(key => Application.Current.TryFindResource(key) is System.Windows.Media.Brush) &&
               Application.Current.TryFindResource("ToolbarShadowColor") is System.Windows.Media.Color;
    }

    private static bool RunPluginSelfTest(BitmapSource source, string testDirectory)
    {
        var catalog = new PluginCatalog(new PluginLoadOptions(Path.Combine(testDirectory, "plugins")));
        catalog.RegisterBuiltIn(new SelfTestPlugin());
        if (catalog.Plugins.Count != 1 || !catalog.TryGetImageProcessor("self-test.identity", out var processor) || processor is null)
            return false;
        var context = new PluginExecutionContext(new EmptyServiceProvider(), new Dictionary<string, object?>());
        var output = processor.ProcessAsync(source, context).AsTask().GetAwaiter().GetResult();
        return ReferenceEquals(output, source) && catalog.Failures.Count == 0;
    }

    private sealed class SelfTestPlugin : INimCapPlugin, IImageProcessorPlugin
    {
        public PluginMetadata Metadata { get; } = new("self-test.identity", "Self-test identity",
            new Version(1, 0), "NimCap Screenshot", [PluginCapability.ImageProcessor]);
        public string Id => Metadata.Id;
        public void Register(IPluginRegistry registry) => registry.RegisterImageProcessor(this);
        public ValueTask<BitmapSource> ProcessAsync(BitmapSource image, PluginExecutionContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(image);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static bool RunCoordinateSelfTest()
    {
        var scales = new[] { 1.0, 1.25, 1.5, 1.75 };
        foreach (var dpiScale in scales)
        {
            const int imageWidth = 2560;
            const int imageHeight = 1440;
            var surface = new System.Windows.Size(imageWidth / dpiScale, imageHeight / dpiScale);
            var selection = new Rect(700 / dpiScale, 350 / dpiScale,
                600 / dpiScale, 400 / dpiScale);
            var pixels = ScreenCoordinateMapper.DipRectToImagePixels(
                selection, surface, imageWidth, imageHeight);
            if (pixels != new PixelRect(700, 350, 600, 400)) return false;
        }

        var negativeMonitor = new System.Drawing.Rectangle(-1920, -120, 1920, 1080);
        var screenRegion = ScreenCoordinateMapper.ImagePixelsToScreenPixels(
            negativeMonitor, new PixelRect(100, 70, 600, 400));
        if (screenRegion != new PixelRect(-1820, -50, 600, 400)) return false;

        var imagePoint = ScreenCoordinateMapper.ScreenPixelsToImagePoint(
            -1720, 50, screenRegion, 600, 400);
        return Math.Abs(imagePoint.X - 100) < 0.001 &&
               Math.Abs(imagePoint.Y - 100) < 0.001;
    }

    private static bool RunTriggerSelfTest()
    {
        if (!HotkeyGesture.TryParse("Ctrl+Shift+A", out var hotkey, out _) ||
            hotkey?.DisplayText != "Ctrl+Shift+A") return false;
        if (HotkeyGesture.TryParse("A", out _, out _)) return false;
        if (!LaunchCommand.TryParse(["-full", "-delay", "3", "-clip"], out var command, out _) ||
            command.Mode != CaptureMode.FullScreen || command.DelaySeconds != 3 || !command.CopyToClipboard) return false;
        return !LaunchCommand.TryParse(["-full", "-window"], out _, out _);
    }

    private static int RunCaptureSelfTest()
    {
        try
        {
            var monitors = DisplayMonitorService.EnumerateBounds();
            var virtualBounds = DisplayMonitorService.GetVirtualBounds();
            if (monitors.Count == 0 || monitors.Any(x => !virtualBounds.Contains(x)))
                throw new InvalidOperationException("Monitor geometry is invalid.");

            var wgc = new WgcCaptureService();
            var warmup = wgc.CaptureVirtualScreen().Image;
            if (warmup.PixelWidth != virtualBounds.Width || warmup.PixelHeight != virtualBounds.Height ||
                !HasOpaqueSamples(warmup) || !HasVisibleSamples(warmup))
                throw new InvalidOperationException("The WGC warm-up frame is invalid.");
            warmup = null;
            RunWgcCaptureBatch(wgc, virtualBounds, 10);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            RunWgcCaptureBatch(wgc, virtualBounds, 25);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var middle = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            RunWgcCaptureBatch(wgc, virtualBounds, 25);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var after = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            var steadyGrowth = after - middle;
            var totalGrowth = after - before;
            if (steadyGrowth > 64L * 1024 * 1024 || totalGrowth > 192L * 1024 * 1024)
                throw new InvalidOperationException(
                    $"WGC private memory growth is unstable (steady {steadyGrowth:N0}, total {totalGrowth:N0} bytes).");

            var wgcScreens = new WgcCaptureService().CaptureAllScreens();
            if (wgcScreens.Count != monitors.Count || wgcScreens.Any(x =>
                    x.Image.PixelWidth <= 0 || x.Image.PixelHeight <= 0 ||
                    !HasOpaqueSamples(x.Image) || !HasVisibleSamples(x.Image)))
                throw new InvalidOperationException("A WGC monitor frame is invalid.");
            foreach (var screen in wgcScreens)
                ValidateSelectionWindowGeometry(screen, wgc);

            // Production still retains GDI for unsupported sessions or capture denial.
            var resilient = new ResilientCaptureService(preferWgc: true, includeCursor: false);
            var capture = resilient.CaptureVirtualScreen();
            if (capture.Image.PixelWidth != virtualBounds.Width ||
                capture.Image.PixelHeight != virtualBounds.Height ||
                !HasOpaqueSamples(capture.Image) || !HasVisibleSamples(capture.Image))
                throw new InvalidOperationException("The resilient WGC capture result is invalid.");
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "NimCap.Screenshot-CaptureSelfTest.log"), ex.ToString()); } catch { }
            return 1;
        }
    }

    private static void RunWgcCaptureBatch(WgcCaptureService wgc, System.Drawing.Rectangle virtualBounds, int count)
    {
        BitmapSource? frame = null;
        for (var i = 0; i < count; i++)
        {
            frame = wgc.CaptureVirtualScreen().Image;
            if (frame.PixelWidth != virtualBounds.Width || frame.PixelHeight != virtualBounds.Height ||
                !HasOpaqueSamples(frame) || !HasVisibleSamples(frame))
                throw new InvalidOperationException($"WGC virtual desktop batch frame {i + 1} is invalid.");
            if ((i + 1) % 5 != 0) continue;
            frame = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.KeepAlive(frame);
    }

    private static void ValidateSelectionWindowGeometry(CapturedScreen screen, ICaptureService captureService)
    {
        var window = new SelectionWindow(screen, [], captureService)
        {
            Opacity = 0.01,
            ShowActivated = false
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            if (PresentationSource.FromVisual(window) is not System.Windows.Interop.HwndSource source)
                throw new InvalidOperationException("The selection overlay has no HWND source.");
            var transform = source.CompositionTarget.TransformFromDevice;
            var expectedWidth = screen.Bounds.Width * transform.M11;
            var expectedHeight = screen.Bounds.Height * transform.M22;
            if (Math.Abs(window.ActualWidth - expectedWidth) > 2 ||
                Math.Abs(window.ActualHeight - expectedHeight) > 2)
                throw new InvalidOperationException(
                    $"Selection overlay DIP size is {window.ActualWidth:F1}x{window.ActualHeight:F1}; expected {expectedWidth:F1}x{expectedHeight:F1}.");
            var topLeft = window.PointToScreen(new Point(0, 0));
            var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            if (Math.Abs(topLeft.X - screen.Bounds.Left) > 1 || Math.Abs(topLeft.Y - screen.Bounds.Top) > 1 ||
                Math.Abs(bottomRight.X - screen.Bounds.Right) > 1 || Math.Abs(bottomRight.Y - screen.Bounds.Bottom) > 1)
                throw new InvalidOperationException("Selection overlay physical bounds do not match the monitor.");
        }
        finally
        {
            window.Close();
        }
    }

    private static bool HasOpaqueSamples(BitmapSource image)
    {
        var converted = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        for (var y = 0; y < converted.PixelHeight; y += Math.Max(1, converted.PixelHeight / 8))
        for (var x = 0; x < converted.PixelWidth; x += Math.Max(1, converted.PixelWidth / 8))
        {
            converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            if (pixel[3] != byte.MaxValue) return false;
        }
        return true;
    }

    private static bool HasVisibleSamples(BitmapSource image)
    {
        var converted = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * converted.PixelHeight));
        converted.CopyPixels(pixels, stride, 0);
        var required = Math.Max(16, converted.PixelWidth * converted.PixelHeight / 10_000);
        var visible = 0;
        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            if (pixels[index] <= 2 && pixels[index + 1] <= 2 && pixels[index + 2] <= 2) continue;
            if (++visible >= required) return true;
        }
        return false;
    }

}
