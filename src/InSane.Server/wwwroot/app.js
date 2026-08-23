const $ = (selector) => document.querySelector(selector);
const state = {
  devices: [], capabilities: null, profiles: [], session: null, selectedPageId: null,
  activeJob: null, zoom: .72, cropEditing: false, titleTimer: null, outputNameTouched: false,
  draggedPageId: null, reorderPending: false, pageDeletePending: false, rotationPending: false,
  selectedPageIds: new Set(), selectionAnchorId: null, failedJob: null,
  cropDraft: null, cropDraftPageId: null, cropDrag: null, wheelZoomTimer: null,
  profileOverlayReturn: "settings", profileOverlayTrigger: null, pageDimensions: new Map()
};

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options
  });
  if (!response.ok) {
    let detail = `${response.status} ${response.statusText}`;
    try { detail = (await response.json()).detail || detail; } catch { /* response was not JSON */ }
    throw new Error(detail);
  }
  if (response.status === 204 || response.headers.get("content-length") === "0") return null;
  return response.json();
}

function option(value, label, selected = false) {
  const element = document.createElement("option");
  element.value = value; element.textContent = label; element.selected = selected;
  return element;
}

function labelForSource(value) {
  return ({ duplex: "Duplex ADF", feeder: "Feeder", flatbed: "Flatbed", auto: "Automatic" })[value] || value;
}

function labelForBitDepth(value) {
  return ({ color: "Colour", grayscale: "Greyscale", blackAndWhite: "Black & white" })[value] || value;
}

function labelForPageSize(value) {
  return ({ auto: "Automatic", letter: "US Letter", legal: "US Legal", a4: "A4" })[value] || value;
}

function defaultDocumentTitle(date = new Date()) {
  return `Scan ${date.toISOString().replace(/\.\d{3}Z$/, "Z")}`;
}

function defaultOutputStem(title) {
  const match = /^Scan (\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})Z$/.exec(title.trim());
  return match ? `scan-${match[1]}${match[2]}${match[3]}T${match[4]}${match[5]}${match[6]}Z` : slug(title);
}

function selectedPage() { return state.session?.pages.find(page => page.id === state.selectedPageId) || null; }

function selectIfAvailable(selector, value) {
  const select = $(selector);
  if ([...select.options].some(item => item.value === String(value))) select.value = String(value);
}

async function initialise() {
  wireEvents();
  const initialTitle = defaultDocumentTitle();
  $("#document-title").value = initialTitle;
  $("#output-filename").value = defaultOutputStem(initialTitle);
  try {
    const [devices, sessions] = await Promise.all([api("/api/v1/devices"), api("/api/v1/sessions")]);
    state.devices = devices;
    renderDevices();
    const building = sessions.find(session => session.status === "building");
    state.session = building || await api("/api/v1/sessions", { method: "POST", body: JSON.stringify({ title: null }) });
    if (state.session.pages.length) {
      state.selectedPageId = state.session.pages[0].id;
      state.selectionAnchorId = state.selectedPageId;
    }
    renderSession();
    await Promise.all([deviceChanged(), loadHistory()]);
  } catch (error) { showError(error); }
}

function wireEvents() {
  $("#device").addEventListener("change", deviceChanged);
  $("#paper-source").addEventListener("change", renderCapabilities);
  $("#profile").addEventListener("change", applyProfile);
  $("#new-profile").addEventListener("click", () => openNewProfileDialog(false));
  $("#manage-profiles").addEventListener("click", openProfileManager);
  $("#profile-manager-new").addEventListener("click", () => openNewProfileDialog(true));
  $("#profile-cancel").addEventListener("click", closeProfileEditor);
  $("#profile-manager-close").addEventListener("click", closeProfileOverlay);
  $("#profile-form").addEventListener("submit", saveProfile);
  [["#brightness", "#brightness-value"], ["#contrast", "#contrast-value"]].forEach(([input, output]) => {
    $(input).addEventListener("input", () => { $(output).value = $(input).value; });
  });
  $("#scan").addEventListener("click", startOrCancelScan);
  $("#empty-scan").addEventListener("click", startOrCancelScan);
  $("#new-session").addEventListener("click", newSession);
  $("#save-document").addEventListener("click", saveDocument);
  $("#download-document").addEventListener("click", downloadDocument);
  $("#page-focus-number").addEventListener("click", clearPageSelection);
  $("#output-format").addEventListener("change", updateOutputFormat);
  $("#document-title").addEventListener("input", scheduleTitleSave);
  $("#output-filename").addEventListener("input", () => { state.outputNameTouched = true; });
  $("#rotate-left").addEventListener("click", () => rotate(-90));
  $("#rotate-right").addEventListener("click", () => rotate(90));
  $("#delete-page").addEventListener("click", () => removePage(true));
  $("#crop-toggle").addEventListener("click", toggleCrop);
  $("#crop-reset").addEventListener("click", resetCropDraft);
  $("#crop-apply").addEventListener("click", applyCrop);
  $("#crop-overlay").addEventListener("pointerdown", beginCropDrag);
  document.addEventListener("pointermove", updateCropDrag);
  document.addEventListener("pointerup", endCropDrag);
  document.addEventListener("pointercancel", endCropDrag);
  $("#zoom-in").addEventListener("click", () => setZoom(state.zoom + .08));
  $("#zoom-out").addEventListener("click", () => setZoom(state.zoom - .08));
  $("#canvas").addEventListener("wheel", handleCanvasWheel, { passive: false });
  $("#history-tab").addEventListener("click", toggleHistory);
  $("#retry-scan").addEventListener("click", retryScan);
  $("#dismiss-scan-error").addEventListener("click", dismissScanError);
  document.addEventListener("keydown", handleKeyboardShortcut);
}

function profileOverlayOpen() {
  return !$("#profile-dialog").hidden || !$("#profile-manager-dialog").hidden;
}

function setProfileOverlay(view, focusSelector = null) {
  $("#settings-panel").hidden = view !== "settings";
  $("#profile-dialog").hidden = view !== "editor";
  $("#profile-manager-dialog").hidden = view !== "manager";
  $("#new-profile").setAttribute("aria-expanded", String(view === "editor"));
  $("#manage-profiles").setAttribute("aria-expanded", String(view === "manager"));
  if (focusSelector) requestAnimationFrame(() => $(focusSelector)?.focus());
}

function closeProfileOverlay() {
  const trigger = state.profileOverlayTrigger;
  setProfileOverlay("settings");
  state.profileOverlayReturn = "settings";
  state.profileOverlayTrigger = null;
  requestAnimationFrame(() => trigger?.focus());
}

