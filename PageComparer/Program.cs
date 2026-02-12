using System.IO.Compression;
using System.Text.Json;
using Microsoft.Playwright;
using PageComparer;
using SkiaSharp;

// Make sure Playwright is installed
var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
if (exitCode is not 0) throw new Exception($"Failed to install Playwright browser engine. Playwright install exited with code ${exitCode}");

// Pages to compare
var beforeUrl = "https://getbootstrap.com/docs/5.0";
var afterUrl = "https://getbootstrap.com/docs/5.2";

// Define routes to test
var routesToTest = new List<RouteDefinition>
{
    new() { Name = "Introduction", Route = "/getting-started/introduction" },
    new() { Name = "Download", Route = "/getting-started/download" },
    new() { Name = "Contents", Route = "/getting-started/contents" },
    new() { Name = "Browser support", Route = "/getting-started/browsers-devices" },
    new() { Name = "Typography", Route = "/content/typography" },
    new() { Name = "Forms", Route = "/forms/overview" },
    new() { Name = "Alerts", Route = "/components/alerts" },
    new() { Name = "Customize Components", Route = "/customize/components" },
    new() { Name = "About", Route = "/about/overview" },
};

// Global configurations
var defaultPageWidth = 1280;
var defaultPageHeight = 720;
var defaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/92.0.4515.159 Safari/537.36";

var defaultMobileWidth = 375;
var defaultMobileHeight = 800;
var mobileUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1";

var marginScreenshots = 50;
var maxRetries = 3;

// Playwright setup
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false });

var desktopBeforeContext = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = defaultPageWidth, Height = defaultPageHeight },
    UserAgent = defaultUserAgent
});
var desktopAfterContext = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = defaultPageWidth, Height = defaultPageHeight },
    UserAgent = defaultUserAgent
});
var mobileBeforeContext = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = defaultMobileWidth, Height = defaultMobileHeight },
    UserAgent = mobileUserAgent
});
var mobileAfterContext = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = defaultMobileWidth, Height = defaultMobileHeight },
    UserAgent = mobileUserAgent
});

var desktopBeforePage = await desktopBeforeContext.NewPageAsync();
var desktopAfterPage = await desktopAfterContext.NewPageAsync();
var mobileBeforePage = await mobileBeforeContext.NewPageAsync();
var mobileAfterPage = await mobileAfterContext.NewPageAsync();

// Zip destination setup
var destinationFolder = "C:\\compares";
if (Directory.Exists(destinationFolder) is false) Directory.CreateDirectory(destinationFolder);
var fileName = "compare.zip";
var destionationPath = Path.Combine(destinationFolder, fileName);

using var zipFileStream = new FileStream(destionationPath, FileMode.Create);
using var zipArchive = new ZipArchive(zipFileStream, ZipArchiveMode.Create);


try
{
    foreach (var route in routesToTest)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await ProcessRouteParallel(route);
                break; // Exit attempt loop on succcess
            }
            catch (Exception)
            {
                if (attempt == maxRetries) throw;
            }

        }
    }
}
finally
{
    await desktopBeforeContext.CloseAsync();
    await desktopAfterContext.CloseAsync();
    await mobileBeforeContext.CloseAsync();
    await mobileAfterContext.CloseAsync();
}

