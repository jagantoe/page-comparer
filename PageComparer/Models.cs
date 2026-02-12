using Microsoft.Playwright;

namespace PageComparer;

class RouteDefinition
{
    public string Name { get; set; }
    public string Route { get; set; }
    public Func<IPage, Task>? PreLoadAction { get; set; }
    public Func<IPage, Task>? AfterLoadAction { get; set; }
    public Func<IPage, Task>? CleanUpAction { get; set; }
}

class ScreenshotMetaData
{
    public string PageName { get; set; }
    public string BeforeUrl { get; set; }
    public string AfterUrl { get; set; }
    public DateTime Typestamp { get; set; }
    public string DeviceType { get; set; }
    public ViewportSizeInfo Viewport { get; set; }
    public string UserAgent { get; set; }
    public double DifferencePercentage { get; set; }
    public int TotalPixels { get; set; }
    public int DifferentPixels { get; set; }
}

class ViewportSizeInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
}

class CaptureResult
{
    public byte[] Screenshot { get; set; }
    public string AriaSnapshot { get; set; }
    public string DomSnapshot { get; set; }
}
