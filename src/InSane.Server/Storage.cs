using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NAPS2.Images;
using NAPS2.Images.ImageSharp;
using NAPS2.Images.Transforms;
using NAPS2.ImportExport;
using NAPS2.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InSane;

public sealed class StoragePaths
{
    public StoragePaths(IOptions<InSaneOptions> options)
    {
        State = Path.GetFullPath(options.Value.Storage.StatePath);
        Output = Path.GetFullPath(options.Value.Storage.OutputPath);
        Sessions = Path.Combine(State, "sessions");
        Directory.CreateDirectory(Sessions);
        Directory.CreateDirectory(Output);
        VerifyWritableDirectory(State, "state");
        VerifyWritableDirectory(Output, "output");
        ValidatedAt = DateTimeOffset.UtcNow;
    }

    public string State { get; }
    public string Output { get; }
    public string Sessions { get; }
    public DateTimeOffset ValidatedAt { get; }
    public string ProfilesFile => Path.Combine(State, "profiles.json");
    public string SessionDirectory(Guid id) => Path.Combine(Sessions, id.ToString("N"));
    public string SessionFile(Guid id) => Path.Combine(SessionDirectory(id), "session.json");
    public string PageDirectory(Guid id) => Path.Combine(SessionDirectory(id), "pages");
    public string PageFile(Guid id, string fileName) => Path.Combine(PageDirectory(id), Path.GetFileName(fileName));

    private static void VerifyWritableDirectory(string path, string name)
    {
        var probe = Path.Combine(path, $".insane-{name}-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
            stream.Flush(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The configured {name} directory is not writable: {path}", ex);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch (IOException) { /* The original write result remains authoritative. */ }
        }
    }
}

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly StoragePaths _paths;
    private readonly ConcurrentDictionary<Guid, DocumentSession> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SessionStore(StoragePaths paths, ILogger<SessionStore> logger)
    {
        _paths = paths;
        foreach (var file in Directory.EnumerateFiles(paths.Sessions, "session.json", SearchOption.AllDirectories))
        {
            try
            {
                var session = JsonSerializer.Deserialize<DocumentSession>(File.ReadAllText(file), JsonOptions);
                if (session is null) continue;
                Hydrate(session);
                _sessions[session.Id] = session;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not restore session state from {File}", file);
            }
        }
    }

    public IReadOnlyList<DocumentSession> List(bool savedOnly = false) => _sessions.Values
        .Where(x => !savedOnly || x.SavedAt.HasValue)
        .OrderByDescending(x => x.UpdatedAt)
        .ToList();

    public DocumentSession? Get(Guid id) => _sessions.GetValueOrDefault(id);

    public async Task<DocumentSession> CreateAsync(string? title)
    {
        var session = new DocumentSession { Title = CleanTitle(title) };
        Directory.CreateDirectory(_paths.PageDirectory(session.Id));
        Hydrate(session);
        _sessions[session.Id] = session;
        await PersistAsync(session);
        return session;
    }

    public string NewPagePath(Guid sessionId, Guid pageId, int number) =>
        _paths.PageFile(sessionId, $"{number:D4}-{pageId:N}.jpg");

    public async Task<DocumentPage> AddPageAsync(Guid sessionId, Guid pageId, string path)
    {
        DocumentPage? page = null;
        await UpdateAsync(sessionId, session =>
        {
            page = new DocumentPage
            {
                Id = pageId,
                Number = session.Pages.Count + 1,
                FileName = Path.GetFileName(path)
            };
            session.Pages.Add(page);
        });
        return page!;
    }