function closeProfileEditor() {
  $("#profile-name").value = "";
  if (state.profileOverlayReturn === "manager") {
    setProfileOverlay("manager", "#profile-manager-new");
    return;
  }
  closeProfileOverlay();
}

function openNewProfileDialog(fromManager = false) {
  if (!fromManager) state.profileOverlayTrigger = $("#new-profile");
  state.profileOverlayReturn = fromManager ? "manager" : "settings";
  $("#profile-name").value = "";
  setProfileOverlay("editor", "#profile-name");
}

function renderDevices() {
  const select = $("#device"); select.replaceChildren();
  const deviceStatus = $("#device-status");
  if (!state.devices.length) {
    select.append(option("", "No scanner found"));
    $("#scan").disabled = true; $("#empty-scan").disabled = true;
    deviceStatus.hidden = false;
    deviceStatus.innerHTML = '<span class="status-dot" style="background:#9b4545"></span>No scanner found';
    return;
  }
  deviceStatus.hidden = true;
  deviceStatus.replaceChildren();
  for (const device of state.devices) select.append(option(device.key, `${device.name} · ${device.driver.toUpperCase()}`));
  $("#scan").disabled = false; $("#empty-scan").disabled = false;
}

async function deviceChanged() {
  const key = $("#device").value;
  if (!key) return;
  try {
    const [caps, profiles] = await Promise.all([
      api(`/api/v1/devices/${encodeURIComponent(key)}/capabilities`),
      api(`/api/v1/profiles?deviceKey=${encodeURIComponent(key)}`)
    ]);
    state.capabilities = caps; state.profiles = profiles;
    renderCapabilities(); renderProfiles();
    $("#device-status").hidden = true;
  } catch (error) { showError(error); }
}

function renderCapabilities() {
  if (!state.capabilities) return;
  const source = $("#paper-source");
  const previousSource = source.value;
  source.replaceChildren(...state.capabilities.paperSources.map(value => option(value, labelForSource(value), value === previousSource)));
  const activeSource = state.capabilities.sources[source.value] || Object.values(state.capabilities.sources)[0];
  if (!activeSource) return;
  const pageSize = $("#page-size"), previousPageSize = pageSize.value;
  const pageSizes = [...(activeSource.pageSizes || ["letter", "legal", "a4"] )];
  if (activeSource.supportsAutomaticPageSize) pageSizes.unshift("auto");
  pageSize.replaceChildren(...pageSizes.map(value => option(value, labelForPageSize(value), value === previousPageSize)));
  if (!pageSize.value) pageSize.value = pageSizes.includes("letter") ? "letter" : pageSizes[0];
  pageSize.title = activeSource.supportsAutomaticPageSize
    ? "Automatic scans the device's full reported area and detects the returned paper edges."
    : "This scanner does not report a scan area for automatic sizing.";
  const resolution = $("#resolution"), previousDpi = Number(resolution.value);
  resolution.replaceChildren(...activeSource.resolutions.map(value => option(value, `${value} dpi`, value === previousDpi)));
  if (!resolution.value) resolution.value = activeSource.resolutions.includes(300) ? "300" : String(activeSource.resolutions[0]);
  const bitDepth = $("#bit-depth"), previousBitDepth = bitDepth.value;
  bitDepth.replaceChildren(...activeSource.bitDepths.map(value => option(value, labelForBitDepth(value), value === previousBitDepth)));
}

function renderProfiles() {
  const select = $("#profile"); select.replaceChildren(option("", "Custom"));
  state.profiles.forEach(profile => select.append(option(profile.id, `${profile.name}${profile.isDefault ? " · button" : ""}`)));
}

function settings() {
  return {
    deviceKey: $("#device").value, paperSource: $("#paper-source").value,
    pageSize: $("#page-size").value, resolution: Number($("#resolution").value),
    bitDepth: $("#bit-depth").value, autoDeskew: $("#auto-deskew").checked,
    discardBlankPages: $("#discard-blank-pages").checked,
    flipDuplexBacks: false,
    brightness: clamp(Number($("#brightness").value), -1000, 1000),
    contrast: clamp(Number($("#contrast").value), -1000, 1000),
    blankPageWhiteThreshold: clamp(Number($("#blank-white-threshold").value), 0, 100),
    blankPageCoverageThreshold: clamp(Number($("#blank-coverage-threshold").value), 0, 100),
    driverOptions: {}
  };
}

function applyProfile() {
  const profile = state.profiles.find(item => item.id === $("#profile").value);
  if (!profile) return;
  const value = profile.settings;
  $("#paper-source").value = value.paperSource; renderCapabilities();
  selectIfAvailable("#page-size", value.pageSize);
  selectIfAvailable("#resolution", value.resolution);
  selectIfAvailable("#bit-depth", value.bitDepth);
  $("#auto-deskew").checked = value.autoDeskew !== false;
  $("#discard-blank-pages").checked = value.discardBlankPages === true;
  $("#brightness").value = value.brightness ?? 0; $("#brightness-value").value = value.brightness ?? 0;
  $("#contrast").value = value.contrast ?? 0; $("#contrast-value").value = value.contrast ?? 0;
  $("#blank-white-threshold").value = value.blankPageWhiteThreshold ?? 70;
  $("#blank-coverage-threshold").value = value.blankPageCoverageThreshold ?? 15;
}

async function saveProfile(event) {
  event.preventDefault();
  try {
    const profile = await api("/api/v1/profiles", { method: "POST", body: JSON.stringify({
      name: $("#profile-name").value, deviceKey: $("#device").value, settings: settings(), isDefault: false
    }) });
    state.profiles.push(profile); renderProfiles(); $("#profile").value = profile.id;
    $("#profile-name").value = "";
    if (state.profileOverlayReturn === "manager") {
      renderProfileManager();
      setProfileOverlay("manager", "#profile-manager-new");
    } else closeProfileOverlay();
    toast("Scan profile saved");
  } catch (error) { showError(error); }
}

async function openProfileManager() {
  state.profileOverlayTrigger = $("#manage-profiles");
  await reloadProfiles();
  renderProfileManager();
  setProfileOverlay("manager", "#profile-manager-close");
}

async function reloadProfiles(preferredId = null) {
  const deviceKey = $("#device").value;
  state.profiles = await api(`/api/v1/profiles?deviceKey=${encodeURIComponent(deviceKey)}`);
  renderProfiles();
  if (preferredId && state.profiles.some(profile => profile.id === preferredId)) $("#profile").value = preferredId;
  if (!$("#profile-manager-dialog").hidden) renderProfileManager();
}

