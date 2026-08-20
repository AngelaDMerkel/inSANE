#!/usr/bin/env python3
"""Create one PDF through inSANE for a live Paperless consumer test."""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class SmokeFailure(RuntimeError):
    pass


def request_json(base: str, path: str, method: str = "GET", body=None, expected=(200,)):
    data = None if body is None else json.dumps(body).encode("utf-8")
    headers = {"Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(base + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = response.read()
            status = response.status
    except urllib.error.HTTPError as error:
        payload = error.read()
        status = error.code
    if status not in expected:
        raise SmokeFailure(
            f"{method} {path} returned {status}, expected {expected}: "
            f"{payload.decode('utf-8', errors='replace')}"
        )
    return json.loads(payload) if payload else None


def wait_for_health(base: str, timeout: int = 180) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            health = request_json(base, "/api/v1/health")
            if health["status"] == "ok" and health["storage"]["output"] == "writable":
                return
        except (OSError, SmokeFailure, KeyError):
            pass
        time.sleep(1)
    raise SmokeFailure("inSANE did not become healthy with a writable output mount")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:51236")
    args = parser.parse_args()
    base = args.base.rstrip("/")
    wait_for_health(base)

    devices = request_json(base, "/api/v1/devices")
    demo = next((device for device in devices if device["driver"] == "demo"), None)
    if demo is None:
        raise SmokeFailure("demo scanner was not discovered")

    settings = {
        "deviceKey": demo["key"],
        "paperSource": "duplex",
        "pageSize": "letter",
        "resolution": 300,
        "bitDepth": "color",
        "autoDeskew": True,
        "discardBlankPages": False,
        "flipDuplexBacks": False,
        "brightness": 0,
        "contrast": 0,
        "blankPageWhiteThreshold": 70,
        "blankPageCoverageThreshold": 15,
        "driverOptions": {},
    }
    session = request_json(base, "/api/v1/sessions", "POST", {"title": "Paperless ingestion gate"}, (201,))
    job = request_json(base, f"/api/v1/sessions/{session['id']}/scan", "POST", {"settings": settings}, (202,))

    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        job = request_json(base, f"/api/v1/scan-jobs/{job['id']}")
        if job["status"] == "completed":
            break
        if job["status"] in {"failed", "cancelled"}:
            raise SmokeFailure(f"demo scan ended as {job['status']}: {job.get('error')}")
        time.sleep(0.2)
    else:
        raise SmokeFailure("demo scan did not finish within 30 seconds")

    stem = f"insane-paperless-gate-{uuid.uuid4().hex}"
    saved = request_json(base, f"/api/v1/sessions/{session['id']}/save", "POST", {
        "title": "Paperless ingestion gate",
        "fileName": stem,
        "format": "pdf",
        "pageIds": None,
    })
    if not saved["fileName"].endswith(".pdf"):
        raise SmokeFailure("inSANE did not publish a PDF")
    print(saved["fileName"])
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (SmokeFailure, OSError, ValueError, KeyError) as error:
        print(f"PAPERLESS INGESTION GATE FAILED: {error}", file=sys.stderr)
        raise SystemExit(1)
