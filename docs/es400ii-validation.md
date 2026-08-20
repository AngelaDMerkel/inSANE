# Epson ES-400 II validation plan

This plan determines whether a failure is in USB/container access, the installed
SANE backend, NAPS2.Sdk, or inSANE. It intentionally tests from the bottom up.

Do not place sensitive documents in the feeder for these tests. Redact the
scanner serial number before committing output.

## 1. Record the running image and USB device

On TrueNAS:

```sh
docker inspect insane --format '{{.Config.Image}} {{.Image}}'
lsusb -d 04b8:0181
```

Expected USB identity: Epson `04b8:0181`.

## 2. Record SANE and device discovery

```sh
docker exec insane scanimage -V
docker exec insane sane-find-scanner -q
docker exec insane scanimage -L
```

The preferred direct-USB device should begin with `epsonds:`. Record any
duplicate devices exposed through `escl:`, `airscan:`, or another Epson backend;
all later commands must name the backend explicitly so test results are not
mixed.

SANE Backends 1.1.1 or newer is required for the ES-400 II support added to
`epsonds`. A newer maintained version is preferred.

## 3. Capture raw capabilities

Replace `<DEVICE>` with the exact ID from `scanimage -L`.

```sh
docker exec insane scanimage -d '<DEVICE>' -A
```

If the source list contains `ADF Front` and `ADF Duplex`, capture each dynamic
view as well:

```sh
docker exec insane scanimage -d '<DEVICE>' -A --source 'ADF Front'
docker exec insane scanimage -d '<DEVICE>' -A --source 'ADF Duplex'
```

Preserve the output as test fixtures after removing the device serial. Important
values are:

- all source strings;
- any `adf-mode`, `adf_mode`, or Boolean `duplex` option;
- resolution values/ranges;
- `page-width` and `page-height`;
- top-left and bottom-right scan bounds;
- ADF skew/crop, double-feed, and paper-protection controls.

## 4. Prove simplex Letter independently of inSANE

Create a temporary directory in the container and load two numbered sheets.
Use the exact source string reported by the device.

```sh
docker exec insane mkdir -p /tmp/es400ii-test
docker exec insane scanimage \
  -d '<DEVICE>' \
  --source 'ADF Front' \
  --mode Color \
  --resolution 300 \
  -l 0 -t 0 -x 215.9 -y 279.4 \
  --format=tiff \
  --batch=/tmp/es400ii-test/simplex-%03d.tif
```

Pass criteria:

- two sheets produce two files;
- each page is approximately 2550 × 3300 pixels at 300 DPI;
- the process ends normally when the feeder is empty;
- no page is clipped on the right or bottom.

If the backend exposes `page-width` and `page-height`, repeat with exact media
dimensions added:

```text
--page-width 215.9 --page-height 279.4
```

## 5. Prove one-pass duplex Letter independently of inSANE

Load two sheets marked `1-front`, `1-back`, `2-front`, and `2-back`.

```sh
docker exec insane scanimage \
  -d '<DEVICE>' \
  --source 'ADF Duplex' \
  --mode Color \
  --resolution 300 \
  -l 0 -t 0 -x 215.9 -y 279.4 \
  --format=tiff \
  --batch=/tmp/es400ii-test/duplex-%03d.tif
```

If the device represents duplex with a feeder source plus another option, use
the exact capability output instead, for example:

```text
--source ADF --adf-mode Duplex
```

or:

```text
--source ADF --duplex=yes
```

Pass criteria:

- two sheets produce four files;
- order is `1-front`, `1-back`, `2-front`, `2-back`;
- every page has Letter dimensions and orientation;
- ADF exhaustion after page four is treated as success.

## 6. Prove the same job through inSANE

Perform the same job from inSANE using a saved per-device profile. Confirm the
discovered capabilities and completed session match the successful raw test,
especially:

- selected source and duplex option;
- the reported feeder source and duplex setting;
- exact US Letter page size;
- front/back order and total page count;
- device ID/backend.

## 7. Reliability acceptance suite

After the basic job succeeds, run:

1. Twenty consecutive jobs of 10 Letter sheets / 20 duplex pages.
2. Scanner sleep followed by a new job.
3. USB disconnect/reconnect followed by a new job.
4. Container restart followed by a new job.
5. Empty-feeder start.
6. Deliberate cover-open or safe paper-jam condition, if practical.
7. A batch containing blank backs.

Track missing pages, duplicate pages, order, orientation, dimensions, job time,
error classification, and recovery without restarting the NAS.

## 8. Deployment checks

Use [`compose.paperless-truenas.yaml`](../compose.paperless-truenas.yaml) and the
[TrueNAS runbook](truenas-paperless-deployment.md). The initial USB proof keeps
`privileged: true` so scanner reconnects do not invalidate a single ephemeral
`/dev/bus/usb/BBB/DDD` mapping. Do not run scanservjs concurrently with inSANE.

After the scanner passes, replace broad privilege only where the TrueNAS Docker
runtime permits a full USB bus mapping, character-device major 189 access, and
explicit group permissions without breaking reconnect behavior.
