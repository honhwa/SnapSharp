using System.Diagnostics;
using System.Windows;
using L = NimCap.Screenshot.Localization.UiText;

namespace NimCap.Screenshot.Services;

internal static class CaptureQrActions
{
    internal static Uri? WebUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) && url.IsWellFormedOriginalString() &&
        (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Length != 0 ? url : null;

    internal static (bool WasWebsite, bool Copied) Handle(string text, Window? owner, Action closeCapture, Action<string> showStatus) =>
        Handle(text, url => MessageBox.Show(owner, L.Format("CaptureQrOpenLink", url.AbsoluteUri),
            L.Get("RecognitionBarcodeNavigation"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes,
            OutputActions.CopyText, closeCapture, OpenInBrowser, showStatus);

    internal static (bool WasWebsite, bool Copied) Handle(string text, Func<Uri, bool> confirm, Action<string> copyText,
        Action closeCapture, Action<Uri> openBrowser, Action<string> showStatus)
    {
        var url = WebUrl(text);
        var open = url is not null && confirm(url);
        string status;
        bool copied;
        try
        {
            copyText(text);
            status = L.Get("CaptureQrCopied");
            copied = true;
        }
        catch (Exception ex)
        {
            status = L.Get("CopyFailed") + ": " + ex.Message;
            copied = false;
        }

        if (open)
        {
            closeCapture();
            openBrowser(url!);
            if (!copied) showStatus(status);
        }
        else showStatus(status);
        return (url is not null, copied);
    }

    private static void OpenInBrowser(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, L.Get("CouldNotOpenLink"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