function profileAction(label, action, className = "button quiet small") {
  const button = document.createElement("button");
  button.type = "button"; button.className = className; button.textContent = label;
  button.addEventListener("click", action);
  return button;
}

function renderProfileManager() {
  const list = $("#profile-manager-list"); list.replaceChildren();
  if (!state.profiles.length) {
    const empty = document.createElement("div"); empty.className = "profile-manager-empty";
    empty.textContent = "No profiles have been saved for this device."; list.append(empty); return;
  }
  for (const profile of state.profiles) {
    const row = document.createElement("div"); row.className = "profile-manager-item";
    const name = document.createElement("div"); name.className = "profile-manager-name";
    const input = document.createElement("input"); input.value = profile.name; input.setAttribute("aria-label", `Name for ${profile.name}`);
    name.append(input);
    if (profile.isDefault) {
      const badge = document.createElement("span"); badge.className = "default-badge"; badge.textContent = "Button default"; name.append(badge);
    }
    const actions = document.createElement("div"); actions.className = "profile-manager-actions";
    actions.append(
      profileAction("Use", () => { $("#profile").value = profile.id; applyProfile(); closeProfileOverlay(); }),
      profileAction("Rename", () => updateProfile(profile, { name: input.value })),
      profileAction("Update", () => updateProfile(profile, { settings: settings() })),
      profileAction("Duplicate", () => duplicateProfile(profile))
    );
    if (!profile.isDefault)
      actions.append(profileAction("Set default", () => updateProfile(profile, { isDefault: true })));
    actions.append(profileAction("Delete", () => deleteProfile(profile), "button quiet small danger"));
    row.append(name, actions); list.append(row);
  }
}

async function updateProfile(profile, changes) {
  try {
    const updated = { ...profile, ...changes };
    await api(`/api/v1/profiles/${profile.id}`, { method: "PUT", body: JSON.stringify(updated) });
    await reloadProfiles(profile.id); toast("Scan profile updated");
  } catch (error) { showError(error); }
}

async function duplicateProfile(profile) {
  try {
    const duplicate = await api("/api/v1/profiles", { method: "POST", body: JSON.stringify({
      name: `${profile.name} copy`, deviceKey: profile.deviceKey, settings: profile.settings, isDefault: false
    }) });
    await reloadProfiles(duplicate.id); toast("Scan profile duplicated");
  } catch (error) { showError(error); }
}

async function deleteProfile(profile) {
  if (!confirm(`Delete the “${profile.name}” profile?`)) return;
  try {
    await api(`/api/v1/profiles/${profile.id}`, { method: "DELETE" });
    await reloadProfiles(); toast("Scan profile deleted");
  } catch (error) { showError(error); }
}

async function startOrCancelScan() {
  if (state.activeJob) {
    try { await api(`/api/v1/scan-jobs/${state.activeJob.id}`, { method: "DELETE" }); } catch (error) { showError(error); }
    return;
  }
  if (state.session.status === "saved") return toast("Start a new document before scanning more pages", true);
  if (!$("#device").value) return toast("No scanner is available", true);
  try {
    dismissScanError();
    state.activeJob = await api(`/api/v1/sessions/${state.session.id}/scan`, {
      method: "POST", body: JSON.stringify({ settings: settings() })
    });
    setScanning(true); pollJob();
  } catch (error) { showError(error); }
}

async function pollJob() {
  if (!state.activeJob) return;
  try {
    const [job, session] = await Promise.all([
      api(`/api/v1/scan-jobs/${state.activeJob.id}`), api(`/api/v1/sessions/${state.session.id}`)
    ]);
    state.activeJob = job; state.session = session;
    if (!state.selectedPageId && session.pages.length) {
      state.selectedPageId = session.pages[0].id;
      state.selectionAnchorId = state.selectedPageId;
    }
    renderSession(); renderProgress(job);
    if (["completed", "failed", "cancelled"].includes(job.status)) {
      state.activeJob = null;
      setScanning(false);
      if (job.status === "failed") {
        state.failedJob = job; showScanError(job.error);
        toast(job.error?.message || job.error || "The scan failed", true);
      }
      else toast(job.status === "completed" ? `${job.pagesCompleted} pages added` : "Scan cancelled");
      return;
    }
    setTimeout(pollJob, 350);
  } catch (error) { setScanning(false); state.activeJob = null; showError(error); }
}

function showScanError(error) {
  const details = typeof error === "string"
    ? { code: "scan-failed", message: error, recovery: "Check the scanner and settings, then retry.", canRetry: true }
    : error || { code: "scan-failed", message: "The scan stopped unexpectedly.", recovery: "Check the scanner and settings, then retry.", canRetry: true };
  $("#scan-error").hidden = false;
  $("#scan-error-title").textContent = details.code.split("-").map(value => value[0].toUpperCase() + value.slice(1)).join(" ");
  $("#scan-error-message").textContent = details.message;
  $("#scan-error-recovery").textContent = details.recovery;
  $("#retry-scan").hidden = details.canRetry === false;
}

function dismissScanError() {
  $("#scan-error").hidden = true;
  state.failedJob = null;
}

async function retryScan() {
  if (!state.failedJob) return;
  const failedId = state.failedJob.id;
  try {
    state.activeJob = await api(`/api/v1/scan-jobs/${failedId}/retry`, { method: "POST" });
    dismissScanError(); setScanning(true); pollJob();
  } catch (error) { showError(error); }
}

function setScanning(active) {
  $("#scan").classList.toggle("scanning", active);
  $("#scan-label").textContent = active ? "Cancel scan" : "Scan pages";
  $("#scan-progress").hidden = !active;
  $("#device-status").hidden = true;
  if (state.session) renderPageList();
}

function renderProgress(job) {
  const progress = job.pageProgress || 0;
  $("#progress").value = progress;
  $("#progress-label").textContent = `Receiving page ${job.currentPage || 1}`;
  $("#progress-value").textContent = `${Math.round(progress * 100)}%`;
}

