#!/usr/bin/env python3
"""Hardware-free release gate for an inSANE demo container."""

from __future__ import annotations

import argparse
import io
import json
import re
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile


class SmokeFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SmokeFailure(message)


def request(base: str, path: str, method: str = "GET", body=None, headers=None, expected=(200,)):
    data = None if body is None else json.dumps(body).encode("utf-8")
    request_headers = {"Accept": "application/json", **(headers or {})}
    if data is not None:
        request_headers["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, data=data, method=method, headers=request_headers)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            payload = response.read()
            status = response.status
            response_headers = response.headers
    except urllib.error.HTTPError as error:
        payload = error.read()
        status = error.code
        response_headers = error.headers
    if status not in expected:
        detail = payload.decode("utf-8", errors="replace")
        raise SmokeFailure(f"{method} {path} returned {status}, expected {expected}: {detail}")
    return status, response_headers, payload


def request_json(base: str, path: str, method: str = "GET", body=None, headers=None, expected=(200,)):
    status, response_headers, payload = request(base, path, method, body, headers, expected)
    return status, response_headers, json.loads(payload) if payload else None


def poll_job(base: str, job_id: str, terminal=("completed",), timeout=20):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        _, _, job = request_json(base, f"/api/v1/scan-jobs/{job_id}")
        if job["status"] in {"completed", "failed", "cancelled"}:
            require(job["status"] in terminal,
                    f"job {job_id} ended as {job['status']}, expected one of {terminal}: {job.get('error')}")
            return job
        time.sleep(0.15)
    raise SmokeFailure(f"job {job_id} did not finish within {timeout} seconds")


def create_session(base: str, title: str):
    _, _, session = request_json(base, "/api/v1/sessions", "POST", {"title": title}, expected=(201,))
    return session


def start_scan(base: str, session_id: str, settings: dict):
    _, _, job = request_json(base, f"/api/v1/sessions/{session_id}/scan", "POST",
                             {"settings": settings}, expected=(202,))
    return job


def png_dimensions(payload: bytes):
    require(payload.startswith(b"\x89PNG\r\n\x1a\n"), "ZIP entry is not a PNG image")
    return struct.unpack(">II", payload[16:24])


def jpeg_dimensions(payload: bytes):
    require(payload.startswith(b"\xff\xd8"), "page image is not JPEG data")
    offset = 2
    start_of_frame = {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}
    while offset + 8 < len(payload):
        if payload[offset] != 0xFF:
            offset += 1
            continue
        marker = payload[offset + 1]
        offset += 2
        if marker in (0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
            continue
        require(offset + 2 <= len(payload), "JPEG marker is truncated")
        length = struct.unpack(">H", payload[offset:offset + 2])[0]
        require(length >= 2 and offset + length <= len(payload), "JPEG segment is invalid")
        if marker in start_of_frame:
            height, width = struct.unpack(">HH", payload[offset + 3:offset + 7])
            return width, height
        offset += length
    raise SmokeFailure("JPEG dimensions were not found")


def pdf_media_boxes(payload: bytes):
    number = rb"([+-]?(?:\d+(?:\.\d*)?|\.\d+))"
    pattern = rb"/MediaBox\s*\[\s*" + rb"\s+".join([number] * 4) + rb"\s*\]"
    return [tuple(float(value) for value in match.groups()) for match in re.finditer(pattern, payload)]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:51235")
    parser.add_argument("--button-token", default="release-button-token")
    args = parser.parse_args()
    base = args.base.rstrip("/")

    checks = []

    def passed(name: str) -> None:
        checks.append(name)
        print(f"PASS  {name}")

    _, _, health = request_json(base, "/api/v1/health")
    require(health["status"] == "ok", "health status is not ok")
    require(health["runtime"]["uid"] == 568 and health["runtime"]["gid"] == 568,
            f"runtime identity does not match the release fixture: {health['runtime']}")
    require(health["storage"]["state"] == "writable", "state mount is not writable")
    require(health["storage"]["output"] == "writable", "output mount is not writable")
    _, _, system = request_json(base, "/api/v1/system")
    require(system["demoEnabled"] is True, "release smoke test requires the demo scanner")
    require(system["outputPath"] == "/data/output", "output path is not /data/output")
    passed("health, writable mounts, and system configuration")

    _, _, default_session = request_json(base, "/api/v1/sessions", "POST", {"title": None}, expected=(201,))
    require(re.fullmatch(r"Scan \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", default_session["title"]) is not None,
            f"default title is not an ISO 8601 UTC timestamp: {default_session['title']}")
    passed("ISO 8601 default document title")

    _, _, drivers = request_json(base, "/api/v1/drivers")
    driver_names = {driver["driver"] for driver in drivers}
    require({"demo", "sane", "escl", "wia", "twain", "apple"} <= driver_names,
            "driver catalogue is incomplete")
    _, _, devices = request_json(base, "/api/v1/devices")
    demo = next((device for device in devices if device["driver"] == "demo"), None)
    require(demo is not None, "demo scanner was not discovered")
    device_key = demo["key"]
    encoded_key = urllib.parse.quote(device_key, safe="")
    _, _, capabilities = request_json(base, f"/api/v1/devices/{encoded_key}/capabilities")
    require("duplex" in capabilities["paperSources"], "duplex source is unavailable")
    require(300 in capabilities["sources"]["duplex"]["resolutions"], "300 dpi is unavailable")
    require("color" in capabilities["sources"]["duplex"]["bitDepths"], "colour is unavailable")
    require("letter" in capabilities["sources"]["duplex"]["pageSizes"], "Letter is unavailable")
    require(capabilities["sources"]["duplex"]["supportsAutomaticPageSize"] is True,
            "automatic paper sizing is unavailable")
    passed("driver catalogue, discovery, and source-aware sizing capabilities")

    settings = {
        "deviceKey": device_key,
        "paperSource": "duplex",
        "pageSize": "auto",
        "resolution": 300,
        "bitDepth": "color",
        "autoDeskew": True,
        "discardBlankPages": False,
        "flipDuplexBacks": False,
        "brightness": 3,
        "contrast": 2,
        "blankPageWhiteThreshold": 70,
        "blankPageCoverageThreshold": 15,
        "driverOptions": {},
    }
    profile = {
        "name": "Release gate duplex",
        "deviceKey": device_key,
        "settings": settings,
        "isDefault": True,
    }
    _, _, profile = request_json(base, "/api/v1/profiles", "POST", profile)
    profile_id = profile["id"]
    _, _, profiles = request_json(base, f"/api/v1/profiles?deviceKey={encoded_key}")
    require(any(item["id"] == profile_id and item["isDefault"] for item in profiles),
            "default profile was not persisted")
    profile["name"] = "Release gate duplex updated"
    profile["settings"]["contrast"] = 4
    _, _, updated_profile = request_json(base, f"/api/v1/profiles/{profile_id}", "PUT", profile)
    require(updated_profile["name"].endswith("updated"), "profile update failed")
    passed("profile create, default, query, and update")

    session = create_session(base, "Release gate document")
    session_id = session["id"]
    job = start_scan(base, session_id, settings)
    completed = poll_job(base, job["id"])
    require(completed["pagesCompleted"] == 4, "duplex demo scan did not produce four pages")
    _, _, session = request_json(base, f"/api/v1/sessions/{session_id}")
    require(len(session["pages"]) == 4, "session did not accrue all scanned pages")
    for page in session["pages"]:
        _, headers, image = request(base, page["imageUrl"])
        require(headers.get_content_type() == "image/jpeg" and image.startswith(b"\xff\xd8"),
                "page image endpoint did not return JPEG data")
        dimensions = jpeg_dimensions(image)
        require(dimensions == (850, 1100),
                f"automatic paper sizing did not remove the demo scanner background: {dimensions}")
        require((page["pixelWidth"], page["pixelHeight"]) == dimensions,
                f"page metadata does not match the scanned image dimensions: {page}")
    passed("asynchronous duplex scan, automatic paper sizing, and live page images")

    original_ids = [page["id"] for page in session["pages"]]
    reversed_ids = list(reversed(original_ids))
    _, _, session = request_json(base, f"/api/v1/sessions/{session_id}/pages/reorder", "POST",
                                 {"pageIds": reversed_ids})
    require([page["id"] for page in session["pages"]] == reversed_ids, "page reorder failed")
    first_id, second_id = reversed_ids[:2]
    _, _, session = request_json(base, f"/api/v1/sessions/{session_id}/pages/{first_id}/rotate", "POST",
                                 {"degrees": 90})
    require(session["pages"][0]["rotation"] == 90, "page rotation failed")
    request_json(base, f"/api/v1/sessions/{session_id}/pages/{first_id}/rotate", "POST",
                 {"degrees": 45}, expected=(400,))
    _, _, session = request_json(base, f"/api/v1/sessions/{session_id}/pages/rotate", "POST",
                                 {"pageIds": [first_id, second_id], "degrees": 90})
    require([page["rotation"] for page in session["pages"][:2]] == [180, 90],
            "multi-page rotation was not applied to every selected page")
    _, _, session = request_json(base, f"/api/v1/sessions/{session_id}/pages/rotate", "POST",
                                 {"pageIds": [first_id, second_id], "degrees": -90})
    require([page["rotation"] for page in session["pages"][:2]] == [90, 0],
            "multi-page counter-rotation was not applied to every selected page")
    request_json(base, f"/api/v1/sessions/{session_id}/pages/rotate", "POST",
                 {"pageIds": [first_id, second_id], "degrees": 45}, expected=(400,))
    crop = {"pageIds": [first_id, second_id], "x": 0.1, "y": 0.1, "width": 0.8, "height": 0.8}
    _, _, session = request_json(base, f"/api/v1/sessions/{session_id}/pages/crop", "POST", crop)
    require(all(page["crop"]["width"] == 0.8 for page in session["pages"][:2]),
            "multi-page crop was not applied")
    request_json(base, f"/api/v1/sessions/{session_id}/pages/crop", "POST",
                 {**crop, "width": 1.2}, expected=(400,))
    passed("drag-model crop coordinates, multi-page crop and rotation, and reorder validation")

    exports = []
    for fmt, stem in (("zip-png", "release-pages-png"), ("zip-jpeg", "release-pages-jpeg"),
                      ("tiff", "release-pages-tiff")):
        _, _, result = request_json(base, f"/api/v1/sessions/{session_id}/save", "POST", {
            "title": "Release selected pages",
            "fileName": stem,
            "format": fmt,
            "pageIds": [first_id, second_id],
        })
        exports.append((fmt, result))

    for fmt, result in exports:
        _, headers, payload = request(base, result["downloadUrl"])
        if fmt.startswith("zip"):
            require(payload.startswith(b"PK"), f"{fmt} export is not a ZIP archive")
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                names = archive.namelist()
                require(len(names) == 2, f"{fmt} export does not contain two pages")
                if fmt == "zip-png":
                    dimensions = [png_dimensions(archive.read(name)) for name in names]
                    require(dimensions == [(880, 680), (680, 880)],
                            f"crop/rotation output dimensions are incorrect: {dimensions}")
                else:
                    require(all(archive.read(name).startswith(b"\xff\xd8") for name in names),
                            "ZIP JPEG export contains a non-JPEG entry")
        else:
            require(payload[:4] in (b"II*\x00", b"MM\x00*"), "TIFF export has an invalid signature")
        require(int(headers.get("Content-Length", "0")) > 0, f"{fmt} history download is empty")
    passed("partial document splitting, TIFF, ZIP/PNG, ZIP/JPEG, crop, and rotation exports")

    _, headers, pdf_download = request(base, f"/api/v1/sessions/{session_id}/download", "POST", {
        "title": "Release browser download",
        "fileName": "release-browser-download",
        "format": "pdf",
        "pageIds": None,
    })
    require(headers.get_content_type() == "application/pdf" and pdf_download.startswith(b"%PDF"),
            "direct browser PDF download failed")
    _, _, pdf_result = request_json(base, f"/api/v1/sessions/{session_id}/save", "POST", {
        "title": "Release final PDF",
        "fileName": "release-final-pdf",
        "format": "pdf",
        "pageIds": None,
    })
    _, _, history = request_json(base, "/api/v1/history")
    require(len(history) >= 4, "document history is missing saved export records")
    require(any(item["outputFileName"] == pdf_result["fileName"] for item in history),
            "saved PDF is absent from history")
    passed("PDF save, browser download, atomic history publication, and filename routing")

    _, _, button_session = request_json(base, "/api/v1/sessions", "POST",
                                        {"title": "Physical button release gate"}, expected=(201,))
    request_json(base, "/api/v1/actions/scanner-button", "POST", {}, expected=(401,))
    _, _, button_result = request_json(base, "/api/v1/actions/scanner-button", "POST", {},
                                       headers={"X-inSANE-Button-Token": args.button_token}, expected=(202,))
    button_job = button_result["job"]
    poll_job(base, button_job["id"])
    _, _, button_session = request_json(base, f"/api/v1/sessions/{button_result['sessionId']}")
    require(len(button_session["pages"]) == 4, "physical button scan did not use the default duplex profile")
    passed("constant-time-token physical scanner button integration")

    error_session = create_session(base, "Error model release gate")
    invalid_settings = {**settings, "deviceKey": "invalid-device-key"}
    failed_job = start_scan(base, error_session["id"], invalid_settings)
    failed = poll_job(base, failed_job["id"], terminal=("failed",))
    require(failed["error"] and failed["error"]["canRetry"] is True,
            "failed scan did not return a retryable structured error")
    _, _, retried = request_json(base, f"/api/v1/scan-jobs/{failed_job['id']}/retry", "POST",
                                 expected=(202,))
    retried = poll_job(base, retried["id"], terminal=("failed",))
    require(retried["attempt"] == 2, "retry attempt was not tracked")
    passed("structured scan error and retry model")

    cancel_session = create_session(base, "Cancellation release gate")
    cancel_job = start_scan(base, cancel_session["id"], settings)
    time.sleep(0.12)
    request(base, f"/api/v1/scan-jobs/{cancel_job['id']}", "DELETE", expected=(202,))
    poll_job(base, cancel_job["id"], terminal=("cancelled",))

    delete_session = create_session(base, "Page deletion release gate")
    feeder_settings = {**settings, "paperSource": "feeder", "pageSize": "legal"}
    delete_job = start_scan(base, delete_session["id"], feeder_settings)
    poll_job(base, delete_job["id"])
    _, _, delete_session = request_json(base, f"/api/v1/sessions/{delete_session['id']}")
    require(all((page["pixelWidth"], page["pixelHeight"]) == (850, 1400)
                for page in delete_session["pages"]),
            "Legal demo pages did not preserve 8.5 by 14 geometry")
    _, _, legal_pdf = request(base, f"/api/v1/sessions/{delete_session['id']}/download", "POST", {
        "title": "Legal geometry release gate",
        "fileName": "legal-geometry-release-gate",
        "format": "pdf",
        "pageIds": None,
    })
    require(pdf_media_boxes(legal_pdf) == [(0.0, 0.0, 612.0, 1008.0)] * 2,
            f"Legal PDF pages are not 8.5 by 14 inches: {pdf_media_boxes(legal_pdf)}")
    page_to_delete = delete_session["pages"][0]["id"]
    request(base, f"/api/v1/sessions/{delete_session['id']}/pages/{page_to_delete}", "DELETE",
            expected=(204,))
    _, _, delete_session = request_json(base, f"/api/v1/sessions/{delete_session['id']}")
    require(len(delete_session["pages"]) == 1, "page deletion failed")
    passed("scan cancellation and page deletion")

    _, _, index = request(base, "/")
    page = index.decode("utf-8")
    require("canvas-viewport" in page and "page-focus-row" in page and "inspector-rows" in page and
            "page-carousel" in page and "crop-apply-selected" in page and
            "sidebar-brand" in page and "page-image-frame" in page and "history-button" in page and
            "workbench-41" in page and "page-focus-sheet" in page and "app-header" not in page and "role=\"tablist\"" not in page and
            "selected-page-title" not in page and "export-scope" not in page and
            "profile-manager-new" in page and "new-profile" in page and "profile-utilities" in page and
            "save-profile" not in page,
            "deployed UI is missing the final canvas or crop controls")
    require(page.index('id="scan-progress"') < page.index('id="scan" class="button scan-button"'),
            "scan progress must render above the fixed scan button")
    _, _, script = request(base, "/app.js?v=workbench-41")
    script_text = script.decode("utf-8")
    require("Scanner connected" not in script_text and "renderPagePreview" in script_text and
            "renderThumbnailPreview" in script_text and "handleCanvasWheel" in script_text and
            "supportsAutomaticPageSize" in script_text and "labelForPageSize" in script_text and
            "openNewProfileDialog" in script_text and "setProfileOverlay" in script_text and
            "defaultDocumentTitle" in script_text and "defaultOutputStem" in script_text and
            "rememberPageDimensions" in script_text and "naturalWidth" in script_text and
            "showModal" not in script_text and "Untitled scan" not in script_text,
            "deployed UI is missing the crop preview or still renders redundant scanner status")
    _, _, stylesheet = request(base, "/app.css?v=workbench-41")
    stylesheet_text = stylesheet.decode("utf-8")
    require("object-fit: contain" in stylesheet_text and "object-fit: fill" not in stylesheet_text and
            "width: calc(680px" not in stylesheet_text,
            "deployed UI still forces scanned pages into Letter preview geometry")
    request(base, "/api/v1/documents/..%2Fsession.json", expected=(404,))
    request(base, f"/api/v1/profiles/{profile_id}", "DELETE", expected=(204,))
    passed("final workbench assets, path containment, and profile deletion")

    print(f"\nRELEASE GATE PASSED: {len(checks)} functional groups")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (SmokeFailure, urllib.error.URLError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        print(f"\nRELEASE GATE FAILED: {error}", file=sys.stderr)
        sys.exit(1)
