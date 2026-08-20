# scanservjs + NAPS2 architecture and feasibility

Date: 2026-08-18

> Implementation update, 2026-08-19: option C is now represented by the first
> runnable implementation in this repository. The service uses NAPS2.Sdk 1.3.0,
> persists document sessions under `/data/state`, publishes completed PDFs to
> `/data/output`, and serves the Session Canvas web interface. The remaining
> conclusions in this document record the evidence and design rationale.

## Executive decision

Combining ideas from the projects is feasible, but scanservjs should not be the
functional or technical foundation. NAPS2 is a cross-platform C# desktop
application with a reusable SDK; scanservjs is a Node.js wrapper around the
`scanimage` CLI. The new product should draw its scanner behavior, capability
model, processing pipeline, and document concepts primarily from NAPS2.Sdk.
scanservjs remains useful as evidence about the current deployment and about
failure modes to avoid.

The revised product architecture is:

1. Build a headless .NET service directly on pinned NAPS2.Sdk packages.
2. Make NAPS2's device, per-source capability, acquisition, page-processing, and
   export models the primary backend behavior.
3. Expose a new versioned scan-job and document-session API; do not preserve
   scanservjs's request model as the long-term contract.
4. Prove the service against the physical ES-400 II and its installed SANE
   backend.
5. Build a new desktop-focused browser UI around persistent document sessions,
   page accrual, editing, saving, and history.
6. Retain raw `scanimage` only as a diagnostic tool and optional emergency
   fallback.

This establishes one coherent backend instead of extending scanservjs and later
replacing it again.

## What the hardware and drivers can support

Epson specifies the ES-400 II as a 50-sheet, one-pass duplex ADF scanner capable
of 35 pages or 70 images per minute, with a maximum width of 8.5 inches. The
scanner's USB identity is `04b8:0181`.

SANE lists the ES-400 II as having **Complete** support through the `epsonds`
backend. Support was added in SANE Backends 1.1.1. This means ADF and duplex
failure should not be assumed to be a hardware or fundamental Linux-driver
limitation. It first needs to be isolated among:

- the SANE version and backend actually loaded in the running image;
- raw `scanimage` behavior;
- scanservjs capability parsing and command construction;
- container device access and changing USB bus addresses.

NAPS2 on Linux also uses system SANE for this USB scanner. NAPS2 therefore cannot
repair a failure below SANE. Its value is in how it queries dynamic SANE options,
normalizes scanner capabilities, performs acquisition, reports errors, and
post-processes pages.

Primary references:

