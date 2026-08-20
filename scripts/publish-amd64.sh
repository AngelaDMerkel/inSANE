#!/bin/sh
set -eu

image="${1:-}"
case "$image" in
  */*:*) ;;
  *)
    echo "Usage: $0 DOCKERHUB_NAMESPACE/insane:VERSION-amd64" >&2
    exit 2
    ;;
esac

docker buildx build \
  --platform linux/amd64 \
  --provenance=mode=max \
  --sbom=true \
  --tag "$image" \
  --push \
  .

docker buildx imagetools inspect "$image"
