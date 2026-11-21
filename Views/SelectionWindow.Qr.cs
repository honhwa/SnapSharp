using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using NimCap.Screenshot.Services;

namespace NimCap.Screenshot.Views;

public partial class SelectionWindow
{
    private readonly LocalRecognitionService _qrRecognition = new();
    private CancellationTokenSource? _qrScan;
    private string? _lastQrText;
    private Task<bool>? _qrDetection;
    private bool _waitingForQrOutput;

    private void CancelQrScan()
    {
        _qrScan?.Cancel();
        _qrScan = null;
        _qrDetection = null;
        QrStatusText.Visibility = Visibility.Collapsed;
    }

    private void ScanSelectionQr()
    {
        if (_scrollingSelection || _editor is null || !IsVisible) return;
        CancelQrScan();
        _qrDetection = ScanSelectionQrAsync(_editor.Image);
    }

    private async Task<bool> ScanSelectionQrAsync(BitmapSource image)
    {
        using var operation = new CancellationTokenSource();
        _qrScan = operation;
        var selection = _pixelSelection;
        try
        {
            await Task.Delay(220, operation.Token);
            if (!CanShowQr()) return false;
            var text = await _qrRecognition.ReadQrCode(ApplyCaptureMask(image), PrecisionPreferences.BarcodeRotate, operation.Token);
            if (!CanShowQr() || string.IsNullOrEmpty(text) || text == _lastQrText) return false;
            _lastQrText = text;
            return CaptureQrActions.Handle(text, this, () => CaptureCanceled?.Invoke(this, EventArgs.Empty), status =>
            {
                if (operation.IsCancellationRequested) return;
                QrStatusText.Text = status;
                QrStatusText.Visibility = Visibility.Visible;
                HideQrStatusAfterDelay();
            }).WasWebsite;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) { Trace.TraceWarning("Selection QR recognition failed ({0}).", ex.GetType().Name); }
        finally
        {
            if (ReferenceEquals(_qrScan, operation))
                _qrScan = null;
        }
        return false;

        bool CanShowQr() => !operation.IsCancellationRequested && _editing && IsVisible && IsActive &&
            !_dragging && !_textRecognitionActive && ToPixelSelection(_selection) == selection;
    }

    private async void HideQrStatusAfterDelay()
    {
        var detection = _qrDetection;
        await Task.Delay(3000);
        if (ReferenceEquals(_qrDetection, detection)) QrStatusText.Visibility = Visibility.Collapsed;
    }
}
