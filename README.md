<img src="src/InSane.Server/wwwroot/brand/insane-lockup.svg" alt="inSANE" width="420">

inSANE is a desktop-focused web document builder for scanners attached to a
headless server. Its first target is an Epson WorkForce ES-400 II connected by
USB to TrueNAS SCALE, with completed PDFs delivered directly to a bind-mounted
Paperless-ngx consume directory.

The scanner and document backend is built directly on pinned
[NAPS2.Sdk](https://github.com/cyanfish/naps2/tree/master/NAPS2.Sdk) packages.
scanservjs is useful prior art for Docker deployment and SANE diagnostics, but
it is not the product or API foundation.

## What is implemented

- NAPS2-backed SANE and eSCL/AirScan device discovery in the Linux container.
- Source-aware capabilities for flatbed, feeder, and hardware duplex scanning.
- Exact NAPS2 page sizes, including US Letter at 8.5 x 11 inches.
- Full per-device profile management: create, rename, update, duplicate,
  delete, apply, and choose a physical-button default.
- Advanced NAPS2 scan controls for brightness, contrast, blank-page white
  threshold, and blank-page coverage threshold.
- Asynchronous scan jobs whose pages accrue into persistent document sessions.
- A live page filmstrip, selected-page canvas, 90-degree rotation, normalized
  crop, persisted drag-to-reorder, page removal, PDF completion, direct browser
  download, and document history.
- Atomic PDF and multi-page TIFF publication: the document is fully exported
  within `/data/output` before it receives its final file name.
- ZIP export with one processed PNG or JPEG per page, preserving document order,
  rotation, and crop settings for both bind-mount saves and browser downloads.
- The browser does not expose an output-folder picker. Server saves always use
  the configured `/data/output` bind mount; browser downloads return a
  temporary PDF, TIFF, or ZIP copy directly to the current browser instead.
- Shift-click page-range selection for non-destructive document splitting.
  Bind-mount saves create a separate history record for the selected pages and
  leave the source session open for the next split.
- Structured scan failures with recovery guidance and one-click retry; pages
  received before an interruption remain in the current document.
- A token-protected scanner-button action compatible with a scanbd/scanbm
  Docker bridge; see
  [`docs/physical-scanner-buttons.md`](docs/physical-scanner-buttons.md).
- Persistent state under `/data/state` and completed documents under
  `/data/output`, both intended to be bind-mounted.
- A TrueNAS-aware entrypoint repairs ownership when required, then runs the
  inSANE/NAPS2 process as the standard Apps identity (`568:568`). USB device
  groups are supplemental; the application never remains UID 0.
- An optional demonstration scanner for exercising the complete workflow
  without hardware.

## Try it locally

Docker is the only prerequisite:

```sh
docker compose -f compose.demo.yaml up --build
```

Open <http://localhost:51234>. Demo state and PDFs are written to `./data`,
which is git-ignored.

## Keyboard shortcuts

Use Command on macOS or Ctrl on Windows and Linux.

| Shortcut | Action |
| --- | --- |
| `Cmd/Ctrl + N` | Start a new document |
| `Cmd/Ctrl + S` | Save to the configured output mount |
| `Cmd/Ctrl + Shift + S` | Download the current document |
| `Cmd/Ctrl + Enter` | Start or cancel scanning |
| `Cmd/Ctrl + A` | Select every page |
| `Cmd/Ctrl +` / `Cmd/Ctrl -` | Zoom the document canvas in or out |
| `Left` / `Right` | Move between pages |
| `Shift + Left` / `Shift + Right` | Extend the page selection |
| `[` / `]` | Rotate the current page left or right |
| `C` | Open or close crop mode |
| `Enter` | Apply the active crop |
| `Escape` | Cancel crop mode or clear the page selection |
| `Backspace` / `Delete` | Remove the current page |

## TrueNAS and Paperless deployment

Use [`compose.paperless-truenas.yaml`](compose.paperless-truenas.yaml) to run
inSANE with the supplied Paperless-ngx services. It preserves the existing
Paperless database credential supplied for this deployment and fixes both
shared-directory consumers to Apps UID/GID `568`. Follow the
[TrueNAS deployment runbook](docs/truenas-paperless-deployment.md).

The integrated compose maps:

| TrueNAS host path | Container path | Purpose |
| --- | --- | --- |
| `/mnt/Array/databases/insane` | `/data/state` | Sessions, page images, profiles, and recovery state |
| `/mnt/Array/databases/paperless/consume` | `/data/output` | Completed PDFs published for Paperless ingestion |

Paperless mounts that same host directory at `/usr/src/paperless/consume` and
uses a five-second polling interval so ingestion does not depend on Docker/ZFS
filesystem notifications. `scripts/verify-truenas-stack.sh` proves that inSANE
can write the bind mount and that Paperless's mapped UID/GID can read and remove
the same probe file. Define a unique, stable `PAPERLESS_SECRET_KEY` in
Portainer's stack environment before deployment; current Paperless releases
refuse to start with the old default.

The initial USB proof uses `privileged: true`, which makes stable access
possible even when the scanner's `/dev/bus/usb/BBB/DDD` address changes after a
reconnect. Restrict the web port to a trusted LAN or put it behind an
authenticated reverse proxy; this first implementation does not include user
authentication.

Do not add a single USB device mapping such as `/dev/bus/usb/002/002`: that
address is ephemeral. The hardening phase will replace broad privilege with a
full USB bus mapping, cgroup rule for character-device major 189, and explicit
group permissions where the TrueNAS Docker runtime permits them.

## NAPS2 compatibility policy

- `NAPS2.Sdk` and `NAPS2.Images.ImageSharp` are consumed as NuGet packages and
  pinned together in [`Directory.Packages.props`](Directory.Packages.props).
- NAPS2 source is not copied or rewritten. Scanner-specific behavior remains
  behind `Naps2ScannerBackend`; the web API uses product concepts instead of
  SANE option spellings.
- The API retains NAPS2's driver names: SANE, eSCL, WIA, TWAIN, and Apple Image
  Capture. A Linux TrueNAS container can run SANE and eSCL. The native drivers
  require future Windows or macOS workers because those operating-system APIs
  cannot execute inside Linux.
- Generic NAPS2/SANE key-value options flow through scan profiles so device
  features can be enabled without growing a new API field for every backend.
- The dependency upgrade procedure is documented in
  [`docs/naps2-compatibility.md`](docs/naps2-compatibility.md).

## Repository map

```text
src/InSane.Server/
  Program.cs       Versioned HTTP API and static web host
  Scanning.cs      NAPS2 adapter, driver catalog, demo backend, scan jobs
  Storage.cs       Session/profile persistence and atomic NAPS2 PDF export
  Models.cs        Stable product and API models
  wwwroot/         Session Canvas browser interface
compose.yaml       TrueNAS-oriented deployment
compose.paperless-truenas.yaml  Integrated production stack
compose.demo.yaml  Hardware-free local workflow
scripts/           Release, ingestion, and on-NAS verification gates
tests/             Disposable Paperless integration compose
```

## Current scope

This is the first runnable implementation, not hardware acceptance. Validate it
against the ES-400 II using
[`docs/es400ii-validation.md`](docs/es400ii-validation.md). OCR is intentionally
off by default for the Paperless output path because Paperless normally owns OCR
and archival conversion.

- [Architecture and feasibility assessment](docs/architecture-feasibility.md)
- [NAPS2 compatibility and driver plan](docs/naps2-compatibility.md)
- [ES-400 II hardware validation plan](docs/es400ii-validation.md)
- [Physical scanner buttons in Docker](docs/physical-scanner-buttons.md)
- [TrueNAS and Paperless deployment runbook](docs/truenas-paperless-deployment.md)
