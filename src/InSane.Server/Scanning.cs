using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using NAPS2.Images;
using NAPS2.Images.ImageSharp;
using NAPS2.Scan;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Naps2BitDepth = NAPS2.Images.BitDepth;
using Naps2PageSize = NAPS2.Images.PageSize;

namespace InSane;

public sealed class Naps2Runtime : IDisposable
{
    public Naps2Runtime(StoragePaths paths, ILoggerFactory loggerFactory)
    {
        var temporary = Path.Combine(paths.State, "tmp");
        Directory.CreateDirectory(temporary);
        Context = new ScanningContext(new ImageSharpImageContext())
        {
            Logger = loggerFactory.CreateLogger("NAPS2"),
            TempFolderPath = temporary
        };
    }

    public ScanningContext Context { get; }
    public void Dispose() => Context.Dispose();
}

public static class DriverCatalog
{
    public static IReadOnlyList<DriverSupport> Get(bool includeDemo)
    {
        var list = new List<DriverSupport>
        {
            new("sane", "SANE", OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Linux or macOS",
                OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? null : "Requires a Linux or macOS scanner worker."),
            new("escl", "eSCL / AirScan", true, "Cross-platform"),
            new("wia", "Windows Image Acquisition", OperatingSystem.IsWindows(), "Windows",
                OperatingSystem.IsWindows() ? null : "Requires a Windows scanner worker."),
            new("twain", "TWAIN", OperatingSystem.IsWindows(), "Windows",
                OperatingSystem.IsWindows() ? null : "Requires a Windows scanner worker; most devices also need the NAPS2 Win32 worker."),
            new("apple", "Apple Image Capture", OperatingSystem.IsMacOS(), "macOS",
                OperatingSystem.IsMacOS() ? null : "Requires a macOS scanner worker.")
        };
        if (includeDemo) list.Insert(0, new DriverSupport("demo", "Demonstration scanner", true, "Development"));
        return list;
    }
}

public interface IScannerBackend
{
    Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<DeviceCapabilities> GetCapabilitiesAsync(string deviceKey, CancellationToken cancellationToken);
    Task ScanAsync(
        ScanSettings settings,
        Func<int, string> allocatePagePath,
        Func<int, string, Task> pageCompleted,
        Action<int, double> pageProgress,
        CancellationToken cancellationToken);
}

public sealed class ScannerRouter : IScannerBackend
{
    private readonly Naps2ScannerBackend _naps2;
    private readonly DemoScannerBackend _demo;
    private readonly bool _enableDemo;

    public ScannerRouter(Naps2ScannerBackend naps2, DemoScannerBackend demo, IOptions<InSaneOptions> options)
    {
        _naps2 = naps2;
        _demo = demo;
        _enableDemo = options.Value.Scanner.EnableDemo;
    }

    public async Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = (await _naps2.GetDevicesAsync(cancellationToken)).ToList();
        if (_enableDemo) devices.InsertRange(0, await _demo.GetDevicesAsync(cancellationToken));
        return devices;
    }

    public Task<DeviceCapabilities> GetCapabilitiesAsync(string deviceKey, CancellationToken cancellationToken) =>
        DeviceKey.Driver(deviceKey) == "demo" && _enableDemo
            ? _demo.GetCapabilitiesAsync(deviceKey, cancellationToken)
            : _naps2.GetCapabilitiesAsync(deviceKey, cancellationToken);

    public Task ScanAsync(ScanSettings settings, Func<int, string> allocatePagePath,
        Func<int, string, Task> pageCompleted, Action<int, double> pageProgress,
        CancellationToken cancellationToken) =>
        DeviceKey.Driver(settings.DeviceKey) == "demo" && _enableDemo
            ? _demo.ScanAsync(settings, allocatePagePath, pageCompleted, pageProgress, cancellationToken)
            : _naps2.ScanAsync(settings, allocatePagePath, pageCompleted, pageProgress, cancellationToken);
}

