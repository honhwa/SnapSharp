using System.Drawing;
using System.Windows.Media.Imaging;

namespace NimCap.Screenshot.Models;

public sealed record CapturedScreen(Rectangle Bounds, BitmapSource Image);

public sealed record WindowCandidate(Rectangle Bounds, nint Handle = default, Rectangle? ClientBounds = null)
{
    public WindowCaptureShape? CaptureShape { get; init; }
    public Rectangle SelectionBounds(bool includeFrame) => !includeFrame && ClientBounds is { Width: > 0, Height: > 0 } client
        ? Rectangle.Intersect(Bounds, client) : Bounds;
}

/// <summary>Window silhouette in physical desktop pixels, snapshotted before the overlay opens.</summary>
public sealed record WindowCaptureShape(Rectangle Bounds, double CornerRadius = 0,
    IReadOnlyList<Rectangle>? RegionRectangles = null);

public enum CaptureMode
{
    Region,
    FullScreen,
    ActiveWindow
}

public enum CaptureAction
{
    Copy,
    Save,
    Pin,
    Scrolling,
    OCR,
    Cutout,
    Recording,
    Summary
}

public sealed record CaptureResult(BitmapSource Image, Rectangle ScreenBounds, Rectangle PixelRegion,
    CaptureAction Action = CaptureAction.Copy)
{
    public CaptureMode Mode { get; init; } = CaptureMode.Region;
}