function renderSession() {
  if (!state.session) return;
  reconcilePageSelection();
  const saved = state.session.status === "saved";
  const titleInput = $("#document-title");
  if (document.activeElement !== titleInput) titleInput.value = state.session.title;
  const visibleTitle = titleInput.value.trim() || state.session.title || defaultDocumentTitle();
  const pageTotal = state.session.pages.length;
  $("#page-count").textContent = pageTotal;
  $("#document-status").textContent = saved
    ? `Saved as ${state.session.outputFileName}`
    : pageTotal ? `${pageTotal} ${pageTotal === 1 ? "page" : "pages"} ready to save` : "Building document";
  if (!state.outputNameTouched) $("#output-filename").value = defaultOutputStem(visibleTitle);
  titleInput.disabled = saved;
  $("#output-filename").disabled = saved;
  $("#output-format").disabled = saved;
  $("#save-document").disabled = saved;
  $("#scan").disabled = saved || !$("#device").value;
  $("#empty-scan").disabled = saved || !$("#device").value;
  renderExportScope();
  updateOutputFormat();
  renderPageList(); renderSelectedPage();
}

function renderPageList() {
  const list = $("#page-list"); list.replaceChildren();
  if (!state.session.pages.length) {
    const empty = document.createElement("div"); empty.className = "filmstrip-empty visually-hidden";
    empty.textContent = "Scanned pages will appear here as the feeder completes them."; list.append(empty); return;
  }
  for (const page of state.session.pages) {
    const button = document.createElement("button");
    button.className = `page-thumb${page.id === state.selectedPageId ? " active" : ""}${state.selectedPageIds.has(page.id) ? " export-selected" : ""}`; button.type = "button";
    button.draggable = state.session.status !== "saved" && !state.activeJob && !state.reorderPending;
    button.title = button.draggable
      ? "Drag to reorder · Shift-click to select a range · Backspace to remove"
      : "Shift-click to select a range";
    button.innerHTML = `<span class="thumb-paper"><span class="thumb-image-frame"><img alt=""></span><span class="page-number">${page.number}</span></span><span class="page-thumb-copy visually-hidden"><strong>Page ${page.number}</strong><small>${page.rotation ? `${page.rotation}° · ` : ""}Scanned</small></span>`;
    const thumbnailImage = button.querySelector(".thumb-image-frame img");
    thumbnailImage.addEventListener("load", () => {
      rememberPageDimensions(page, thumbnailImage);
      if (button.isConnected) renderThumbnailPreview(button, page, thumbnailImage);
    }, { once: true });
    thumbnailImage.src = `${page.thumbnailUrl}?v=${encodeURIComponent(state.session.updatedAt)}`;
    renderThumbnailPreview(button, page);
    button.addEventListener("click", event => selectPage(event, page.id));
    button.addEventListener("dragstart", event => beginPageDrag(event, page.id, button));
    button.addEventListener("dragover", event => updatePageDropTarget(event, page.id, button));
    button.addEventListener("dragleave", event => {
      if (!button.contains(event.relatedTarget)) button.classList.remove("drop-before", "drop-after");
    });
    button.addEventListener("drop", event => finishPageDrop(event, page.id, button));
    button.addEventListener("dragend", endPageDrag);
    list.append(button);
  }
}

function reconcilePageSelection() {
  const pageIds = new Set(state.session.pages.map(page => page.id));
  for (const id of state.selectedPageIds) if (!pageIds.has(id)) state.selectedPageIds.delete(id);
  if (state.selectedPageId && !pageIds.has(state.selectedPageId)) state.selectedPageId = state.session.pages[0]?.id || null;
  if (state.selectionAnchorId && !pageIds.has(state.selectionAnchorId)) state.selectionAnchorId = state.selectedPageId;
}

function selectPage(event, pageId) {
  const pages = state.session.pages;
  const additive = event.metaKey || event.ctrlKey;
  if (event.shiftKey) {
    const anchorId = state.selectionAnchorId || state.selectedPageId || pageId;
    const anchorIndex = Math.max(0, pages.findIndex(page => page.id === anchorId));
    const pageIndex = pages.findIndex(page => page.id === pageId);
    const start = Math.min(anchorIndex, pageIndex), end = Math.max(anchorIndex, pageIndex);
    state.selectedPageIds = new Set(pages.slice(start, end + 1).map(page => page.id));
  } else if (additive) {
    if (!state.selectedPageIds.size && state.selectedPageId) state.selectedPageIds.add(state.selectedPageId);
    if (state.selectedPageIds.has(pageId) && state.selectedPageIds.size > 1) state.selectedPageIds.delete(pageId);
    else state.selectedPageIds.add(pageId);
    state.selectionAnchorId = pageId;
  } else {
    state.selectedPageIds.clear();
    state.selectionAnchorId = pageId;
  }
  state.selectedPageId = additive && state.selectedPageIds.size && !state.selectedPageIds.has(pageId)
    ? pages.find(page => state.selectedPageIds.has(page.id))?.id || pageId
    : pageId;
  if (additive) state.selectionAnchorId = state.selectedPageId;
  stopCropEditing(); renderSession();
}

function clearPageSelection() {
  state.selectedPageIds.clear();
  state.selectionAnchorId = state.selectedPageId;
  renderSession();
}

function selectedPageIdsInOrder() {
  if (!state.selectedPageIds.size) return null;
  return state.session.pages.filter(page => state.selectedPageIds.has(page.id)).map(page => page.id);
}

function renderExportScope() {
  const count = state.selectedPageIds.size || (selectedPage() ? 1 : 0);
  const indicator = $("#page-focus-number");
  $("#page-focus-count").textContent = count;
  indicator.classList.toggle("multiple", count > 1);
  indicator.setAttribute("aria-label", `${count} ${count === 1 ? "page" : "pages"} selected`);
  indicator.title = count > 1 ? "Clear page selection" : "1 page selected";
  const multiple = count > 1;
  $("#rotate-left").setAttribute("aria-label", multiple ? "Rotate selected pages left" : "Rotate left");
  $("#rotate-right").setAttribute("aria-label", multiple ? "Rotate selected pages right" : "Rotate right");
  $("#crop-toggle").setAttribute("aria-label", multiple ? "Crop selected pages" : "Crop page");
  $("#delete-page").setAttribute("aria-label", multiple ? "Remove selected pages" : "Remove page");
}

function beginPageDrag(event, pageId, button) {
  if (!button.draggable || state.reorderPending) return event.preventDefault();
  state.draggedPageId = pageId;
  button.classList.add("dragging");
  event.dataTransfer.effectAllowed = "move";
  event.dataTransfer.setData("text/plain", pageId);
}

function updatePageDropTarget(event, pageId, button) {
  if (!state.draggedPageId || state.draggedPageId === pageId) return;
  event.preventDefault();
  event.dataTransfer.dropEffect = "move";
  clearPageDropTargets();
  const after = event.clientX >= button.getBoundingClientRect().left + button.offsetWidth / 2;
  button.classList.add(after ? "drop-after" : "drop-before");

  const list = $("#page-list"), bounds = list.getBoundingClientRect();
  if (event.clientX < bounds.left + 32) list.scrollLeft -= 12;
  else if (event.clientX > bounds.right - 32) list.scrollLeft += 12;
}