public sealed class Naps2ScannerBackend : IScannerBackend
{
    private static readonly (string Key, Naps2PageSize Size)[] CommonPageSizes =
    [
        ("letter", Naps2PageSize.Letter),
        ("legal", Naps2PageSize.Legal),
        ("a4", Naps2PageSize.A4)
    ];

    private readonly Naps2Runtime _runtime;
    private readonly ScannerOptions _options;
    private readonly ILogger<Naps2ScannerBackend> _logger;
    private readonly SemaphoreSlim _scannerGate = new(1, 1);

    public Naps2ScannerBackend(Naps2Runtime runtime, IOptions<InSaneOptions> options,
        ILogger<Naps2ScannerBackend> logger)
    {
        _runtime = runtime;
        _options = options.Value.Scanner;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var result = new List<ScannerDevice>();
        var controller = new ScanController(_runtime.Context);
        foreach (var driver in RuntimeDrivers())
        {
            try
            {
                await foreach (var device in controller.GetDevices(DeviceOptions(driver), cancellationToken))
                {
                    result.Add(new ScannerDevice(DeviceKey.Encode(device), DriverName(device.Driver), device.ID,
                        device.Name, device.ConnectionUri));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate {Driver} scanner devices", driver);
            }
        }
        return result.DistinctBy(x => x.Key).ToList();
    }

    public async Task<DeviceCapabilities> GetCapabilitiesAsync(string deviceKey,
        CancellationToken cancellationToken)
    {
        var device = DeviceKey.Decode(deviceKey);
        EnsureRuntimeDriver(device.Driver);
        var caps = await new ScanController(_runtime.Context).GetCaps(DeviceOptions(device.Driver, device), cancellationToken);
        var sources = new Dictionary<string, SourceCapabilities>();
        AddSource(sources, "flatbed", caps.FlatbedCaps);
        AddSource(sources, "feeder", caps.FeederCaps);
        AddSource(sources, "duplex", caps.DuplexCaps);
        var paperSources = new List<string>();
        if (caps.PaperSourceCaps?.SupportsFlatbed == true) paperSources.Add("flatbed");
        if (caps.PaperSourceCaps?.SupportsFeeder == true) paperSources.Add("feeder");
        if (caps.PaperSourceCaps?.SupportsDuplex == true) paperSources.Add("duplex");
        if (paperSources.Count == 0) paperSources.AddRange(sources.Keys);
        if (paperSources.Count == 0)
        {
            paperSources.Add("auto");
            sources["auto"] = DefaultSourceCapabilities();
        }
        return new DeviceCapabilities(deviceKey, paperSources, sources,
            caps.MetadataCaps?.Manufacturer, caps.MetadataCaps?.Model, caps.MetadataCaps?.DriverSubtype);
    }

    public async Task ScanAsync(ScanSettings settings, Func<int, string> allocatePagePath,
        Func<int, string, Task> pageCompleted, Action<int, double> pageProgress,
        CancellationToken cancellationToken)
    {
        var device = DeviceKey.Decode(settings.DeviceKey);
        EnsureRuntimeDriver(device.Driver);
        await _scannerGate.WaitAsync(cancellationToken);
        try
        {
            var controller = new ScanController(_runtime.Context);
            controller.PageProgress += (_, args) => pageProgress(args.PageNumber, args.Progress);
            var options = DeviceOptions(device.Driver, device);
            options.PaperSource = ParsePaperSource(settings.PaperSource);
            var automaticallySizePage = settings.PageSize.Equals("auto", StringComparison.OrdinalIgnoreCase);
            if (automaticallySizePage)
            {
                var caps = await controller.GetCaps(options, cancellationToken);
                options.PageSize = AutomaticScanArea(caps, options.PaperSource) ?? Naps2PageSize.Letter;
            }
            else
            {
                options.PageSize = ParsePageSize(settings.PageSize);
            }
            options.Dpi = settings.Resolution;
            options.BitDepth = ParseBitDepth(settings.BitDepth);
            options.AutoDeskew = settings.AutoDeskew;
            options.ExcludeBlankPages = settings.DiscardBlankPages;
            options.FlipDuplexedPages = settings.FlipDuplexBacks;
            options.Brightness = settings.Brightness;
            options.Contrast = settings.Contrast;
            options.BlankPageWhiteThreshold = settings.BlankPageWhiteThreshold;
            options.BlankPageCoverageThreshold = settings.BlankPageCoverageThreshold;
            options.ThumbnailSize = 360;
            options.Quality = 88;
            options.KeyValueOptions = new KeyValueScanOptions(settings.DriverOptions);

            var pageNumber = 0;
            await foreach (var image in controller.Scan(options, cancellationToken))
            {
                using (image)
                {
                    pageNumber++;
                    var path = allocatePagePath(pageNumber);
                    image.Save(path, ImageFileFormat.Jpeg, new ImageSaveOptions { Quality = 88 });
                    if (automaticallySizePage)
                    {
                        await AutomaticPageSizeDetector.TrimScannerBackgroundAsync(path, cancellationToken);
                    }
                    await pageCompleted(pageNumber, path);
                }
            }
        }
        finally { _scannerGate.Release(); }
    }

    private ScanOptions DeviceOptions(Driver driver, ScanDevice? device = null) => new()
    {
        Driver = driver,
        Device = device,
        EsclOptions = { SearchTimeout = _options.EsclSearchTimeoutMilliseconds },
        SaneOptions = { KeepInitialized = true }
    };

    private static IEnumerable<Driver> RuntimeDrivers()
    {
        if (OperatingSystem.IsLinux()) yield return Driver.Sane;
        if (OperatingSystem.IsMacOS()) { yield return Driver.Apple; yield return Driver.Sane; }
        if (OperatingSystem.IsWindows()) { yield return Driver.Wia; yield return Driver.Twain; }
        yield return Driver.Escl;
    }

    private static void EnsureRuntimeDriver(Driver driver)
    {
        if (!RuntimeDrivers().Contains(driver))
            throw new PlatformNotSupportedException($"{driver} is represented by the API but requires its native platform worker.");
    }

    private static void AddSource(IDictionary<string, SourceCapabilities> target, string name, PerSourceCaps? caps)
    {
        if (caps is null) return;
        var bitDepths = new List<string>();
        if (caps.BitDepthCaps?.SupportsColor == true) bitDepths.Add("color");
        if (caps.BitDepthCaps?.SupportsGrayscale == true) bitDepths.Add("grayscale");
        if (caps.BitDepthCaps?.SupportsBlackAndWhite == true) bitDepths.Add("blackAndWhite");
        var area = caps.PageSizeCaps?.ScanArea;
        target[name] = new SourceCapabilities(
            caps.DpiCaps?.CommonValues?.ToList() ?? [300],
            bitDepths.Count == 0 ? ["color"] : bitDepths,
            CommonPageSizes
                .Where(pageSize => caps.PageSizeCaps?.Fits(pageSize.Size) != false)
                .Select(pageSize => pageSize.Key)
                .ToList(),
            area is not null,
            area is null ? null : new PageDimensions(area.WidthInInches, area.HeightInInches, "in"));
    }

    private static SourceCapabilities DefaultSourceCapabilities() =>
        new([300], ["color"], CommonPageSizes.Select(pageSize => pageSize.Key).ToList(), false, null);

    private static Naps2PageSize? AutomaticScanArea(ScanCaps caps, PaperSource source) => source switch
    {
        PaperSource.Flatbed => caps.FlatbedCaps?.PageSizeCaps?.ScanArea,
        PaperSource.Feeder => caps.FeederCaps?.PageSizeCaps?.ScanArea,
        PaperSource.Duplex => caps.DuplexCaps?.PageSizeCaps?.ScanArea,
        _ => caps.FlatbedCaps?.PageSizeCaps?.ScanArea
             ?? caps.FeederCaps?.PageSizeCaps?.ScanArea
             ?? caps.DuplexCaps?.PageSizeCaps?.ScanArea
    };

    private static PaperSource ParsePaperSource(string value) => value.ToLowerInvariant() switch
    {
        "flatbed" => PaperSource.Flatbed,
        "feeder" => PaperSource.Feeder,
        "duplex" => PaperSource.Duplex,
        _ => PaperSource.Auto
    };

    private static Naps2BitDepth ParseBitDepth(string value) => value.ToLowerInvariant() switch
    {
        "grayscale" => Naps2BitDepth.Grayscale,
        "blackandwhite" or "black-white" or "bw" => Naps2BitDepth.BlackAndWhite,
        _ => Naps2BitDepth.Color
    };

    private static Naps2PageSize ParsePageSize(string value) =>
        Naps2PageSize.Parse(value) ?? Naps2PageSize.Letter;

    private static string DriverName(Driver driver) => driver.ToString().ToLowerInvariant();
}

public sealed class DemoScannerBackend : IScannerBackend
{
    private static readonly ScannerDevice DemoDevice = new(
        DeviceKey.Encode("demo", "session-canvas", "inSANE Demonstration Scanner"),
        "demo", "session-canvas", "inSANE Demonstration Scanner", IsDemo: true);

