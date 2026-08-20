# Physical scanner buttons in Docker

inSANE exposes a token-protected action that starts the profile marked
**Button default** for a device:

```sh
curl --fail --silent --show-error \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'X-inSANE-Button-Token: replace-with-a-long-random-token' \
  --data '{}' \
  http://insane:8080/api/v1/actions/scanner-button
```

Enable it with these container environment values:

```yaml
InSane__Scanner__PhysicalButtonEnabled: "true"
InSane__Scanner__PhysicalButtonToken: "replace-with-a-long-random-token"
```

Then use **Manage scan profiles** in the Settings column to mark one profile as
the button default. A trigger adds pages to the most recently active building
document, or creates a document if none is open. Repeated button events are
rejected while the scanner is busy.

## Why a button monitor is a separate service

USB passthrough alone does not deliver scanner-button events to a web
application. A SANE-aware monitor such as
[scanbd](https://github.com/mdengler/scanbd) must observe the scanner's button
sensor and run the request above as its action script. Whether this works for a
specific button depends on the SANE backend exposing that button as a sensor.

Do not run ordinary scanbd polling beside inSANE while both open the same USB
device. scanbd documents that polling locks the scanner. Its supported sharing
model is **scanbd + scanbm manager mode**: scanbm proxies `saned`, asks scanbd to
release the device for a scan, and lets clients connect through SANE's `net`
backend.

For a TrueNAS deployment, the safe arrangement is:

1. Give the USB device to a scanbd/scanbm service, not to two independent
   containers.
2. Expose scanbm's SANE network port only on the private Docker network.
3. Configure `/etc/sane.d/net.conf` in the inSANE container with the scanbm
   service name, so NAPS2 discovers the proxied scanner through SANE `net`.
4. Configure the scanbd button action to call inSANE's action URL with the
   shared token.
5. Confirm manual scans release and reacquire the device before enabling the
   action permanently.

The exact scanbd sensor name and service configuration are backend-specific,
so they should be captured during ES-400 II hardware acceptance instead of
being guessed in the default Compose file. The upstream
[scanbm manual](https://manpages.debian.org/unstable/scanbd/scanbm.8.en.html)
describes the release/proxy lifecycle.
