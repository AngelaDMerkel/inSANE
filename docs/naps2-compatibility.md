# NAPS2 compatibility and driver plan

Date: 2026-08-19

## Compatibility boundary

inSANE references `NAPS2.Sdk` and `NAPS2.Images.ImageSharp` version 1.3.0. It
creates a standard `ScanningContext`, discovers devices and per-source
capabilities with `ScanController`, receives `ProcessedImage` pages from the
normal NAPS2 acquisition loop, applies NAPS2 transforms, and completes PDFs
with `PdfExporter`.

No NAPS2 implementation files are copied into this repository. The adapter in
`Scanning.cs` is intentionally small so an upstream update normally consists
of a package bump plus adapter changes required by the public SDK surface.

The web contract is not a serialized `ScanOptions` object. It presents stable
product concepts and translates them at the boundary:

| inSANE concept | NAPS2 type |
| --- | --- |
| Device | `ScanDevice` |
| Driver | `Driver` |
| Flatbed / Feeder / Duplex | `PaperSource` |
| Colour / Greyscale / B&W | `BitDepth` |
| Letter / Legal / A4 | `PageSize` |
| Source-dependent controls | `ScanCaps` and `PerSourceCaps` |
| Vendor-specific SANE controls | `KeyValueScanOptions` |
| Acquired page | `ProcessedImage` |
| Crop / rotation | `CropTransform` / `RotationTransform` |
| Completed document | `PdfExporter` |

That separation prevents an upstream SDK change from leaking through every UI
component while keeping NAPS2's scanner semantics intact.

## Driver coverage

NAPS2's drivers are native-platform integrations. Docker cannot make a Windows
or macOS scanner API available inside a Linux container, so comprehensive
support is split by runtime rather than emulated poorly.

| Driver | TrueNAS Linux container | Future native worker | Notes |
| --- | --- | --- | --- |
| SANE | Implemented | Linux/macOS | Primary direct-USB path for the ES-400 II |
| eSCL / AirScan | Implemented | All platforms | Network scanners; uses NAPS2's direct eSCL driver |
| WIA | Represented, unavailable | Windows | Native Windows service required |
| TWAIN | Represented, unavailable | Windows | A 64-bit host usually also needs `NAPS2.Sdk.Worker.Win32` for 32-bit data sources |
| Apple Image Capture | Represented, unavailable | macOS | Requires a macOS-targeted worker build |

The `/api/v1/drivers` endpoint reports all five families and explains runtime
availability. Device keys preserve the NAPS2 driver identity, so a later worker
does not require a new document or profile model.

The next breadth milestone is a remote scanner-worker protocol that implements
the same scanner adapter on Windows and macOS. The TrueNAS service will remain
the document-session owner while a selected worker performs native capability
discovery and acquisition. This is the practical route to NAPS2-level coverage
without forcing native desktop driver libraries into Linux.

## Upstream upgrade procedure

1. Read the NAPS2.Sdk release notes and compare public changes in
   `ScanController`, `ScanOptions`, `ScanCaps`, image transforms, import, and PDF
   export.
2. Change both NAPS2 package versions together in `Directory.Packages.props`.
3. Build the container for the TrueNAS architecture.
4. Run the demonstration workflow: scan pages, rotate, crop, save, restart, and
   confirm session/history recovery.
5. Run the ES-400 II acceptance suite for simplex and hardware-duplex Letter.
6. Record any adapter workaround with the exact upstream issue or release that
   makes it necessary.

Avoid reflection over NAPS2 internals and avoid copying internal SANE bridge
classes. If the public SDK lacks a necessary capability, propose it upstream
first; that preserves the ability to port later improvements cleanly.

## Deliberate first-version limits

- OCR is not performed for the Paperless path. Paperless should own OCR.
- Interactive native-driver dialogs are not suitable for a headless browser
  service and are disabled.
- Authentication, multi-user ownership, remote native workers, page reordering,
  import, splitting, and barcode routing are subsequent milestones.
- Crop is stored as an original-page normalized rectangle. Rotation and crop are
  non-destructive until PDF completion.