    public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ScannerDevice>>([DemoDevice]);

    public Task<DeviceCapabilities> GetCapabilitiesAsync(string deviceKey, CancellationToken cancellationToken) =>
        Task.FromResult(new DeviceCapabilities(deviceKey, ["feeder", "duplex"],
            new Dictionary<string, SourceCapabilities>
            {
                ["feeder"] = new([150, 200, 300, 600], ["color", "grayscale", "blackAndWhite"],
                    ["letter", "legal", "a4"], true, new(8.5m, 14m, "in")),
                ["duplex"] = new([150, 200, 300, 600], ["color", "grayscale", "blackAndWhite"],
                    ["letter", "legal", "a4"], true, new(8.5m, 14m, "in"))
            }, "inSANE", "Session Canvas", "demo"));

    public async Task ScanAsync(ScanSettings settings, Func<int, string> allocatePagePath,
        Func<int, string, Task> pageCompleted, Action<int, double> pageProgress,
        CancellationToken cancellationToken)
    {
        var pageCount = settings.PaperSource.Equals("duplex", StringComparison.OrdinalIgnoreCase) ? 4 : 2;
        for (var page = 1; page <= pageCount; page++)
        {
            for (var progress = 0; progress <= 10; progress++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageProgress(page, progress / 10d);
                await Task.Delay(45, cancellationToken);
            }
            var path = allocatePagePath(page);
            await CreateDemoPageAsync(path, page, settings.PageSize.Equals("auto", StringComparison.OrdinalIgnoreCase),
                cancellationToken);
            if (settings.PageSize.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                await AutomaticPageSizeDetector.TrimScannerBackgroundAsync(path, cancellationToken);
            }
            await pageCompleted(page, path);
        }
    }