- [Epson ES-400 II product page](https://support.epson.com/p/B11B261201)
- [SANE supported devices](https://www.sane-project.org/lists/sane-mfgs-cvs.html)
- [SANE Backends 1.1.1 release](https://gitlab.com/sane-project/backends/-/releases/1.1.1)
- [Current epsonds backend](https://gitlab.com/sane-project/backends/-/raw/master/backend/epsonds.c)

## Findings in scanservjs

These are source-level findings from the current upstream `master` branch, not
guesses based on the UI.

### 1. The US Letter preset is inaccurate

scanservjs defines Letter as `216 × 279 mm`. Exact Letter is
`215.9 × 279.4 mm`. Its UI removes a paper preset when either preset dimension
is greater than the device's reported maximum. A device that correctly reports
an 8.5-inch maximum (`215.9 mm`) therefore loses the `216 mm` Letter preset.

NAPS2 represents Letter as `8.5 × 11 in` and converts it to exact millimetres.
This is a direct explanation for Letter being unavailable or constrained
incorrectly.

### 2. Capability discovery is a static snapshot of dynamic SANE state

scanservjs runs `scanimage -A` once, parses its text output, and drops options
whose current value is `inactive`. SANE options are dynamic: selecting Flatbed,
Feeder, or Duplex can activate different geometry, resolution, ADF, and
enhancement options. An option inactive under the initial source is lost before
the user selects another source.

NAPS2 opens the device, selects each logical source, and collects separate
Flatbed, Feeder, and Duplex capabilities. That behavior has substantial value
for this project.

### 3. Duplex is exposed as backend syntax instead of a product capability

Different SANE backends describe duplex in several ways:

- a source string such as `ADF Duplex`;
- an `adf-mode` or `adf_mode` option;
- a standalone Boolean `duplex` option.

scanservjs handles the first two forms but does not model or send the Boolean
`duplex` option. NAPS2 deliberately tries all of these forms and contains tests
for Epson's `epsonscan2` backend. A stable API should expose one logical value,
`paperSource: duplex`, and keep backend syntax private.

### 4. Paper size settings can disagree

scanservjs has two sets of geometry values:

- scan area (`left`, `top`, `width`, `height`);
- media size (`page-width`, `page-height`).

Choosing a paper preset updates only scan-area width and height. The media-size
values can remain at backend defaults even though some ADF backends use them for
centering, paper-end detection, or double-feed detection. NAPS2 sets media size
and all four scan-area bounds as one operation and clamps to device limits.

### 5. The batch model does not clearly distinguish hardware duplex

scanservjs offers raw source selection plus batch modes named `auto` and
`auto-collate-*`. The collation modes are two-pass workflows for scanning fronts
and backs separately; they are not required for a one-pass duplex scanner. The
UI can therefore allow a valid but incorrect combination.

The product model should make `Duplex ADF` a single acquisition mode. One sheet
must yield front then back, and an ADF stack must end normally when SANE reports
`NO_DOCS`.

### 6. Scanner errors and progress are weakly typed

scanservjs primarily observes a spawned process. NAPS2 maps SANE statuses to
specific conditions including empty feeder, busy device, paper jam, open cover,
offline device, and communication failure. It also exposes page start and page
progress events and supports cancellation. These are valuable even before the UI
is redesigned because they make the API testable and operationally diagnosable.

### 7. Current Docker volume and identity settings do not match upstream

Current scanservjs documentation uses:

- `/etc/scanservjs` for configuration overrides;
- `/var/lib/scanservjs/output` for completed output.

The supplied compose maps `/app/config` and `/app/data/output`, which do not
match the current upstream image. Upstream scanservjs also does not consume
`PUID` or `PGID`; those environment variables are ignored. The current image
runs as root unless its alternate build target or an explicit container user is
used.

`privileged: true` and a single `/dev/bus/usb/002/002` mapping should not be used
together. The numeric USB device address can change after a reconnect, sleep,
or reboot. The prototype can use privileged access alone. The hardened design
should mount `/dev/bus/usb`, allow USB character-device major 189, and arrange
permissions explicitly.

Relevant scanservjs sources:

- [feature parser](https://github.com/sbs20/scanservjs/blob/master/app-server/src/classes/feature.js)
- [device parser](https://github.com/sbs20/scanservjs/blob/master/app-server/src/classes/device.js)
- [request normalization](https://github.com/sbs20/scanservjs/blob/master/app-server/src/classes/request.js)
- [scanimage command construction](https://github.com/sbs20/scanservjs/blob/master/app-server/src/classes/scanimage-command.js)
- [paper presets](https://github.com/sbs20/scanservjs/blob/master/app-server/src/classes/config.js)
- [scan UI](https://github.com/sbs20/scanservjs/blob/master/app-ui/src/components/Scan.vue)
- [Docker documentation](https://github.com/sbs20/scanservjs/blob/master/docs/02-docker.md)

## What to reuse from NAPS2

### High value: include in the scanner engine

| Capability | Why it matters here |
| --- | --- |
| Direct SANE capability access | Avoids parsing human-formatted `scanimage -A` output and preserves dynamic option state. |
| Logical Flatbed/Feeder/Duplex mapping | Handles source strings, ADF modes, and Boolean duplex consistently. |
| Capabilities per source | Prevents flatbed defaults from hiding ADF geometry and resolutions. |
| Exact, unit-aware page sizes | Fixes Letter and makes custom receipt/card/long-page dimensions reliable. |
| Source-aware feeder loop | Treats ADF exhaustion as successful completion and preserves duplex page order. |
| Structured errors, progress, cancellation | Required for reliable operation and for the future UI. |
| Blank-page removal | Useful for duplex batches with blank backs, with configurable thresholds. |
| Rotation, back-page flip, deskew, crop/stretch | Directly improves document correctness before Paperless ingestion. |
| Generic SANE key/value options | Allows double-feed, paper protection, hardware crop, and scanner-specific controls without new API fields for every backend. |
| Barcode/Patch-T detection | Valuable later for document splitting and routing. |
| Thumbnails | Useful for progress and eventual page organization without transferring full images. |
| PDF metadata, encryption, and PDF/A | Valuable for direct-download workflows; optional for Paperless. |

References:

- [NAPS2.Sdk overview](https://github.com/cyanfish/naps2/tree/master/NAPS2.Sdk)
- [NAPS2 SANE driver](https://github.com/cyanfish/naps2/blob/master/NAPS2.Sdk/Scan/Internal/Sane/SaneScanDriver.cs)
- [NAPS2 scan options](https://github.com/cyanfish/naps2/blob/master/NAPS2.Sdk/Scan/ScanOptions.cs)
- [NAPS2 post-processing](https://github.com/cyanfish/naps2/blob/master/NAPS2.Sdk/Scan/Internal/RemotePostProcessor.cs)
- [NAPS2 PDF compatibility modes](https://github.com/cyanfish/naps2/blob/master/NAPS2.Sdk/Pdf/PdfCompat.cs)

### Already covered or better left in Paperless

- OCR should be disabled by default for Paperless-bound output. Paperless-ngx
  already performs OCR and archival document generation; doing it twice adds
  latency and may reduce quality.
- Cloud upload, email, and file organization are not scanner-engine concerns in
  this deployment.
- Printing is out of scope.
- NAPS2's desktop page organizer, import UI, native-driver dialogs, and desktop
  preferences should not be ported into the current scanservjs UI.
- WIA and TWAIN are not available inside a Linux TrueNAS container. They should
  remain outside the server feature contract unless a future Windows worker is
  explicitly added.
- NAPS2 scanner sharing via eSCL is useful for sharing a scanner to arbitrary
  clients, but routing scanservjs through an eSCL server would add another
  translation layer while retaining scanservjs's parsing limitations. It is not
  the primary integration path.

## Architecture options considered

### A. Patch scanservjs and continue using `scanimage`

This could correct the immediate Letter and duplex defects, but it would keep
scanner behavior tied to human-formatted CLI text and turn NAPS2 feature parity
into a reimplementation project. It is now limited to diagnostics or an
emergency fallback, not the product path.

### B. Call the NAPS2 desktop CLI

The NAPS2 CLI already accepts `--source duplex`, `--pagesize letter`, DPI,
bit-depth, deskew, and output options. It is useful as a hardware spike, but it
is not a strong service boundary: capability discovery, job progress,
cancellation, error typing, profile state, and forward compatibility would
remain coupled to command output.

### C. Headless worker built on NAPS2.Sdk — recommended

Create an ASP.NET Core service that references pinned NAPS2.Sdk packages. It
owns the scanner, processing pipeline, document-session state, export, and
history. A new browser client uses its narrow, versioned contract directly.

This reuses NAPS2's tested scanner behavior and document functionality without
importing the NAPS2 desktop UI. It also avoids maintaining a transitional Node
backend that would be removed later.

The worker should use a headless image implementation. Dependency and license
notices must be generated during the image build; in particular, the selected
NAPS2 image package and its transitive image library require an explicit review
before distribution.

### D. NAPS2 scanner-sharing server in front of scanservjs

Not recommended as the main path. It would translate SANE to eSCL and then back
through a SANE eSCL backend, add discovery and networking failure modes, and
still leave scanservjs's capability handling in place.

## Proposed internal API

The initial API should model product concepts, not SANE option spellings.

```text
GET    /v1/devices
GET    /v1/devices/{deviceId}/capabilities
POST   /v1/scan-jobs
GET    /v1/scan-jobs/{jobId}
GET    /v1/scan-jobs/{jobId}/events
DELETE /v1/scan-jobs/{jobId}
```

Example job request:

```json
{
  "deviceId": "epsonds:libusb:002:002",
  "source": "duplex",
  "pageSize": { "width": 8.5, "height": 11, "unit": "in" },
  "dpi": 300,
  "colorMode": "color",
  "processing": {
    "deskew": true,
    "discardBlankPages": true,
    "flipDuplexBacks": false
  },
  "output": {
    "format": "pdf",
    "destination": "paperless"
  },
  "driverOptions": {
    "adf-skew": "yes",
    "adf-crp": "yes"
  }
}
```

The engine should return source-specific capabilities, resolved settings, a page
count, front/back metadata, structured warnings, and a final output artifact.
The Node server can translate the existing scanservjs request and response shape
to this contract during the compatibility phase.

## Container layout

Use two services during development:

- `web`: the new static/browser client; no hardware privilege;
- `scanner-engine`: ASP.NET Core + NAPS2.Sdk; exclusive USB access and ownership
  of document sessions, history, and output.

Only `scanner-engine` should open the USB device. Both services may share a
staging/output volume, but the Paperless consume directory must receive a file
only after the document is complete. Build in a staging directory on the same
filesystem and perform a final atomic rename into `consume`; do not let
Paperless observe a partially copied PDF.

For the initial TrueNAS proof, privileged scanner-engine access is acceptable.
The hardened target is:

- `/dev/bus/usb:/dev/bus/usb` mounted into scanner-engine;
- character-device major `189` allowed;
- an explicit scanner device group or udev permissions;
- no host USB access in the web container;
- a pinned image version or digest, never an unqualified `latest` in production;
- a health check that validates the engine, not one that moves paper.

## Licensing

scanservjs declares GPL-2.0. The NAPS2 application is GPL-2.0-or-later, while
NAPS2.Sdk, NAPS2.Images, NAPS2.Escl, and NAPS2.Internals are LGPL-2.1-or-later.
These licenses do not present an obvious blocker to a GPLv2 project.

The preferred integration is to reference unmodified, pinned NAPS2 SDK packages
from the separately built worker and preserve all notices and corresponding
source/relinking obligations. Copying arbitrary code from the full NAPS2 desktop
application should be avoided unless the file's license and provenance are
checked. This is an engineering assessment, not legal advice.

Primary license references:

- [scanservjs license](https://github.com/sbs20/scanservjs/blob/master/LICENSE)
- [NAPS2 license summary](https://github.com/cyanfish/naps2#license)
- [NAPS2.Sdk license](https://github.com/cyanfish/naps2/blob/master/NAPS2.Sdk/LICENSE)

## Delivery phases and exit criteria

### Phase 0 — establish the hardware truth

Run the validation plan in `es400ii-validation.md` inside the current container.
Capture command output as versioned fixtures with serial numbers and host paths
redacted.

Exit: raw SANE produces a correctly ordered, two-sided, exact Letter scan from a
stack and terminates cleanly at an empty feeder.

If this fails, work stays at the driver/container layer. Neither a scanservjs
patch nor NAPS2.Sdk should be blamed until the exact same installed SANE backend
has been tested through both acquisition paths.

### Phase 1 — build the NAPS2.Sdk scanner core

- implement device discovery and source-specific capabilities with NAPS2.Sdk;
- expose exact, unit-aware page sizes and custom dimensions;
- support Flatbed, Feeder, and Duplex through NAPS2's normalized model;
- implement scan jobs, page events, progress, cancellation, and structured
  scanner errors;
- preserve raw SANE option snapshots for diagnostics;
- correct the TrueNAS USB, configuration, and output mounts;
- add ES-400 II capability fixtures and hardware acceptance tests.

Exit: the API completes 20 consecutive 10-sheet, 20-page Letter scans with
correct order, dimensions, progress events, and recovery.

### Phase 2 — document-session backend

- create persistent draft documents to which scan jobs append pages;
- stream page thumbnails to the browser as pages complete;
- store page order, rotation, and non-destructive crop metadata;
- support rescan, delete, duplicate, and drag reorder;
- separate `Save document` from `Scan`, so the user decides when a draft is
  complete;
- create immutable saved-document history with reopen/re-export actions.

Exit: a user can accrue multiple scan runs into one draft, edit its pages, save
it, and retrieve it from history without losing the original page images.

### Phase 3 — valuable document processing

- blank-back removal with auditable thresholds;
- deskew and back-page rotation;
- safe multipage PDF generation;
- atomic Paperless delivery;
- optional barcode/Patch-T detection and splitting;
- direct-download OCR/PDF-A options, disabled by default for Paperless.

Exit: end-to-end Paperless ingestion succeeds without partial files, duplicate
OCR, missing pages, or reordered duplex sides.

### Phase 4 — operational hardening and migration

Run the NAPS2.Sdk service through scanner reconnects, container restarts,
TrueNAS updates, incomplete jobs, and output recovery. Migrate away from the
scanservjs container after the new service meets the acceptance suite. Keep raw
`scanimage` commands in diagnostics, not as a parallel product backend.

### Phase 5 — implement the selected desktop UI

Build the selected desktop direction against the stable job and document-session
API. Device switching, per-device profiles, paper source, page size, resolution,
bit depth, live page browsing, rotation, cropping, save control, and document
history are first-class requirements rather than additions to scanservjs.

## Principal risks

| Risk | Mitigation |
| --- | --- |
| NAPS2 uses the same broken system SANE path | Prove raw SANE first and compare providers against the same backend/version. |
| Dynamic SANE options vary by device state | Query capabilities per source and retain raw diagnostic option snapshots. |
| USB address changes | Pass the USB bus/cgroup rather than one ephemeral bus/device node. |
| Two processes contend for the scanner | Give scanner-engine exclusive hardware ownership and serialize jobs. |
| Paperless ingests incomplete files | Stage on the destination filesystem and atomically rename only after success. |
| SDK or dependency update changes behavior | Pin versions; store capability fixtures and golden scan metadata; run hardware acceptance before upgrades. |
| Image processing removes real content | Keep original pages through validation; make blank removal configurable and report discarded page numbers. |
| Scope expands into an entire desktop clone | Use the value matrix above and keep UI work behind the scanner-engine acceptance gate. |

## Conclusion

The scanner should be capable of the requested ADF, duplex, and US Letter
workflow on Linux. scanservjs's static, lossy SANE capability model and its
inaccurate Letter preset explain why the current experience falls short, but
they do not justify carrying scanservjs forward as the product foundation.

NAPS2.Sdk should be the primary backend: its SANE normalization,
source-specific capabilities, feeder loop, structured errors, exact page sizes,
post-processing, and export functionality align with the intended product. The
new browser UI should be built around that backend's job and document-session
model rather than around scanservjs concepts.
