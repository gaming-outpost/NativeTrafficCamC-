using Avalonia;

namespace CoastalCommandCenter.Views;

/// <summary>
/// Calculates cell positions for the camera grid window given a camera count.
///
/// Grid layouts (cell width × cell height, all in logical pixels):
///   1 camera  : 1×1  →  560 × 420
///   2 cameras : 1×2  →  560 × 210  each  (total 560 × 420)
///   3 cameras : 2×2  →  top-left 280×210, top-right 280×210, bottom 560×210
///   4 cameras : 2×2  →  280 × 210 each   (total 560 × 420)
///   5 cameras : 3×2  →  ~187 × 210 each  (total 560 × 420)
///   6 cameras : 3×2  →  same
///   7-9       : 3×3  →  ~187 × 140 each  (total 560 × 420)
///
/// The grid itself is always 560 × 420 logical pixels so the OS window stays
/// a predictable size.  The title bar (30 px) is added on top by the caller.
/// </summary>
public static class CameraGridManager
{
    public const double GridWidth  = 560;
    public const double GridHeight = 420;

    /// <summary>
    /// Returns a list of <see cref="Rect"/> values — one per camera, in order.
    /// Each rect is in grid-local coordinates (origin = top-left of the canvas).
    /// </summary>
    public static IReadOnlyList<Rect> CalculateLayout(int count)
    {
        if (count <= 0) return [];

        return count switch
        {
            1 => Layout1(),
            2 => Layout2(),
            3 => Layout3(),
            4 => Layout4(),
            5 or 6 => LayoutNxM(3, 2, count),
            _       => LayoutNxM(3, 3, count)
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Fixed layouts
    // ──────────────────────────────────────────────────────────────────────────

    private static List<Rect> Layout1() =>
    [
        new Rect(0, 0, GridWidth, GridHeight)
    ];

    private static List<Rect> Layout2()
    {
        double h = GridHeight / 2;
        return
        [
            // Camera 2 on top, camera 1 on bottom (newest on top per spec)
            new Rect(0, h, GridWidth, h),  // slot 0 → bottom
            new Rect(0, 0, GridWidth, h)   // slot 1 → top
        ];
    }

    private static List<Rect> Layout3()
    {
        double hw = GridWidth  / 2;
        double hh = GridHeight / 2;
        return
        [
            new Rect(0,  hh, GridWidth, hh), // slot 0 → bottom full-width
            new Rect(0,  0,  hw,        hh), // slot 1 → top-left
            new Rect(hw, 0,  hw,        hh)  // slot 2 → top-right
        ];
    }

    private static List<Rect> Layout4()
    {
        double hw = GridWidth  / 2;
        double hh = GridHeight / 2;
        return
        [
            new Rect(0,  hh, hw, hh),
            new Rect(hw, hh, hw, hh),
            new Rect(0,  0,  hw, hh),
            new Rect(hw, 0,  hw, hh)
        ];
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Generic N-column × M-row layout (fills left-to-right, top-to-bottom)
    // ──────────────────────────────────────────────────────────────────────────

    private static List<Rect> LayoutNxM(int cols, int rows, int count)
    {
        double cellW = GridWidth  / cols;
        double cellH = GridHeight / rows;
        var result = new List<Rect>(count);

        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            result.Add(new Rect(col * cellW, row * cellH, cellW, cellH));
        }

        return result;
    }
}