    private static async Task CreateDemoPageAsync(string path, int page, bool includeScannerBackground,
        CancellationToken cancellationToken)
    {
        const int width = 850;
        const int pageHeight = 1100;
        var imageHeight = includeScannerBackground ? 1400 : pageHeight;
        using var image = new Image<Rgb24>(width, imageHeight, includeScannerBackground ? Color.Black : Color.White);
        image.Metadata.HorizontalResolution = 100;
        image.Metadata.VerticalResolution = 100;
        image.ProcessPixelRows(accessor =>
        {
            if (includeScannerBackground)
            {
                for (var y = 0; y < pageHeight; y++) Fill(accessor.GetRowSpan(y), 0, width, new Rgb24(255, 255, 255));
            }
            var ink = page % 2 == 0 ? new Rgb24(90, 46, 52) : new Rgb24(68, 62, 57);
            for (var y = 85; y < 175; y++) Fill(accessor.GetRowSpan(y), 72, 778, ink);
            for (var block = 0; block < 7; block++)
            {
                var y = 240 + block * 95;
                Fill(accessor.GetRowSpan(y), 90, 690 - (block % 3) * 85, new Rgb24(187, 181, 172));
                Fill(accessor.GetRowSpan(y + 14), 90, 760 - (block % 2) * 140, new Rgb24(222, 218, 211));
            }
            for (var y = 940; y < 980; y++) Fill(accessor.GetRowSpan(y), 90, 280 + page * 35, new Rgb24(132, 113, 103));
        });
        await image.SaveAsJpegAsync(path, new JpegEncoder { Quality = 88 }, cancellationToken);
    }