function finishPageDrop(event, pageId, button) {
  if (!state.draggedPageId || state.draggedPageId === pageId) return endPageDrag();
  event.preventDefault();
  const after = button.classList.contains("drop-after");
  const draggedPageId = state.draggedPageId;
  endPageDrag();
  reorderPages(draggedPageId, pageId, after);
}

function endPageDrag() {
  state.draggedPageId = null;
  clearPageDropTargets();
  document.querySelectorAll(".page-thumb.dragging").forEach(element => element.classList.remove("dragging"));
}

function clearPageDropTargets() {
  document.querySelectorAll(".page-thumb.drop-before, .page-thumb.drop-after")
    .forEach(element => element.classList.remove("drop-before", "drop-after"));
}

async function reorderPages(draggedPageId, targetPageId, after) {
  const draggedPage = state.session.pages.find(page => page.id === draggedPageId);
  if (!draggedPage) return;
  const reordered = state.session.pages.filter(page => page.id !== draggedPageId);
  const targetIndex = reordered.findIndex(page => page.id === targetPageId);
  if (targetIndex < 0) return;
  reordered.splice(targetIndex + (after ? 1 : 0), 0, draggedPage);
  if (reordered.every((page, index) => page.id === state.session.pages[index].id)) return;

  state.reorderPending = true;
  reordered.forEach((page, index) => { page.number = index + 1; });
  state.session.pages = reordered;
  renderSession();
  try {
    state.session = await api(`/api/v1/sessions/${state.session.id}/pages/reorder`, {
      method: "POST", body: JSON.stringify({ pageIds: reordered.map(page => page.id) })
    });
  } catch (error) {
    try { state.session = await api(`/api/v1/sessions/${state.session.id}`); } catch { /* retain the local session */ }
    showError(error);
  } finally {
    state.reorderPending = false;
    renderSession();
  }
}

function renderSelectedPage() {
  const page = selectedPage(), hasPage = Boolean(page);
  const editable = hasPage && state.session?.status !== "saved";
  $("#empty-canvas").hidden = hasPage; $("#page-stage").hidden = !hasPage;
  $("#page-management").hidden = !hasPage;
  ["#rotate-left", "#rotate-right"].forEach(id => $(id).disabled = !editable || state.rotationPending);
  ["#crop-toggle", "#delete-page"].forEach(id => $(id).disabled = !editable);
  if (!editable) stopCropEditing();
  if (!page) {
    updateCropVisibility();
    return;
  }
  const image = $("#page-image");
  image.dataset.pageId = page.id;
  image.onload = () => {
    if (image.dataset.pageId !== page.id || selectedPage()?.id !== page.id) return;
    rememberPageDimensions(page, image);
    const loadedCrop = state.cropEditing
      ? { x: 0, y: 0, width: 1, height: 1 }
      : sourceToVisualCrop(page.crop, page.rotation || 0);
    renderPagePreview(page, loadedCrop, image);
  };
  image.src = `${page.imageUrl}?v=${encodeURIComponent(state.session.updatedAt)}`;
  const visualCrop = state.cropEditing
    ? { x: 0, y: 0, width: 1, height: 1 }
    : sourceToVisualCrop(page.crop, page.rotation || 0);
  renderPagePreview(page, visualCrop);
  $("#zoom-label").textContent = `${Math.round(state.zoom * 100)}%`;
  if (state.cropEditing) {
    if (state.cropDraftPageId !== page.id) loadCropDraft(page);
    else renderCropOverlay();
  }
  updateCropVisibility();
}

async function rotate(degrees) {
  const page = selectedPage(); if (!page) return;
  if (state.session?.status === "saved" || state.rotationPending) return;
  const pageIds = selectedPageIdsInOrder() || [page.id];
  state.rotationPending = true;
  renderSelectedPage();
  try {
    state.session = await api(`/api/v1/sessions/${state.session.id}/pages/rotate`, {
      method: "POST", body: JSON.stringify({ pageIds, degrees })
    });
    stopCropEditing();
    renderSession();
    toast(pageIds.length === 1 ? "Page rotated" : `${pageIds.length} selected pages rotated`);
  } catch (error) { showError(error); }
  finally { state.rotationPending = false; renderSelectedPage(); }
}

function toggleCrop() {
  if (!selectedPage() || state.session?.status === "saved") return;
  if (state.cropEditing) stopCropEditing();
  else {
    state.cropEditing = true;
    loadCropDraft(selectedPage());
  }
  renderSelectedPage();
}

function updateCropVisibility() {
  $("#crop-controls").hidden = !state.cropEditing;
  $("#crop-overlay").hidden = !state.cropEditing || !selectedPage();
  $("#crop-toggle").classList.toggle("active", state.cropEditing);
  renderCropScope();
  if (state.cropEditing) renderCropOverlay();
}

function stopCropEditing() {
  state.cropEditing = false;
  state.cropDraft = null;
  state.cropDraftPageId = null;
  state.cropDrag = null;
  $("#crop-overlay").classList.remove("dragging");
}

function loadCropDraft(page) {
  if (!page) return stopCropEditing();
  state.cropDraft = sourceToVisualCrop(page.crop, page.rotation || 0);
  state.cropDraftPageId = page.id;
  renderCropOverlay();
}

function resetCropDraft() {
  if (!state.cropEditing) return;
  state.cropDraft = { x: 0, y: 0, width: 1, height: 1 };
  renderCropOverlay();
}

function renderCropScope() {
  const count = state.selectedPageIds.size;
  const option = $("#crop-selected-option");
  option.hidden = !state.cropEditing || count < 2;
  $("#crop-selected-label").textContent = "Apply to selection";
  if (option.hidden) $("#crop-apply-selected").checked = false;
}

function renderCropOverlay() {
  if (!state.cropEditing || !state.cropDraft) return;
  const crop = state.cropDraft, overlay = $("#crop-overlay");
  overlay.style.left = `${crop.x * 100}%`;
  overlay.style.top = `${crop.y * 100}%`;
  overlay.style.width = `${crop.width * 100}%`;
  overlay.style.height = `${crop.height * 100}%`;
}