    public async Task<DocumentSession?> UpdateAsync(Guid id, Action<DocumentSession> update)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(id, out var session)) return null;
            update(session);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            RenumberAndHydrate(session);
            await PersistUnsafeAsync(session);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeletePageAsync(Guid sessionId, Guid pageId)
    {
        string? removedPath = null;
        var updated = await UpdateAsync(sessionId, session =>
        {
            var page = session.Pages.FirstOrDefault(x => x.Id == pageId);
            if (page is null) return;
            removedPath = page.FilePath;
            session.Pages.Remove(page);
        });
        if (updated is null || removedPath is null) return false;
        if (File.Exists(removedPath)) File.Delete(removedPath);
        return true;
    }

    public Task<DocumentSession?> ReorderPagesAsync(Guid sessionId, IReadOnlyList<Guid> pageIds) =>
        UpdateAsync(sessionId, session =>
        {
            if (session.Status == "saved")
                throw new InvalidOperationException("A saved document cannot be reordered.");
            if (pageIds.Count != session.Pages.Count || pageIds.Distinct().Count() != pageIds.Count)
                throw new ArgumentException("The new page order must contain every page exactly once.");

            var pages = session.Pages.ToDictionary(page => page.Id);
            if (pageIds.Any(id => !pages.ContainsKey(id)))
                throw new ArgumentException("The new page order contains an unknown page.");
            session.Pages = pageIds.Select(id => pages[id]).ToList();
        });

    public async Task<DocumentSession> CreateExportRecordAsync(DocumentSession source,
        IReadOnlyList<DocumentPage> pages, string? title, string outputFileName)
    {
        await _gate.WaitAsync();
        try
        {
            var exported = new DocumentSession
            {
                Title = CleanTitle(title ?? source.Title),
                Status = "saved",
                SavedAt = DateTimeOffset.UtcNow,
                OutputFileName = outputFileName,
                SourceSessionId = source.Id
            };
            Directory.CreateDirectory(_paths.PageDirectory(exported.Id));
            for (var index = 0; index < pages.Count; index++)
            {
                var sourcePage = pages[index];
                var pageId = Guid.NewGuid();
                var path = NewPagePath(exported.Id, pageId, index + 1);
                File.Copy(sourcePage.FilePath, path);
                exported.Pages.Add(new DocumentPage
                {
                    Id = pageId,
                    Number = index + 1,
                    FileName = Path.GetFileName(path),
                    Rotation = sourcePage.Rotation,
                    Crop = new CropRegion
                    {
                        X = sourcePage.Crop.X,
                        Y = sourcePage.Crop.Y,
                        Width = sourcePage.Crop.Width,
                        Height = sourcePage.Crop.Height
                    },
                    CapturedAt = sourcePage.CapturedAt
                });
            }
            Hydrate(exported);
            _sessions[exported.Id] = exported;
            await PersistUnsafeAsync(exported);
            return exported;
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(DocumentSession session)
    {
        await _gate.WaitAsync();
        try { await PersistUnsafeAsync(session); }
        finally { _gate.Release(); }
    }

    private async Task PersistUnsafeAsync(DocumentSession session)
    {
        Directory.CreateDirectory(_paths.SessionDirectory(session.Id));
        var target = _paths.SessionFile(session.Id);
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(session, JsonOptions));
        File.Move(temporary, target, true);
    }

    private void RenumberAndHydrate(DocumentSession session)
    {
        for (var i = 0; i < session.Pages.Count; i++) session.Pages[i].Number = i + 1;
        Hydrate(session);
    }

    private void Hydrate(DocumentSession session)
    {
        foreach (var page in session.Pages)
        {
            page.FilePath = _paths.PageFile(session.Id, page.FileName);
            page.ImageUrl = $"/api/v1/sessions/{session.Id}/pages/{page.Id}/image";
            page.ThumbnailUrl = page.ImageUrl;
        }
    }

    private static string CleanTitle(string? value) =>
        string.IsNullOrWhiteSpace(value) ? $"Scan {DateTime.Now:MMM d, yyyy}" : value.Trim();
}

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly StoragePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ScanProfile> _profiles;

    public ProfileStore(StoragePaths paths, ILogger<ProfileStore> logger)
    {
        _paths = paths;
        try
        {
            _profiles = File.Exists(paths.ProfilesFile)
                ? JsonSerializer.Deserialize<List<ScanProfile>>(File.ReadAllText(paths.ProfilesFile), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore scan profiles");
            _profiles = [];
        }
    }

    public IReadOnlyList<ScanProfile> List(string? deviceKey) => _profiles
        .Where(x => string.IsNullOrWhiteSpace(deviceKey) || x.DeviceKey == deviceKey)
        .OrderBy(x => x.Name)
        .ToList();

    public ScanProfile? Get(Guid id) => _profiles.FirstOrDefault(x => x.Id == id);

    public ScanProfile? GetDefault(string? deviceKey) => _profiles
        .Where(x => x.IsDefault && (string.IsNullOrWhiteSpace(deviceKey) || x.DeviceKey == deviceKey))
        .OrderByDescending(x => x.UpdatedAt)
        .FirstOrDefault();

    public async Task<ScanProfile> SaveAsync(ScanProfile profile)
    {
        await _gate.WaitAsync();
        try
        {
            profile.Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id;
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "New profile" : profile.Name.Trim();
            profile.DeviceKey = profile.Settings.DeviceKey;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            if (profile.IsDefault)
            {
                foreach (var existing in _profiles.Where(x => x.DeviceKey == profile.DeviceKey && x.Id != profile.Id))
                    existing.IsDefault = false;
            }
            var index = _profiles.FindIndex(x => x.Id == profile.Id);
            if (index >= 0) _profiles[index] = profile; else _profiles.Add(profile);
            await AtomicJson.WriteAsync(_paths.ProfilesFile, _profiles, JsonOptions);
            return profile;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var removed = _profiles.RemoveAll(x => x.Id == id) > 0;
            if (removed) await AtomicJson.WriteAsync(_paths.ProfilesFile, _profiles, JsonOptions);
            return removed;
        }
        finally { _gate.Release(); }
    }
}