    private static void Fill(Span<Rgb24> row, int start, int end, Rgb24 color)
    {
        for (var x = start; x < Math.Min(end, row.Length); x++) row[x] = color;
    }
}

internal static class AutomaticPageSizeDetector
{
    private const int SampleStride = 4;
    private const double MinimumPaperCoverage = 0.08;
    private const double MinimumBackgroundContrast = 28;

    public static async Task TrimScannerBackgroundAsync(string path, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync<Rgb24>(path, cancellationToken);
        var bounds = DetectPaperBounds(image);
        if (bounds.X == 0 && bounds.Y == 0 && bounds.Width == image.Width && bounds.Height == image.Height) return;

        image.Mutate(context => context.Crop(bounds));
        await image.SaveAsJpegAsync(path, new JpegEncoder { Quality = 88 }, cancellationToken);
    }

    internal static Rectangle DetectPaperBounds(Image<Rgb24> image)
    {
        if (image.Width < 32 || image.Height < 32) return new Rectangle(0, 0, image.Width, image.Height);

        var patch = Math.Max(4, Math.Min(image.Width, image.Height) / 40);
        var background = AverageCornerLuminance(image, patch);
        var centre = AverageLuminance(image,
            new Rectangle(image.Width / 4, image.Height / 4, image.Width / 2, image.Height / 2));

        // A white scanner bed and a white page cannot be distinguished reliably. In that case the scanner's returned
        // image dimensions are already the safest automatic result.
        if (centre - background < MinimumBackgroundContrast)
            return new Rectangle(0, 0, image.Width, image.Height);

        var threshold = Math.Min(245, background + MinimumBackgroundContrast);
        var top = FindRow(image, threshold, fromStart: true);
        var bottom = FindRow(image, threshold, fromStart: false);
        var left = FindColumn(image, threshold, fromStart: true);
        var right = FindColumn(image, threshold, fromStart: false);
        if (top < 0 || bottom <= top || left < 0 || right <= left)
            return new Rectangle(0, 0, image.Width, image.Height);

        const int padding = 0;
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(image.Width - 1, right + padding);
        bottom = Math.Min(image.Height - 1, bottom + padding);

        // Ignore tiny adjustments caused by ordinary scanner overscan or JPEG noise.
        if ((right - left + 1) >= image.Width * 0.985 && (bottom - top + 1) >= image.Height * 0.985)
            return new Rectangle(0, 0, image.Width, image.Height);

        return new Rectangle(left, top, right - left + 1, bottom - top + 1);
    }

    private static int FindRow(Image<Rgb24> image, double threshold, bool fromStart)
    {
        var start = fromStart ? 0 : image.Height - 1;
        var end = fromStart ? image.Height : -1;
        var step = fromStart ? 1 : -1;
        for (var y = start; y != end; y += step)
        {
            var bright = 0;
            var samples = 0;
            for (var x = 0; x < image.Width; x += SampleStride)
            {
                samples++;
                if (Luminance(image[x, y]) >= threshold) bright++;
            }
            if (bright >= samples * MinimumPaperCoverage) return y;
        }
        return -1;
    }

