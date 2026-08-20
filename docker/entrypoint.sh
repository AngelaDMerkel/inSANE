#!/bin/sh
set -eu

insane_uid="${PUID:-568}"
insane_gid="${PGID:-568}"

case "$insane_uid:$insane_gid" in
  *[!0-9:]*|:*|*:)
    echo "PUID and PGID must be positive numeric IDs." >&2
    exit 64
    ;;
esac

if [ "$(id -u)" -ne 0 ]; then
  if [ "$(id -u)" -ne "$insane_uid" ] || [ "$(id -g)" -ne "$insane_gid" ]; then
    echo "Container user override does not match PUID:PGID $insane_uid:$insane_gid." >&2
    exit 77
  fi
  umask 0002
  exec "$@"
fi

insane_group="$(getent group "$insane_gid" | cut -d: -f1 || true)"
if [ -z "$insane_group" ]; then
  insane_group="insane-apps"
  groupadd --gid "$insane_gid" "$insane_group"
fi

insane_user="$(getent passwd "$insane_uid" | cut -d: -f1 || true)"
if [ -z "$insane_user" ]; then
  insane_user="insane"
  useradd --uid "$insane_uid" --gid "$insane_gid" --no-create-home \
    --home-dir /nonexistent --shell /usr/sbin/nologin "$insane_user"
else
  usermod --gid "$insane_gid" "$insane_user"
fi

# The TrueNAS USB nodes are commonly root-owned. Keep the application at the
# Apps UID while granting only the supplementary groups exposed by the USB bus.
usb_gids="0"
if [ -d /dev/bus/usb ]; then
  usb_gids="$usb_gids $(find /dev/bus/usb -maxdepth 2 -printf '%G\n' | sort -un)"
fi
for usb_gid in $usb_gids; do
  [ "$usb_gid" = "$insane_gid" ] && continue
  usb_group="$(getent group "$usb_gid" | cut -d: -f1 || true)"
  if [ -z "$usb_group" ]; then
    usb_group="insane-usb-$usb_gid"
    groupadd --gid "$usb_gid" "$usb_group"
  fi
  usermod --append --groups "$usb_group" "$insane_user"
done

mkdir -p /data/state /data/output
owner_marker="/data/state/.insane-owner"
expected_owner="$insane_uid:$insane_gid"
current_owner="$(cat "$owner_marker" 2>/dev/null || true)"
if [ "$current_owner" != "$expected_owner" ]; then
  chown -R "$insane_uid:$insane_gid" /data/state
  printf '%s\n' "$expected_owner" > "$owner_marker"
fi
chown "$insane_uid:$insane_gid" /data/state /data/output "$owner_marker"
chmod 2775 /data/state /data/output
chmod 0664 "$owner_marker"

umask 0002
exec gosu "$insane_user" "$@"