async Task ProcessRouteParallel(RouteDefinition definition)
{
    var screenshotTasks = new[]
    {
        CapturePage(desktopBeforePage, beforeUrl, definition),
        CapturePage(desktopAfterPage, afterUrl, definition),
        CapturePage(mobileBeforePage, beforeUrl, definition),
        CapturePage(mobileAfterPage, afterUrl, definition)
    };

    var captures = await Task.WhenAll(screenshotTasks);


    // Process desktop
    await ProcessCaptures(before: captures[0], after: captures[1], isMobile: false);

    // Process mobile
    await ProcessCaptures(before: captures[2], after: captures[3], isMobile: true);

    async Task ProcessCaptures(CaptureResult before, CaptureResult after, bool isMobile)
    {
        SaveAria(definition, before.AriaSnapshot, after.AriaSnapshot, isMobile);
        SaveDom(definition, before.DomSnapshot, after.DomSnapshot, isMobile);

        using var bitmapBefore = SKBitmap.Decode(before.Screenshot);
        using var bitmapAfter = SKBitmap.Decode(after.Screenshot);

        var (differentPixels, totalPixels, differencePercentage) = CalculatePixelDifference(bitmapBefore, bitmapAfter);

        // Compare image
        var compareImage = CreateCompareImage(bitmapBefore, bitmapAfter);

        // Pixel diff image
        var diffImage = CreateDiffImage(bitmapBefore, bitmapAfter);

        // Shifted diff image
        var shiftedDiffImage = CreateShiftedDiffImage(bitmapBefore, bitmapAfter);


        var metaData = new ScreenshotMetaData
        {
            PageName = definition.Name,
            BeforeUrl = beforeUrl + definition.Route,
            AfterUrl = afterUrl + definition.Route,
            Typestamp = DateTime.UtcNow,
            DeviceType = isMobile ? "Mobile" : "Desktop",
            Viewport = new()
            {
                Width = isMobile ? defaultMobileWidth : defaultPageWidth,
                Height = isMobile ? defaultMobileHeight : defaultPageHeight
            },
            UserAgent = isMobile ? mobileUserAgent : defaultUserAgent,
            DifferencePercentage = Math.Round(differencePercentage),
            TotalPixels = totalPixels,
            DifferentPixels = differentPixels
        };

        var metaDataJson = JsonSerializer.Serialize(metaData, new JsonSerializerOptions { WriteIndented = true });

        var prefix = isMobile ? "mobile_" : "";
        AddImageToZip(definition.Name, $"{prefix}before.png", before.Screenshot);
        AddImageToZip(definition.Name, $"{prefix}after.png", after.Screenshot);
        AddImageToZip(definition.Name, $"{prefix}compare.png", compareImage);
        AddImageToZip(definition.Name, $"{prefix}diff.png", diffImage);
        AddImageToZip(definition.Name, $"{prefix}diff-shifted.png", shiftedDiffImage);

        AddTextToZip(definition.Name, $"{prefix}metadata.json", metaDataJson);
    }
}

async Task<CaptureResult> CapturePage(IPage page, string url, RouteDefinition definition)
{
    var route = url + definition.Route;

    await page.GotoAsync(route);
    if (definition.PreLoadAction is not null)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await definition.PreLoadAction(page);
        await page.GotoAsync(route);
    }
    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    if (definition.AfterLoadAction is not null) await definition.AfterLoadAction(page);

    var screenshot = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
    var ariaSnapshot = await page.Locator("html").AriaSnapshotAsync();
    var domSnapshot = await page.ContentAsync();

    if (definition.CleanUpAction is not null) await definition.CleanUpAction(page);

    return new()
    {
        Screenshot = screenshot,
        AriaSnapshot = ariaSnapshot,
        DomSnapshot = domSnapshot
    };
}
byte[] CreateCompareImage(SKBitmap before, SKBitmap after)
{
    var maxWidth = Math.Max(before.Width, after.Width);
    var maxHeight = Math.Max(before.Height, after.Height);

    var totalWidth = maxWidth * 2 + marginScreenshots;
    var totalHeight = maxHeight;

    using var bitmap = new SKBitmap(totalWidth, totalHeight);
    using var canvas = new SKCanvas(bitmap);

    // Draw before on the left
    canvas.DrawBitmap(before, 0, 0);

    // Draw after on the right (with margin)
    canvas.DrawBitmap(after, maxWidth + marginScreenshots, 0);

    using var stream = new MemoryStream();
    bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
    return stream.ToArray();
}

void SaveAria(RouteDefinition definition, string beforeAria, string afterAria, bool isMobile)
{
    var prefix = isMobile ? "mobile_ " : "";
    AddTextToZip(definition.Name, $"{prefix}before-aria.txt", beforeAria);
    AddTextToZip(definition.Name, $"{prefix}after-aria.txt", afterAria);
}
void SaveDom(RouteDefinition definition, string beforeDom, string afterDom, bool isMobile)
{
    var prefix = isMobile ? "mobile_ " : "";
    AddTextToZip(definition.Name, $"{prefix}before-dom.html", beforeDom);
    AddTextToZip(definition.Name, $"{prefix}after-dom.html", afterDom);
}

void AddImageToZip(string folderName, string fileName, byte[] imageData)
{
    var entry = zipArchive.CreateEntry($"{folderName}/{fileName}");
    using var entryStream = entry.Open();
    entryStream.Write(imageData, 0, imageData.Length);
}
void AddTextToZip(string folderName, string fileName, string textContent)
{
    var entry = zipArchive.CreateEntry($"{folderName}/{fileName}");
    using var entryStream = entry.Open();
    using var writer = new StreamWriter(entryStream);
    writer.Write(textContent);
}


