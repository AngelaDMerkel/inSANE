#!/bin/sh
set -eu

compose_file="${1:-compose.paperless-truenas.yaml}"
probe_name=".insane-shared-consume-probe"

docker compose -f "$compose_file" config --quiet

health="$(docker compose -f "$compose_file" exec -T insane \
  curl --fail --silent http://127.0.0.1:8080/api/v1/health)"
case "$health" in
  *'"status":"ok"'*'"state":"writable"'*'"output":"writable"'*) ;;
  *)
    echo "FAIL: inSANE health did not confirm writable state and output mounts." >&2
    exit 1
    ;;
esac

docker compose -f "$compose_file" exec -T insane sh -ec \
  "printf '%s\n' 'inSANE shared mount probe' > '/data/output/$probe_name'"

if ! docker compose -f "$compose_file" exec -T --user "${PAPERLESS_UID:-568}:${PAPERLESS_GID:-568}" webserver \
  sh -ec "test -r '/usr/src/paperless/consume/$probe_name' && rm '/usr/src/paperless/consume/$probe_name'"; then
  docker compose -f "$compose_file" exec -T insane sh -ec \
    "rm -f '/data/output/$probe_name'" || true
  echo "FAIL: Paperless could not read and remove a file written through inSANE's output mount." >&2
  exit 1
fi

echo "PASS: Compose is valid, inSANE storage is writable, and both services share the Paperless consume directory."