function sourceToVisualCrop(crop = { x: 0, y: 0, width: 1, height: 1 }, rotation = 0) {
  const value = crop || { x: 0, y: 0, width: 1, height: 1 };
  return rotation === 90
    ? { x: 1 - value.y - value.height, y: value.x, width: value.height, height: value.width }
    : rotation === 180
      ? { x: 1 - value.x - value.width, y: 1 - value.y - value.height, width: value.width, height: value.height }
      : rotation === 270
        ? { x: value.y, y: 1 - value.x - value.width, width: value.height, height: value.width }
        : { ...value };
}

function isFullCrop(crop) {
  return !crop || (crop.x <= .000001 && crop.y <= .000001 && crop.width >= .999999 && crop.height >= .999999);
}

function rememberPageDimensions(page, image) {
  if (!image?.naturalWidth || !image?.naturalHeight) return;
  const dimensions = { width: image.naturalWidth, height: image.naturalHeight };
  state.pageDimensions.set(page.id, dimensions);
  page.pixelWidth = dimensions.width;
  page.pixelHeight = dimensions.height;
}

function pagePreviewMetrics(page, image = null) {
  const rotation = ((page.rotation || 0) % 360 + 360) % 360;
  const rotated = rotation === 90 || rotation === 270;
  const cached = state.pageDimensions.get(page.id);
  const pixelWidth = Number(page.pixelWidth) || cached?.width || image?.naturalWidth || 680;
  const pixelHeight = Number(page.pixelHeight) || cached?.height || image?.naturalHeight || 880;
  const previewScale = 880 / Math.max(pixelWidth, pixelHeight);
  const sourceWidth = pixelWidth * previewScale;
  const sourceHeight = pixelHeight * previewScale;
  return {
    rotation,
    pixelWidth,
    pixelHeight,
    sourceWidth,
    sourceHeight,
    visualWidth: rotated ? sourceHeight : sourceWidth,
    visualHeight: rotated ? sourceWidth : sourceHeight
  };
}

function renderPagePreview(page, crop, loadedImage = null) {
  const stage = $("#page-stage"), frame = $("#page-image-frame"), image = $("#page-image");
  const metrics = pagePreviewMetrics(page, loadedImage), normalisedCrop = normaliseCrop(crop);
  const fullWidth = metrics.visualWidth * state.zoom;
  const fullHeight = metrics.visualHeight * state.zoom;

  stage.style.width = `${fullWidth * normalisedCrop.width}px`;
  stage.style.height = `${fullHeight * normalisedCrop.height}px`;
  stage.classList.toggle("rotated", metrics.rotation === 90 || metrics.rotation === 270);
  stage.classList.toggle("cropped", !isFullCrop(normalisedCrop));

  frame.style.width = `${fullWidth}px`;
  frame.style.height = `${fullHeight}px`;
  frame.style.left = `${-normalisedCrop.x * fullWidth}px`;
  frame.style.top = `${-normalisedCrop.y * fullHeight}px`;
  image.style.width = `${metrics.sourceWidth * state.zoom}px`;
  image.style.height = `${metrics.sourceHeight * state.zoom}px`;
  image.style.transform = `translate(-50%, -50%) rotate(${metrics.rotation}deg)`;
}

function renderThumbnailPreview(button, page, loadedImage = null) {
  const paper = button.querySelector(".thumb-paper");
  const frame = button.querySelector(".thumb-image-frame");
  const image = frame.querySelector("img");
  const metrics = pagePreviewMetrics(page, loadedImage);
  const crop = normaliseCrop(sourceToVisualCrop(page.crop, metrics.rotation));
  const viewportWidth = 40, viewportHeight = 54;
  const scale = Math.min(
    viewportWidth / (metrics.visualWidth * crop.width),
    viewportHeight / (metrics.visualHeight * crop.height)
  );
  const fullWidth = metrics.visualWidth * scale;
  const fullHeight = metrics.visualHeight * scale;
  const marginLeft = (viewportWidth - fullWidth * crop.width) / 2;
  const marginTop = (viewportHeight - fullHeight * crop.height) / 2;

  paper.classList.toggle("cropped", !isFullCrop(crop));
  frame.style.width = `${fullWidth}px`;
  frame.style.height = `${fullHeight}px`;
  frame.style.left = `${marginLeft - crop.x * fullWidth}px`;
  frame.style.top = `${marginTop - crop.y * fullHeight}px`;
  image.style.width = `${metrics.sourceWidth * scale}px`;
  image.style.height = `${metrics.sourceHeight * scale}px`;
  image.style.transform = `translate(-50%, -50%) rotate(${metrics.rotation}deg)`;
}

function visualToSourceCrop(crop, rotation = 0) {
  return rotation === 90
    ? { x: crop.y, y: 1 - crop.x - crop.width, width: crop.height, height: crop.width }
    : rotation === 180
      ? { x: 1 - crop.x - crop.width, y: 1 - crop.y - crop.height, width: crop.width, height: crop.height }
      : rotation === 270
        ? { x: 1 - crop.y - crop.height, y: crop.x, width: crop.height, height: crop.width }
        : { ...crop };
}

function normaliseCrop(crop) {
  const round = value => Math.round(clamp(value, 0, 1) * 1e6) / 1e6;
  const x = round(crop.x), y = round(crop.y);
  return {
    x,
    y,
    width: round(Math.min(crop.width, 1 - x)),
    height: round(Math.min(crop.height, 1 - y))
  };
}

function beginCropDrag(event) {
  if (!state.cropEditing || !state.cropDraft || event.button !== 0) return;
  const handle = event.target.closest(".crop-handle")?.dataset.cropHandle || "move";
  state.cropDrag = {
    pointerId: event.pointerId,
    handle,
    startX: event.clientX,
    startY: event.clientY,
    start: { ...state.cropDraft }
  };
  $("#crop-overlay").classList.add("dragging");
  $("#crop-overlay").setPointerCapture?.(event.pointerId);
  event.preventDefault();
}

function updateCropDrag(event) {
  const drag = state.cropDrag;
  if (!drag || event.pointerId !== drag.pointerId) return;
  const bounds = $("#page-stage").getBoundingClientRect();
  if (!bounds.width || !bounds.height) return;
  const dx = (event.clientX - drag.startX) / bounds.width;
  const dy = (event.clientY - drag.startY) / bounds.height;
  const minimum = .05, start = drag.start;
  let crop = { ...start };

  if (drag.handle === "move") {
    crop.x = clamp(start.x + dx, 0, 1 - start.width);
    crop.y = clamp(start.y + dy, 0, 1 - start.height);
  } else {
    if (drag.handle.includes("w")) {
      const right = start.x + start.width;
      crop.x = clamp(start.x + dx, 0, right - minimum);
      crop.width = right - crop.x;
    }
    if (drag.handle.includes("e")) {
      crop.width = clamp(start.width + dx, minimum, 1 - start.x);
    }
    if (drag.handle.includes("n")) {
      const bottom = start.y + start.height;
      crop.y = clamp(start.y + dy, 0, bottom - minimum);
      crop.height = bottom - crop.y;
    }
    if (drag.handle.includes("s")) {
      crop.height = clamp(start.height + dy, minimum, 1 - start.y);
    }
  }

  state.cropDraft = normaliseCrop(crop);
  renderCropOverlay();
  event.preventDefault();
}

