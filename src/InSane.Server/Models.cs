using System.Globalization;
using System.Text.Json.Serialization;

namespace InSane;

internal static class DocumentNaming
{
    public static string DefaultTitle(DateTimeOffset? timestamp = null) =>
        $"Scan {(timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}";

    public static string DefaultFileStem(DateTimeOffset? timestamp = null) =>
        $"scan-{(timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
}

public sealed class InSaneOptions
{
    public StorageOptions Storage { get; set; } = new();
    public ScannerOptions Scanner { get; set; } = new();
}

public sealed class StorageOptions
{
    public string StatePath { get; set; } = "/data/state";
    public string OutputPath { get; set; } = "/data/output";
}

public sealed class ScannerOptions
{
    public bool EnableDemo { get; set; }
    public int EsclSearchTimeoutMilliseconds { get; set; } = 5000;
    public bool PhysicalButtonEnabled { get; set; }
    public string PhysicalButtonToken { get; set; } = "";
}

public sealed record DriverSupport(
    string Driver,
    string DisplayName,
    bool Available,
    string Runtime,
    string? Reason = null);

public sealed record ScannerDevice(
    string Key,
    string Driver,
    string Id,
    string Name,
    string? ConnectionUri = null,
    bool IsDemo = false);

public sealed record SourceCapabilities(
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<string> BitDepths,
    IReadOnlyList<string> PageSizes,
    bool SupportsAutomaticPageSize,
    PageDimensions? MaximumPageSize);

public sealed record DeviceCapabilities(
    string DeviceKey,
    IReadOnlyList<string> PaperSources,
    IReadOnlyDictionary<string, SourceCapabilities> Sources,
    string? Manufacturer,
    string? Model,
    string? DriverSubtype);

public sealed record PageDimensions(decimal Width, decimal Height, string Unit);

public sealed class ScanSettings
{
    public string DeviceKey { get; set; } = "";
    public string PaperSource { get; set; } = "duplex";
    public string PageSize { get; set; } = "letter";
    public int Resolution { get; set; } = 300;
    public string BitDepth { get; set; } = "color";
    public bool AutoDeskew { get; set; } = true;
    public bool DiscardBlankPages { get; set; }
    public bool FlipDuplexBacks { get; set; }
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int BlankPageWhiteThreshold { get; set; } = 70;
    public int BlankPageCoverageThreshold { get; set; } = 15;
    public Dictionary<string, string> DriverOptions { get; set; } = new();
}

public sealed class ScanProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public string DeviceKey { get; set; } = "";
    public ScanSettings Settings { get; set; } = new();
    public bool IsDefault { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CropRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;

    [JsonIgnore]
    public bool IsFullPage => X <= 0 && Y <= 0 && Width >= 1 && Height >= 1;
}

public sealed class DocumentPage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Number { get; set; }
    [JsonIgnore]
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Rotation { get; set; }
    public CropRegion Crop { get; set; } = new();
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ImageUrl { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
}

public sealed class DocumentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = DocumentNaming.DefaultTitle();
    public string Status { get; set; } = "building";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SavedAt { get; set; }
    public string? OutputFileName { get; set; }
    public Guid? SourceSessionId { get; set; }
    public List<DocumentPage> Pages { get; set; } = [];
}

public sealed record ScanError(
    string Code,
    string Message,
    string Recovery,
    bool CanRetry,
    DateTimeOffset OccurredAt);

public sealed class ScanJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string Status { get; set; } = "queued";
    public int PagesCompleted { get; set; }
    public int? CurrentPage { get; set; }
    public double? PageProgress { get; set; }
    public ScanError? Error { get; set; }
    public int Attempt { get; set; } = 1;
    public Guid? PreviousJobId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed record CreateSessionRequest(string? Title);
public sealed record ScanSessionRequest(ScanSettings Settings);
public sealed record RotatePageRequest(int Degrees);
public sealed record RotatePagesRequest(IReadOnlyList<Guid> PageIds, int Degrees);
public sealed record CropPageRequest(double X, double Y, double Width, double Height);
public sealed record CropPagesRequest(IReadOnlyList<Guid> PageIds, double X, double Y, double Width, double Height);
public sealed record ReorderPagesRequest(IReadOnlyList<Guid> PageIds);
public sealed record SaveDocumentRequest(
    string? FileName,
    string? Title,
    string? Format = null,
    IReadOnlyList<Guid>? PageIds = null);
public sealed record SaveDocumentResult(Guid SessionId, string FileName, string DownloadUrl);
public sealed record PreparedDocumentDownload(string FilePath, string FileName, string ContentType);
public sealed record PhysicalButtonRequest(string? DeviceKey = null);
