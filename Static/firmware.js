(() => {
    "use strict";
    const element = id => document.getElementById(id);
    const buttonClasses = element("installLatest").className;
    let releases = [];
    let includeExperimental = true;
    let minimumSupportedVersion = null;
    let visibleCount = 8;
    let view = null;
    let statusKnown = false;
    let catalogReady = false;
    let pendingAction = false;
    let renderedVersion;
    let statusReads = Promise.resolve();

    const warning = element("firmwareWarning");
    if (warning) {
        try { warning.open = localStorage.getItem("firmwareWarningCollapsed") !== "1"; } catch { }
        const updateWarningAction = () => { element("firmwareWarningAction").textContent = warning.open ? "−" : "+"; };
        updateWarningAction();
        warning.addEventListener("toggle", () => {
            updateWarningAction();
            try { localStorage.setItem("firmwareWarningCollapsed", warning.open ? "0" : "1"); } catch { }
        });
    }

    function error(id, message) {
        element(id).textContent = message || "";
        element(id).hidden = !message;
    }

    async function request(url, options = {}) {
        const response = await fetch(url, { cache: "no-store", signal: AbortSignal.timeout(60000), ...options });
        if (!response.ok) {
            throw new Error((await response.text()) || `Request failed (${response.status}).`);
        }
        return response;
    }

    function filtered() {
        return releases.filter(release => includeExperimental || !release.experimental);
    }

    function currentRelease() {
        return releases.find(release => release.version === view?.currentVersion);
    }

    function versionLink(container, release, label) {
        container.replaceChildren();
        if (!release) { container.textContent = label; return; }
        const link = document.createElement("a");
        link.href = `#firmware-${release.id}`;
        link.textContent = label;
        container.append(link);
    }

    function compareVersions(left, right) {
        const a = left.split(".").map(Number);
        const b = right.split(".").map(Number);
        for (let i = 0; i < Math.max(a.length, b.length); i++) {
            const difference = (a[i] || 0) - (b[i] || 0);
            if (difference) { return difference; }
        }
        return 0;
    }

    function actionLabel(release) {
        if (!isSupported(release)) { return minimumSupportedVersion ? `Unavailable (requires ${minimumSupportedVersion}+)` : "Compatibility unavailable"; }
        if (!view?.currentVersion) { return `Install ${release.version}`; }
        const order = compareVersions(release.version, view.currentVersion);
        return `${order < 0 ? "Downgrade to" : order === 0 ? "Reinstall" : "Update to"} ${release.version}`;
    }

    function isSupported(release) {
        return !!release && minimumSupportedVersion !== null && compareVersions(release.version, minimumSupportedVersion) >= 0;
    }

    function updateControls() {
        const blocked = !statusKnown || !catalogReady || pendingAction || !view?.connected || view?.progress.active || view?.progress.needsRecovery || !view?.currentVersion || !["RT4K Pro", "RT4K CE"].includes(view?.model);
        element("installLatest").disabled = blocked || !isSupported(filtered()[0]);
        for (const button of document.querySelectorAll("[data-firmware-id]")) {
            button.disabled = blocked || !isSupported(releases.find(release => release.id === button.dataset.firmwareId));
        }
        element("cancelFirmware").disabled = !statusKnown || pendingAction || !view?.progress.canCancel;
        element("cancelFirmware").hidden = !view?.progress.active;
    }

    function renderVersions() {
        const eligible = filtered();
        const latest = eligible[0];
        const current = currentRelease();
        const visible = eligible.slice(0, visibleCount);
        if (current && !visible.some(release => release.id === current.id)) { visible.push(current); }
        versionLink(element("currentFirmware"), current, view?.currentVersion || "Unavailable — power on the RT4K and check the serial connection.");
        versionLink(element("latestFirmware"), latest, latest ? `${latest.version} (${latest.experimental ? "Experimental" : "Release"}, ${latest.date})` : catalogReady ? "No releases available." : "Releases unavailable.");
        element("installLatest").textContent = latest ? actionLabel(latest) : "Install latest";
        element("firmwareReleases").replaceChildren();
        for (const release of visible) {
            const section = document.createElement("section");
            section.id = `firmware-${release.id}`;
            section.className = "w3-panel w3-card w3-white";
            const title = document.createElement("h3");
            title.textContent = `${release.version} — ${release.date}${release.experimental ? " (Experimental)" : " (Release)"}${release.version === view?.currentVersion ? " — Currently installed" : ""}`;
            const notes = document.createElement("div");
            notes.style.whiteSpace = "pre-wrap";
            notes.textContent = release.changelog;
            const actions = document.createElement("p");
            const button = document.createElement("button");
            button.type = "button";
            button.className = buttonClasses;
            button.dataset.firmwareId = release.id;
            button.textContent = actionLabel(release);
            button.hidden = release.experimental && !includeExperimental;
            button.addEventListener("click", () => start(release));
            actions.append(button);
            section.append(title, notes, actions);
            element("firmwareReleases").append(section);
        }
        element("moreFirmware").hidden = eligible.length <= visibleCount;
        updateControls();
    }

    async function loadReleases(forceRefresh = false) {
        element("refreshFirmware").disabled = true;
        catalogReady = false;
        minimumSupportedVersion = null;
        updateControls();
        error("firmwareCatalogError", null);
        try {
            const data = await (await request(forceRefresh ? "/Firmware/releases?refresh=true" : "/Firmware/releases")).json();
            if (typeof data.minimumSupportedVersion !== "string" || !/^\d+\.\d+(?:\.\d+){0,2}$/.test(data.minimumSupportedVersion)) {
                throw new Error("Firmware compatibility information is unavailable. Refresh the page before installing firmware.");
            }
            minimumSupportedVersion = data.minimumSupportedVersion;
            includeExperimental = data.includeExperimental !== false;
            releases = data.releases;
            catalogReady = true;
        } catch (failure) {
            error("firmwareCatalogError", failure.message);
        } finally {
            element("refreshFirmware").disabled = false;
            renderVersions();
        }
    }

    function formatBytes(bytes) {
        return `${(bytes / 1024 / 1024).toFixed(2)} MiB`;
    }

    async function readStatus() {
        try {
            view = await (await request("/Firmware/status", { signal: AbortSignal.timeout(8000) })).json();
            statusKnown = true;
            error("firmwareConnectionError", null);
            element("firmwareModel").textContent = view.model || (view.connected ? "Device is not reporting its model." : "Serial disconnected.");
            const status = view.progress;
            const phaseNames = { Idle: "Ready", Preparing: "Getting ready", Validating: "Checking download", Transferring: "Sending files", Checking: "Checking files", Publishing: "Getting ready to install", Flashing: "Installing", Rebooting: "Waiting for RT4K", WaitingForDevice: "Waiting for RT4K", CleanupRequired: "Waiting to clean up", Cleaning: "Cleaning up", Failed: "Update failed", Canceled: "Update canceled", Completed: "Update complete" };
            element("firmwarePhase").textContent = phaseNames[status.phase] || status.phase;
            element("firmwareTarget").textContent = status.version ? ` — ${status.version}` : "";
            if (element("firmwareMessage").textContent !== status.message) { element("firmwareMessage").textContent = status.message; }
            const progress = element("firmwareProgress");
            progress.hidden = !status.active;
            if (status.totalBytes > 0) {
                progress.max = status.totalBytes;
                progress.value = Math.min(status.bytes, status.totalBytes);
                element("firmwareBytes").textContent = `${formatBytes(status.bytes)} / ${formatBytes(status.totalBytes)} (${Math.floor(100 * status.bytes / status.totalBytes)}%)`;
            } else {
                progress.removeAttribute("value");
                element("firmwareBytes").textContent = status.active && status.bytes > 0 ? `${formatBytes(status.bytes)} received` : "";
            }
            if (renderedVersion !== view.currentVersion) {
                renderedVersion = view.currentVersion;
                renderVersions();
            }
        } catch (failure) {
            statusKnown = false;
            error("firmwareConnectionError", "Cannot reach the Pi. The update may still be running. Keep Pi and RT4K power connected; status will reconnect automatically. " + failure.message);
        } finally {
            updateControls();
        }
    }

    function refreshStatus() {
        const read = statusReads.then(readStatus);
        statusReads = read.catch(() => {});
        return read;
    }

    async function poll() {
        try { await refreshStatus(); }
        catch (failure) { console.error("Could not display firmware status:", failure); }
        finally { setTimeout(poll, 1000); }
    }

    async function mutate(url) {
        if (pendingAction) { return; }
        pendingAction = true;
        error("firmwareActionError", null);
        updateControls();
        try {
            await request(url, { method: "POST", headers: { "X-RT4K-Firmware": "1" } });
        } catch (failure) {
            error("firmwareActionError", failure.message);
        } finally {
            pendingAction = false;
            statusKnown = false;
            updateControls();
            await refreshStatus();
        }
    }

    async function start(release) {
        if (pendingAction || !statusKnown || !isSupported(release) || (release.experimental && !includeExperimental) || view?.progress.active || view?.progress.needsRecovery) { return; }
        if (!confirm(`${actionLabel(release)}${release.experimental ? " (experimental)" : ""}?\n\nKeep the Pi and RT4K powered on. You can cancel while files are downloading or being sent, but not once installation starts.`)) { return; }
        await mutate(`/Firmware/start?id=${encodeURIComponent(release.id)}&confirmed=true`);
    }

    element("refreshFirmware").addEventListener("click", () => loadReleases(true));
    element("moreFirmware").addEventListener("click", () => { visibleCount += 8; renderVersions(); });
    element("installLatest").addEventListener("click", () => { const latest = filtered()[0]; if (latest) { start(latest); } });
    element("cancelFirmware").addEventListener("click", () => mutate("/Firmware/cancel"));
    loadReleases();
    poll();
})();