function endCropDrag(event) {
  if (!state.cropDrag || event.pointerId !== state.cropDrag.pointerId) return;
  const overlay = $("#crop-overlay");
  if (overlay.hasPointerCapture?.(event.pointerId)) overlay.releasePointerCapture(event.pointerId);
  overlay.classList.remove("dragging");
  state.cropDrag = null;
}

async function applyCrop() {
  const page = selectedPage();
  if (!page || !state.cropDraft) return;
  const crop = normaliseCrop(visualToSourceCrop(state.cropDraft, page.rotation || 0));
  if (crop.width <= .01 || crop.height <= .01) return toast("The crop area is too small", true);
  const applyToSelection = $("#crop-apply-selected").checked && state.selectedPageIds.size > 1;
  const pageIds = applyToSelection ? selectedPageIdsInOrder() : [page.id];
  try {
    state.session = applyToSelection
      ? await api(`/api/v1/sessions/${state.session.id}/pages/crop`, {
        method: "POST", body: JSON.stringify({ pageIds, ...crop })
      })
      : await api(`/api/v1/sessions/${state.session.id}/pages/${page.id}/crop`, {
        method: "POST", body: JSON.stringify(crop)
      });
    stopCropEditing();
    renderSession();
    toast(applyToSelection ? `Crop applied to ${pageIds.length} selected pages` : "Crop applied to this page");
  } catch (error) { showError(error); }
}

function isTypingTarget(target) {
  return target instanceof Element &&
    (target.matches("input, textarea, select") || target.closest("[contenteditable='true']"));
}

function selectAllPages() {
  const pages = state.session?.pages || [];
  if (!pages.length) return;
  state.selectedPageIds = new Set(pages.map(page => page.id));
  state.selectedPageId ||= pages[0].id;
  state.selectionAnchorId = pages[0].id;
  stopCropEditing();
  renderSession();
}

function movePageSelection(offset, extendRange = false) {
  const pages = state.session?.pages || [];
  if (!pages.length) return;
  const currentIndex = Math.max(0, pages.findIndex(page => page.id === state.selectedPageId));
  const target = pages[clamp(currentIndex + offset, 0, pages.length - 1)];
  if (!target || target.id === state.selectedPageId) return;
  selectPage({ shiftKey: extendRange, metaKey: false, ctrlKey: false }, target.id);
}

function cancelCropOrSelection() {
  if (state.cropEditing) {
    stopCropEditing();
    renderSelectedPage();
  } else if (state.selectedPageIds.size) {
    clearPageSelection();
  } else if (state.failedJob) {
    dismissScanError();
  }
}

function handleKeyboardShortcut(event) {
  const modifier = event.metaKey || event.ctrlKey;
  const key = event.key.toLowerCase();
  const typing = isTypingTarget(event.target);
  if (profileOverlayOpen()) {
    if (event.key === "Escape") {
      event.preventDefault();
      if (!$("#profile-dialog").hidden) closeProfileEditor();
      else closeProfileOverlay();
    }
    return;
  }

  if (modifier && !event.altKey) {
    if (["+", "="].includes(event.key)) {
      event.preventDefault();
      setZoom(state.zoom + .08);
      return;
    }
    if (["-", "_"].includes(event.key)) {
      event.preventDefault();
      setZoom(state.zoom - .08);
      return;
    }
    if (event.repeat) return;
    if (key === "n" && !event.shiftKey) {
      event.preventDefault();
      newSession();
      return;
    }
    if (key === "s") {
      event.preventDefault();
      if (event.shiftKey) downloadDocument();
      else saveDocument();
      return;
    }
    if (key === "enter") {
      event.preventDefault();
      startOrCancelScan();
      return;
    }
    if (key === "a" && !event.shiftKey && !typing) {
      event.preventDefault();
      selectAllPages();
      return;
    }
  }

  if (typing || event.repeat) return;
  if (event.key === "Escape") {
    event.preventDefault();
    cancelCropOrSelection();
    return;
  }
  if (state.cropEditing && event.key === "Enter") {
    event.preventDefault();
    applyCrop();
    return;
  }
  if (key === "c" && !modifier && !event.altKey) {
    event.preventDefault();
    toggleCrop();
    return;
  }
  if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
    event.preventDefault();
    movePageSelection(event.key === "ArrowLeft" ? -1 : 1, event.shiftKey);
    return;
  }
  if (event.key === "[" || event.key === "]") {
    event.preventDefault();
    rotate(event.key === "[" ? -90 : 90);
    return;
  }
  if (["Backspace", "Delete"].includes(event.key) && !state.activeJob && !state.pageDeletePending &&
      state.session?.status !== "saved" && selectedPage()) {
    event.preventDefault();
    removePage(false);
  }
}

async function removePage(confirmRemoval = true) {
  const page = selectedPage();
  if (!page || state.pageDeletePending || (confirmRemoval && !confirm(`Remove page ${page.number} from this document?`))) return;
  const removedNumber = page.number;
  state.pageDeletePending = true;
  try {
    await api(`/api/v1/sessions/${state.session.id}/pages/${page.id}`, { method: "DELETE" });
    state.session = await api(`/api/v1/sessions/${state.session.id}`);
    state.selectedPageIds.delete(page.id);
    state.selectedPageId = state.session.pages[0]?.id || null;
    state.selectionAnchorId = state.selectedPageId; stopCropEditing(); renderSession();
    toast(`Page ${removedNumber} removed`);
  } catch (error) { showError(error); }
  finally { state.pageDeletePending = false; }
}

function handleCanvasWheel(event) {
  if (!selectedPage() || event.deltaY === 0) return;
  event.preventDefault();
  const modeScale = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? 240 : 1;
  const zoomDelta = clamp(event.deltaY * modeScale * .0008, -.08, .08);
  const stage = $("#page-stage");
  stage.classList.add("wheel-zooming");
  clearTimeout(state.wheelZoomTimer);
  state.wheelZoomTimer = setTimeout(() => stage.classList.remove("wheel-zooming"), 120);
  setZoom(state.zoom - zoomDelta);
}

