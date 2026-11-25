using System.IO;

namespace NimCap.Screenshot.RegressionChecks;

internal static partial class Program
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(".artifacts", "scrolling-checks", Guid.NewGuid().ToString("N")));
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        try
        {
            var desktopBrowser = args is ["--desktop-browser", var executable] ? executable : null;
            using var liveData = desktopBrowser is null ? null : new LiveDataScope(Root);
            if (args is ["--samples", var directory]) { ReplayVerticalSamples(directory); return 0; }
            if (args is ["--browser-sample", var browser]) { CaptureBrowserSample(browser); return 0; }
            if (args is ["--export-sample", var project, var application, var version, var scenario])
            { ExportVerticalSample(project, application, version, scenario); return 0; }
            if (args.Length > 0 && desktopBrowser is null) throw new ArgumentException("Use no arguments, --samples DIRECTORY, --browser-sample EXE, --desktop-browser EXE, or --export-sample PROJECT APPLICATION VERSION SCENARIO.");
            if (desktopBrowser is null)
            {
                ScrollingChecks(); VerticalScenarioChecks();
                foreach (var samples in Directory.GetDirectories("tools/ScrollingChecks/Samples")) ReplayVerticalSamples(samples);
            }
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            try
            {
                System.Xml.Linq.XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = System.Xml.Linq.XDocument.Load("App.xaml").Root!.Element(wpf + "Application.Resources")!.Elements().Single();
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "services", "clr-namespace:NimCap.Screenshot.Services;assembly=NimCap.Screenshot");
                app.Resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString(),
                    new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/NimCap.Screenshot;component/") });
                var previous = NimCap.Screenshot.Localization.UiText.Culture;
                var previousContext = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
                    NimCap.Screenshot.Localization.UiText.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
                    if (desktopBrowser is not null) LiveVerticalChecks(desktopBrowser);
                    else Require(NimCap.Screenshot.Services.ScrollingSelfTest.Run(Root), "Scrolling capture/correction UI workflow failed.");
                }
                finally { NimCap.Screenshot.Localization.UiText.SetCulture(previous); SynchronizationContext.SetSynchronizationContext(previousContext); }
                if (desktopBrowser is null) Console.WriteLine("PASS: vertical capture -> unresolved preview -> manual seam -> undo/redo and project workflow.");
            }
            finally { app.Shutdown(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
