# TrueNAS and Paperless-ngx deployment

This runbook deploys the tested `linux/amd64` inSANE image beside Paperless-ngx
and makes both services use the exact same consume directory.

## Release status

The hardware-free release gate covers health, storage, profiles, asynchronous
duplex scanning, page images, reorder, rotation, drag-crop data, selected-page
splitting, PDF, TIFF, ZIP/PNG, ZIP/JPEG, browser download, document history,
button webhooks, structured recovery, retry, cancellation, page deletion,
restart persistence, and route containment. A disposable stack also proved
that a four-page inSANE PDF was consumed successfully by Paperless-ngx v3.0.5.

The Epson ES-400 II itself is not attached to the development machine. USB,
ADF, duplex ordering, Letter geometry, sleep/reconnect, and sustained-load
acceptance therefore remain mandatory on TrueNAS before the deployment is
called hardware-qualified.

## 1. Preserve the current Paperless settings

Back up the Paperless database and data directories before replacing its
compose definition. Record the database password currently used by the running
Postgres volume. Changing `POSTGRES_PASSWORD` after a database has already been
initialized does not rotate that database user's password, so the first
integrated deployment must use the existing value.

Stop scanservjs before starting inSANE. Both services would otherwise compete
for port `51234` and the same USB scanner.

## 2. Prepare dataset access

The production compose uses these host paths:

| Host path | Used by |
| --- | --- |
| `/mnt/Array/databases/insane` | inSANE sessions, page images, profiles, and recovery data |
| `/mnt/Array/databases/paperless/consume` | inSANE output and Paperless input |
| `/mnt/Array/appdata/paperless/*` | Existing Redis, Postgres, application data, and export data |
| `/mnt/Array/databases/paperless/media` | Existing Paperless document media |

In the TrueNAS dataset ACL editor, grant the Apps identity (UID and GID `568`
in the supplied stack) Modify and Traverse access to the inSANE and Paperless
consume datasets. Preserve any stronger existing Paperless ACL entries. The
equivalent on a non-ACL test dataset is:

```sh
install -d -o 568 -g 568 -m 2775 /mnt/Array/databases/insane
install -d -o 568 -g 568 -m 2775 /mnt/Array/databases/paperless/consume
```

inSANE's entrypoint starts as root only long enough to create the bind-mount
directories, migrate existing inSANE state ownership, and discover the numeric
groups used by USB device nodes. It then replaces itself with the inSANE/NAPS2
process at effective UID/GID `568:568`. The health endpoint reports that
effective identity and refuses readiness if either bind mount is not writable.
Final exports are renamed atomically into the consume directory and receive mode
`0664`; Paperless's mapped identity can read them and can remove them after
ingestion because it has write access to the directory.

## 3. Confirm credentials and image

The compose is intentionally self-contained for this existing stack. It uses
`docker.io/angeladmerkel/insane:1.0.0-amd64`, preserves the current Paperless
database credential `paperless_password`, and fixes the shared-directory
identity to `568:568`. Rotate the database password separately after deployment;
changing only the compose value does not change an initialized Postgres role.

## 4. Validate and start

```sh
docker compose -f compose.paperless-truenas.yaml config --quiet
docker compose -f compose.paperless-truenas.yaml pull
docker compose -f compose.paperless-truenas.yaml up -d
docker compose -f compose.paperless-truenas.yaml ps
```

The Paperless consumer is explicitly set to `/usr/src/paperless/consume` and
polls every five seconds. Polling avoids relying on filesystem notifications
that may not propagate reliably through Docker, ZFS, NFS, or SMB layers.

## 5. Prove the shared directory

Run the included non-document probe:

```sh
./scripts/verify-truenas-stack.sh compose.paperless-truenas.yaml
```

The verifier checks the compose model, requires inSANE health to report both
mounts writable, writes a hidden probe through inSANE's `/data/output`, then
requires Paperless's mapped UID/GID to read and remove that exact file through
`/usr/src/paperless/consume`.

## 6. Qualify the Epson scanner

Open `http://TRUENAS-IP:51234`, select the ES-400 II, and run the full
[hardware acceptance plan](es400ii-validation.md). At minimum, require:

1. SANE discovery of the expected Epson device and ADF sources.
2. Two Letter sheets producing four pages in front/back order at 300 DPI.
3. Correct 8.5 x 11 inch bounds without clipping.
4. Twenty consecutive 10-sheet duplex jobs without missing or duplicate pages.
5. Recovery after scanner sleep, USB reconnect, container restart, empty feeder,
   and a safe jam/cover-open test.

## 7. Prove real Paperless ingestion

Save a uniquely named PDF from inSANE, then watch Paperless:

```sh
docker compose -f compose.paperless-truenas.yaml logs -f webserver
```

Pass only when the log reports that exact filename's consumption finished and
the document appears in Paperless. The source file disappearing from `consume`
after success is expected: the consume directory is a hand-off queue, not an
archive. inSANE retains its session metadata and page images under
`/mnt/Array/databases/insane`.

## Rollback

Stop the integrated stack, restore the prior Paperless compose, and start the
prior services against the unchanged bind mounts. Do not delete or recreate the
Postgres, Paperless data, media, or inSANE datasets during rollback.