function setZoom(value) { state.zoom = clamp(value, .4, 1.08); renderSelectedPage(); }
function clamp(value, min, max) { return Math.min(max, Math.max(min, value)); }

function formatDetails() {
  return ({
    pdf: { key: "pdf", label: "PDF", extension: ".pdf" },
    tiff: { key: "tiff", label: "TIFF", extension: ".tiff" },
    "zip-png": { key: "zip-png", label: "ZIP · PNG", extension: ".zip" },
    "zip-jpeg": { key: "zip-jpeg", label: "ZIP · JPEG", extension: ".zip" }
  })[$("#output-format").value] || { key: "pdf", label: "PDF", extension: ".pdf" };
}

function updateOutputFormat() {
  const format = formatDetails(), saved = state.session?.status === "saved";
  $("#output-extension").textContent = format.extension;
  $("#save-document").textContent = saved ? "Saved" : "Save";
  $("#download-document").textContent = "Download";
}

function scheduleTitleSave() {
  const title = $("#document-title").value;
  if (!state.outputNameTouched) $("#output-filename").value = defaultOutputStem(title);
  clearTimeout(state.titleTimer);
  state.titleTimer = setTimeout(async () => {
    try {
      state.session = await api(`/api/v1/sessions/${state.session.id}`, {
        method: "PATCH", body: JSON.stringify({ title: $("#document-title").value })
      });
      $("#document-status").textContent = state.session.pages.length
        ? `${state.session.pages.length} ${state.session.pages.length === 1 ? "page" : "pages"} ready to save`
        : "Building document";
    } catch (error) { showError(error); }
  }, 450);
}

async function newSession() {
  if (state.activeJob) return toast("Finish or cancel the current scan first", true);
  try {
    state.session = await api("/api/v1/sessions", { method: "POST", body: JSON.stringify({ title: null }) });
    state.selectedPageId = null; state.selectedPageIds.clear(); state.selectionAnchorId = null; state.pageDimensions.clear();
    stopCropEditing(); state.outputNameTouched = false;
    renderSession(); setTab("inspector"); toast("New document started");
  } catch (error) { showError(error); }
}

async function saveDocument() {
  if (state.session.status === "saved") return toast("This document is already saved", true);
  if (!state.session.pages.length) return toast("Scan at least one page before saving", true);
  const title = $("#document-title").value.trim() || state.session.title || defaultDocumentTitle();
  const fileName = $("#output-filename").value.trim();
  const format = formatDetails();
  if (!fileName) return toast("Enter a file name before saving", true);
  clearTimeout(state.titleTimer);
  const submit = $("#save-document"); submit.disabled = true; submit.textContent = "Saving…";
  try {
    const pageIds = selectedPageIdsInOrder();
    const sourceSessionId = state.session.id;
    const result = await api(`/api/v1/sessions/${state.session.id}/save`, { method: "POST", body: JSON.stringify({
      title, fileName, format: format.key, pageIds
    }) });
    state.session = await api(`/api/v1/sessions/${sourceSessionId}`);
    if (pageIds) state.selectedPageIds.clear();
    renderSession();
    await loadHistory(); toast(`${result.fileName} saved to the output mount`);
  } catch (error) { showError(error); }
  finally {
    const saved = state.session?.status === "saved";
    submit.disabled = saved;
    updateOutputFormat();
  }
}

async function downloadDocument() {
  if (!state.session.pages.length) return toast("Scan at least one page before downloading", true);
  const title = $("#document-title").value.trim() || state.session.title || defaultDocumentTitle();
  const fileName = $("#output-filename").value.trim();
  const format = formatDetails();
  if (!fileName) return toast("Enter a file name before downloading", true);

  const button = $("#download-document");
  button.disabled = true; button.textContent = "Preparing…";
  try {
    const response = await fetch(`/api/v1/sessions/${state.session.id}/download`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title, fileName, format: format.key, pageIds: selectedPageIdsInOrder() })
    });
    if (!response.ok) {
      let detail = `${response.status} ${response.statusText}`;
      try { detail = (await response.json()).detail || detail; } catch { /* response was not JSON */ }
      throw new Error(detail);
    }

    const blob = await response.blob();
    const disposition = response.headers.get("content-disposition") || "";
    const encodedName = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
    const basicName = disposition.match(/filename="?([^";]+)"?/i)?.[1];
    const downloadName = encodedName ? decodeURIComponent(encodedName) : basicName || `${slug(fileName)}${format.extension}`;
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url; link.download = downloadName; link.hidden = true;
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast(`${downloadName} downloaded`);
  } catch (error) { showError(error); }
  finally { button.disabled = false; updateOutputFormat(); }
}

async function loadHistory() {
  const history = await api("/api/v1/history"), list = $("#history-list"); list.replaceChildren();
  if (!history.length) {
    const empty = document.createElement("div"); empty.className = "filmstrip-empty";
    empty.textContent = "Saved documents will appear here."; list.append(empty); return;
  }
  for (const document of history) {
    const item = window.document.createElement("a"); item.className = "history-item";
    item.href = `/api/v1/documents/${encodeURIComponent(document.outputFileName)}`; item.target = "_blank";
    const extension = document.outputFileName.split(".").pop()?.toUpperCase() || "FILE";
    item.innerHTML = `<span class="history-icon">${escapeHtml(extension)}</span><span><strong>${escapeHtml(document.title)}</strong><small>${document.pages.length} pages · ${new Date(document.savedAt).toLocaleDateString()}</small></span><span class="history-arrow">↗</span>`;
    list.append(item);
  }
}

function setTab(tab) {
  const history = tab === "history";
  $("#inspector-panel").hidden = history; $("#history-panel").hidden = !history;
  $("#history-tab").classList.toggle("active", history);
  $("#history-tab").setAttribute("aria-pressed", String(history));
}

function toggleHistory() {
  setTab($("#history-panel").hidden ? "history" : "inspector");
}

function slug(value) {
  return value.toLowerCase().trim().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") ||
    defaultDocumentTitle().slice(5).replace(/[-:]/g, "").replace(/^/, "scan-");
}

function escapeHtml(value) {
  return String(value).replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
}

let toastTimer;
function toast(message, error = false) {
  const element = $("#toast"); clearTimeout(toastTimer); element.textContent = message;
  element.classList.toggle("error", error); element.classList.add("show");
  toastTimer = setTimeout(() => element.classList.remove("show"), 3200);
}
function showError(error) { console.error(error); toast(error.message || "Something went wrong", true); }

initialise();
