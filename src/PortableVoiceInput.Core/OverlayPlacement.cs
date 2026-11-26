namespace PortableVoiceInput.Core;

/// <summary>A rectangle in the physical screen coordinates used by Windows.</summary>
public readonly record struct ScreenRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class OverlayPlacement
{
    /// <summary>Keep an overlay within a monitor's usable area, even after a display is removed.</summary>
    public static ScreenRectangle Fit(ScreenRectangle overlay, ScreenRectangle workArea)
    {
        if (overlay.Width <= 0 || overlay.Height <= 0 || workArea.Width <= 0 || workArea.Height <= 0) return overlay;
        var width = Math.Min(overlay.Width, workArea.Width);
        var height = Math.Min(overlay.Height, workArea.Height);
        var left = Math.Clamp(overlay.Left, workArea.Left, workArea.Right - width);
        var top = Math.Clamp(overlay.Top, workArea.Top, workArea.Bottom - height);
        return new ScreenRectangle(left, top, left + width, top + height);
    }
}