public sealed class DocumentExporter
{
    private readonly StoragePaths _paths;
    private readonly SessionStore _sessions;
    private readonly Naps2Runtime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DocumentExporter(StoragePaths paths, SessionStore sessions, Naps2Runtime runtime)
    {
        _paths = paths;
        _sessions = sessions;
        _runtime = runtime;
    }

    public async Task<SaveDocumentResult> SaveDocumentAsync(Guid sessionId, SaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var session = _sessions.Get(sessionId) ?? throw new KeyNotFoundException("Document session not found.");
        if (session.Pages.Count == 0) throw new InvalidOperationException("Scan or add at least one page before saving.");

        await _gate.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            var format = DocumentFormat.Parse(request.Format);
            var pages = ResolvePages(session, request.PageIds);
            var finalName = AvailableFileName(
                CleanFileName(request.FileName ?? request.Title ?? session.Title), format.Extension);
            var finalPath = Path.Combine(_paths.Output, finalName);
            temporary = Path.Combine(_paths.Output, $".insane-{Guid.NewGuid():N}.partial");
            await ExportDocumentAsync(session, pages, request.Title, temporary, format, cancellationToken);
            SetSharedOutputPermissions(temporary);
            File.Move(temporary, finalPath);
            temporary = null;

            Guid savedSessionId;
            if (pages.Count == session.Pages.Count)
            {
                await _sessions.UpdateAsync(sessionId, value =>
                {
                    value.Title = string.IsNullOrWhiteSpace(request.Title) ? value.Title : request.Title.Trim();
                    value.Status = "saved";
                    value.SavedAt = DateTimeOffset.UtcNow;
                    value.OutputFileName = finalName;
                });
                savedSessionId = sessionId;
            }
            else
            {
                var exported = await _sessions.CreateExportRecordAsync(session, pages, request.Title, finalName);
                savedSessionId = exported.Id;
            }
            return new SaveDocumentResult(savedSessionId, finalName, $"/api/v1/documents/{Uri.EscapeDataString(finalName)}");
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    public async Task<PreparedDocumentDownload> PrepareDownloadAsync(Guid sessionId,
        SaveDocumentRequest request, CancellationToken cancellationToken)
    {
        var session = _sessions.Get(sessionId) ?? throw new KeyNotFoundException("Document session not found.");
        if (session.Pages.Count == 0) throw new InvalidOperationException("Scan or add at least one page before downloading.");

        await _gate.WaitAsync(cancellationToken);
        var format = DocumentFormat.Parse(request.Format);
        var pages = ResolvePages(session, request.PageIds);
        var temporary = Path.Combine(_paths.State, $".insane-download-{Guid.NewGuid():N}{format.Extension}");
        try
        {
            await ExportDocumentAsync(session, pages, request.Title, temporary, format, cancellationToken);
            var fileName = CleanFileName(request.FileName ?? request.Title ?? session.Title) + format.Extension;
            return new PreparedDocumentDownload(temporary, fileName, format.ContentType);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<DocumentPage> ResolvePages(DocumentSession session, IReadOnlyList<Guid>? pageIds)
    {
        if (pageIds is null) return session.Pages;
        if (pageIds.Count == 0) throw new ArgumentException("Select at least one page to export.");
        if (pageIds.Distinct().Count() != pageIds.Count)
            throw new ArgumentException("The page selection contains duplicates.");
        var selected = pageIds.ToHashSet();
        if (selected.Any(id => session.Pages.All(page => page.Id != id)))
            throw new ArgumentException("The page selection contains a page outside this document.");
        return session.Pages.Where(page => selected.Contains(page.Id)).ToList();
    }

    private async Task ExportDocumentAsync(DocumentSession session, IReadOnlyList<DocumentPage> pages,
        string? title, string path,
        DocumentFormat format, CancellationToken cancellationToken)
    {
        var images = new List<ProcessedImage>();
        try
        {
            var importer = new ImageImporter(_runtime.Context);
            foreach (var page in pages)
            {
                ProcessedImage? imported = null;
                await foreach (var image in importer.Import(page.FilePath).WithCancellation(cancellationToken))
                {
                    imported = image;
                    break;
                }
                if (imported is null) throw new InvalidOperationException($"Could not import page {page.Number}.");

                if (!page.Crop.IsFullPage)
                {
                    using var rendered = imported.Render();
                    var left = (int)Math.Round(rendered.Width * page.Crop.X);
                    var top = (int)Math.Round(rendered.Height * page.Crop.Y);
                    var right = (int)Math.Round(rendered.Width * (1 - page.Crop.X - page.Crop.Width));
                    var bottom = (int)Math.Round(rendered.Height * (1 - page.Crop.Y - page.Crop.Height));
                    imported = imported.WithTransform(new CropTransform(
                        Math.Max(0, left), Math.Max(0, right), Math.Max(0, top), Math.Max(0, bottom),
                        rendered.Width, rendered.Height), true);
                }
                if (page.Rotation != 0)
                {
                    imported = imported.WithTransform(new RotationTransform(page.Rotation), true);
                }
                images.Add(imported);
            }

            if (format.Key == "tiff")
            {
                ExportTiff(path, images);
            }
            else if (format.Key is "zip-png" or "zip-jpeg")
            {
                await ExportImageZipAsync(path, images, format.Key, cancellationToken);
            }
            else
            {
                var exporter = new PdfExporter(_runtime.Context);
                var ok = await exporter.Export(path, images, new PdfExportParams
                {
                    Metadata = new PdfMetadata
                    {
                        Title = title ?? session.Title,
                        Creator = "inSANE via NAPS2.Sdk"
                    }
                });
                if (!ok) throw new InvalidOperationException("NAPS2 could not create the PDF.");
            }
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    private static void ExportTiff(string path, IReadOnlyList<ProcessedImage> images)
    {
        var pages = new List<Image<Rgba32>>();
        Image<Rgba32>? document = null;
        try
        {
            foreach (var image in images)
            {
                using var rendered = image.Render();
                if (rendered is not ImageSharpImage imageSharp)
                    throw new InvalidOperationException("The configured image runtime cannot create TIFF documents.");

                pages.Add(imageSharp.Image.CloneAs<Rgba32>());
            }

            if (pages.Count == 0) throw new InvalidOperationException("No pages were available for TIFF export.");

            // ImageSharp requires every frame in one image to share a canvas size. Cropping and
            // rotation can legitimately leave selected pages with different dimensions, so place
            // each page on the smallest common white canvas instead of failing the whole export.
            var width = pages.Max(page => page.Width);
            var height = pages.Max(page => page.Height);
            foreach (var page in pages)
            {
                using var canvas = new Image<Rgba32>(width, height, Color.White);
                var location = new Point((width - page.Width) / 2, (height - page.Height) / 2);
                canvas.Mutate(context => context.DrawImage(page, location, 1f));

                if (document is null)
                {
                    document = canvas.Clone();
                }
                else
                {
                    document.Frames.AddFrame(canvas.Frames.RootFrame);
                }
            }

            document!.Save(path, new TiffEncoder());
        }
        finally
        {
            document?.Dispose();
            foreach (var page in pages) page.Dispose();
        }
    }

    private static async Task ExportImageZipAsync(string path, IReadOnlyList<ProcessedImage> images,
        string format, CancellationToken cancellationToken)
    {
        var imageExtension = format == "zip-png" ? ".png" : ".jpg";
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 65536, useAsync: true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        for (var index = 0; index < images.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rendered = images[index].Render();
            if (rendered is not ImageSharpImage imageSharp)
                throw new InvalidOperationException("The configured image runtime cannot create ZIP image archives.");

            var entry = archive.CreateEntry($"page-{index + 1:D4}{imageExtension}", CompressionLevel.NoCompression);
            await using var entryStream = entry.Open();
            if (format == "zip-png")
            {
                await imageSharp.Image.SaveAsync(entryStream, new PngEncoder(), cancellationToken);
            }
            else
            {
                await imageSharp.Image.SaveAsync(entryStream, new JpegEncoder { Quality = 90 }, cancellationToken);
            }
        }
    }

    public string? ResolveOutput(string fileName)
    {
        var clean = Path.GetFileName(fileName);
        var path = Path.Combine(_paths.Output, clean);
        return clean == fileName && File.Exists(path) ? path : null;
    }

    private string AvailableFileName(string stem, string extension)
    {
        var candidate = stem + extension;
        for (var i = 2; File.Exists(Path.Combine(_paths.Output, candidate)); i++)
            candidate = $"{stem}-{i}{extension}";
        return candidate;
    }

    private static string CleanFileName(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value.Trim());
        foreach (var character in Path.GetInvalidFileNameChars()) stem = stem.Replace(character, '-');
        stem = string.Join('-', stem.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(stem) ? $"scan-{DateTime.Now:yyyy-MM-dd-HHmm}" : stem;
    }

    private static void SetSharedOutputPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead);
    }

    private sealed record DocumentFormat(string Key, string Extension, string ContentType)
    {
        public static DocumentFormat Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "pdf" => new("pdf", ".pdf", "application/pdf"),
            "tif" or "tiff" => new("tiff", ".tiff", "image/tiff"),
            "zip-png" => new("zip-png", ".zip", "application/zip"),
            "zip-jpg" or "zip-jpeg" => new("zip-jpeg", ".zip", "application/zip"),
            _ => throw new ArgumentException("Format must be PDF, TIFF, ZIP with PNG pages, or ZIP with JPEG pages.")
        };
    }
}

internal static class AtomicJson
{
    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, options));
        File.Move(temporary, path, true);
    }
}