    private static int FindColumn(Image<Rgb24> image, double threshold, bool fromStart)
    {
        var start = fromStart ? 0 : image.Width - 1;
        var end = fromStart ? image.Width : -1;
        var step = fromStart ? 1 : -1;
        for (var x = start; x != end; x += step)
        {
            var bright = 0;
            var samples = 0;
            for (var y = 0; y < image.Height; y += SampleStride)
            {
                samples++;
                if (Luminance(image[x, y]) >= threshold) bright++;
            }
            if (bright >= samples * MinimumPaperCoverage) return x;
        }
        return -1;
    }

    private static double AverageCornerLuminance(Image<Rgb24> image, int patch)
    {
        var regions = new[]
        {
            new Rectangle(0, 0, patch, patch),
            new Rectangle(image.Width - patch, 0, patch, patch),
            new Rectangle(0, image.Height - patch, patch, patch),
            new Rectangle(image.Width - patch, image.Height - patch, patch, patch)
        };
        return regions.Average(region => AverageLuminance(image, region));
    }

    private static double AverageLuminance(Image<Rgb24> image, Rectangle region)
    {
        double total = 0;
        var samples = 0;
        for (var y = region.Top; y < region.Bottom; y += SampleStride)
        {
            for (var x = region.Left; x < region.Right; x += SampleStride)
            {
                total += Luminance(image[x, y]);
                samples++;
            }
        }
        return samples == 0 ? 0 : total / samples;
    }

    private static double Luminance(Rgb24 pixel) => pixel.R * 0.2126 + pixel.G * 0.7152 + pixel.B * 0.0722;
}

public sealed class ScanCoordinator
{
    private readonly ScannerRouter _scanner;
    private readonly SessionStore _sessions;
    private readonly ILogger<ScanCoordinator> _logger;
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    public ScanCoordinator(ScannerRouter scanner, SessionStore sessions, ILogger<ScanCoordinator> logger)
    {
        _scanner = scanner;
        _sessions = sessions;
        _logger = logger;
    }

    public ScanJob Start(Guid sessionId, ScanSettings settings)
    {
        var session = _sessions.Get(sessionId) ?? throw new KeyNotFoundException("Document session not found.");
        if (session.Status == "saved") throw new InvalidOperationException("Start a new document before scanning more pages.");
        if (string.IsNullOrWhiteSpace(settings.DeviceKey)) throw new ArgumentException("Choose a scanner device.");
        if (HasActiveJob()) throw new InvalidOperationException("The scanner is already handling another job.");
        var job = new ScanJob { SessionId = sessionId };
        return StartJob(job, settings, session.Pages.Count);
    }

    public ScanJob? Get(Guid id) => _jobs.TryGetValue(id, out var value) ? value.Job : null;

    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var value)) return false;
        value.Cancellation.Cancel();
        return true;
    }

    public ScanJob Retry(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var previous)) throw new KeyNotFoundException("Scan job not found.");
        if (previous.Job.Status != "failed" || previous.Job.Error?.CanRetry != true)
            throw new InvalidOperationException("This scan job cannot be retried.");
        if (HasActiveJob()) throw new InvalidOperationException("The scanner is already handling another job.");
        var session = _sessions.Get(previous.Job.SessionId) ?? throw new KeyNotFoundException("Document session not found.");
        var job = new ScanJob
        {
            SessionId = previous.Job.SessionId,
            Attempt = previous.Job.Attempt + 1,
            PreviousJobId = previous.Job.Id
        };
        return StartJob(job, previous.Settings, session.Pages.Count);
    }

    public bool HasActiveJob() => _jobs.Values.Any(value => value.Job.Status is "queued" or "scanning");

    private ScanJob StartJob(ScanJob job, ScanSettings settings, int existingPages)
    {
        var cancellation = new CancellationTokenSource();
        _jobs[job.Id] = new JobState(job, cancellation, settings);
        _ = RunAsync(job, settings, existingPages, cancellation.Token);
        return job;
    }

    private async Task RunAsync(ScanJob job, ScanSettings settings, int existingPages, CancellationToken cancellationToken)
    {
        job.Status = "scanning";
        try
        {
            await _scanner.ScanAsync(settings,
                page =>
                {
                    var id = Guid.NewGuid();
                    return _sessions.NewPagePath(job.SessionId, id, existingPages + page);
                },
                async (page, path) =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    var idPart = fileName.Split('-').Last();
                    var pageId = Guid.ParseExact(idPart, "N");
                    await _sessions.AddPageAsync(job.SessionId, pageId, path);
                    job.PagesCompleted++;
                    job.CurrentPage = page;
                    job.PageProgress = 1;
                },
                (page, progress) =>
                {
                    job.CurrentPage = page;
                    job.PageProgress = progress;
                }, cancellationToken);
            job.Status = "completed";
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan job {JobId} failed", job.Id);
            job.Status = "failed";
            job.Error = ScannerErrorMapper.From(ex);
        }
        finally
        {
            job.CompletedAt = DateTimeOffset.UtcNow;
            if (_jobs.TryGetValue(job.Id, out var value)) value.Cancellation.Dispose();
        }
    }

    private sealed record JobState(ScanJob Job, CancellationTokenSource Cancellation, ScanSettings Settings);
}