#region AI Generated Code
(int differentPixels, int totalPixels, double percentage) CalculatePixelDifference(SKBitmap before, SKBitmap after, int tolerance = 10)
{
    // Use the dimensions of the smaller image to avoid index out of bounds
    int width = Math.Min(before.Width, after.Width);
    int height = Math.Min(before.Height, after.Height);
    int totalPixels = width * height;
    int differentPixels = 0;

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            var pixelBefore = before.GetPixel(x, y);
            var pixelAfter = after.GetPixel(x, y);

            // Calculate color difference using Euclidean distance
            int rDiff = Math.Abs(pixelBefore.Red - pixelAfter.Red);
            int gDiff = Math.Abs(pixelBefore.Green - pixelAfter.Green);
            int bDiff = Math.Abs(pixelBefore.Blue - pixelAfter.Blue);
            int aDiff = Math.Abs(pixelBefore.Alpha - pixelAfter.Alpha);

            // If any channel difference exceeds tolerance, count as different
            if (rDiff > tolerance || gDiff > tolerance || bDiff > tolerance || aDiff > tolerance)
            {
                differentPixels++;
            }
        }
    }

    double percentage = totalPixels > 0 ? (double)differentPixels / totalPixels * 100 : 0;
    return (differentPixels, totalPixels, percentage);
}
byte[] CreateDiffImage(SKBitmap before, SKBitmap after, int tolerance = 10)
{
    var deletedColor = new SKColor(255, 100, 100, 150);
    var addedColor = new SKColor(100, 255, 100, 150);

    // Use the dimensions of the larger image to show all differences
    int width = Math.Max(before.Width, after.Width);
    int height = Math.Max(before.Height, after.Height);

    var diffBitmap = new SKBitmap(width, height);
    using var canvas = new SKCanvas(diffBitmap);

    // Fill with white background
    canvas.Clear(SKColors.White);

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            SKColor pixelColor;

            // Check if coordinates are within both images
            bool inBefore = x < before.Width && y < before.Height;
            bool inAfter = x < after.Width && y < after.Height;

            if (inBefore && inAfter)
            {
                var pixelBefore = before.GetPixel(x, y);
                var pixelAfter = after.GetPixel(x, y);

                // Calculate color difference
                int rDiff = Math.Abs(pixelBefore.Red - pixelAfter.Red);
                int gDiff = Math.Abs(pixelBefore.Green - pixelAfter.Green);
                int bDiff = Math.Abs(pixelBefore.Blue - pixelAfter.Blue);
                int aDiff = Math.Abs(pixelBefore.Alpha - pixelAfter.Alpha);

                if (rDiff > tolerance || gDiff > tolerance || bDiff > tolerance || aDiff > tolerance)
                {
                    // Highlight difference in magenta
                    pixelColor = new SKColor(255, 0, 255, 180);
                }
                else
                {
                    // Convert to grayscale for unchanged pixels
                    byte gray = (byte)((pixelAfter.Red + pixelAfter.Green + pixelAfter.Blue) / 3);
                    pixelColor = new SKColor(gray, gray, gray);
                }
            }
            else if (inBefore)
            {
                // Pixel only in before (deleted area) - show in red
                pixelColor = deletedColor;
            }
            else
            {
                // Pixel only in after (added area) - show in green
                pixelColor = addedColor;
            }

            diffBitmap.SetPixel(x, y, pixelColor);
        }
    }

    using var stream = new MemoryStream();
    diffBitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
    return stream.ToArray();
}
byte[] CreateShiftedDiffImage(SKBitmap before, SKBitmap after)
{
    var beforeColor = new SKColor(255, 0, 0, 255); // Red
    var afterColor = new SKColor(0, 0, 255, 255);  // Blue

    // Use the dimensions of the larger image to show all differences
    int width = Math.Max(before.Width, after.Width);
    int height = Math.Max(before.Height, after.Height);

    var diffBitmap = new SKBitmap(width, height);
    using var canvas = new SKCanvas(diffBitmap);

    // Fill with white background
    canvas.Clear(SKColors.White);

    for (int y = 0; y < before.Height; y++)
    {
        for (int x = 0; x < before.Width; x++)
        {
            var pixel = before.GetPixel(x, y);

            if (isWhiteOrNearWhite(pixel) is false)
            {
                diffBitmap.SetPixel(x, y, beforeColor);
            }
        }
    }

    for (int y = 0; y < after.Height; y++)
    {
        for (int x = 0; x < after.Width; x++)
        {
            var pixel = after.GetPixel(x, y);

            if (isWhiteOrNearWhite(pixel) is false)
            {
                diffBitmap.SetPixel(x, y, afterColor);
            }
        }
    }

    using var stream = new MemoryStream();
    diffBitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
    return stream.ToArray();

    bool isWhiteOrNearWhite(SKColor color, byte threshold = 240)
    {
        return color.Red >= threshold && color.Green >= threshold && color.Blue >= threshold;
    }
}
#endregion