internal static class ScannerErrorMapper
{
    public static ScanError From(Exception exception)
    {
        var text = $"{exception.GetType().Name} {exception.Message}".ToLowerInvariant();
        var (code, recovery, canRetry) = text switch
        {
            var value when value.Contains("jam") =>
                ("paper-jam", "Clear the paper path, square the stack, then retry. Pages already received are still in this document.", true),
            var value when value.Contains("cover") && value.Contains("open") =>
                ("cover-open", "Close the scanner cover securely, then retry.", true),
            var value when value.Contains("busy") || value.Contains("in use") =>
                ("device-busy", "Wait for the other scan to finish or close the application using the scanner, then retry.", true),
            var value when value.Contains("feeder") &&
                               (value.Contains("empty") || value.Contains("no doc") || value.Contains("paper")) =>
                ("feeder-empty", "Load paper in the feeder and adjust the guides, then retry.", true),
            var value when value.Contains("offline") || value.Contains("not found") || value.Contains("unavailable") =>
                ("device-unavailable", "Check scanner power and USB or network connectivity, then refresh the device and retry.", true),
            var value when value.Contains("denied") || value.Contains("permission") || value.Contains("access") =>
                ("device-permission", "Verify that the Docker container can access the USB bus and that no host process owns the device.", false),
            var value when value.Contains("duplex") && value.Contains("support") =>
                ("duplex-unavailable", "Choose Feeder or another supported paper source, then retry.", true),
            var value when value.Contains("communicat") || value.Contains("i/o") || value.Contains("ioexception") =>
                ("communication-failed", "Reconnect or power-cycle the scanner, wait for it to become ready, then retry.", true),
            _ => ("scan-failed", "Review the scanner status and settings, then retry. Any pages already received remain available.", true)
        };
        return new ScanError(code, exception.Message, recovery, canRetry, DateTimeOffset.UtcNow);
    }
}

internal static class DeviceKey
{
    public static string Encode(ScanDevice device) => Encode(device.Driver.ToString().ToLowerInvariant(), device.ID, device.Name);

    public static string Encode(string driver, string id, string name)
    {
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', driver, id, name)));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Driver(string key) => DecodeParts(key)[0];

    public static ScanDevice Decode(string key)
    {
        var parts = DecodeParts(key);
        if (!Enum.TryParse<Driver>(parts[0], true, out var driver) || driver == NAPS2.Scan.Driver.Default)
            throw new ArgumentException("The scanner device key has an unsupported driver.");
        return new ScanDevice(driver, parts[1], parts[2]);
    }

    private static string[] DecodeParts(string key)
    {
        try
        {
            var normalized = key.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split('\n');
            if (parts.Length != 3) throw new FormatException();
            return parts;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("The scanner device key is invalid.", nameof(key), ex);
        }
    }
}
