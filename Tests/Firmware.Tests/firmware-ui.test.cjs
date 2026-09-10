const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const source = fs.readFileSync(path.resolve(__dirname, "../../Static/firmware.js"), "utf8");
const markup = fs.readFileSync(path.resolve(__dirname, "../../Slices/Firmware.cshtml"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));

class Element {
    constructor() {
        this.children = [];
        this.handlers = {};
        this.dataset = {};
        this.style = {};
        this.textContent = "";
        this.className = "button";
        this.checked = false;
        this.disabled = false;
        this.hidden = false;
    }
    set innerHTML(_) { throw new Error("Untrusted firmware content must not be rendered as HTML."); }
    replaceChildren(...children) { this.children = children; }
    append(...children) { this.children.push(...children); }
    addEventListener(name, handler) { this.handlers[name] = handler; }
    removeAttribute(name) { delete this[name]; }
    fire(name) { if (!this.disabled) { return this.handlers[name]?.(); } }
}

async function page(minimumSupportedVersion = "1.75.0", storage = new Map(), includeExperimental = true) {
    const elements = new Map([...markup.matchAll(/id="([^"]+)"/g)].map(match => [match[1], new Element()]));
    const created = [];
    const timers = [];
    const requests = [];
    const confirmations = [];
    const state = {
        offline: false,
        startError: null,
        includeExperimental,
        view: { connected: true, currentVersion: "1.75.0", model: "RT4K CE", progress: { phase: "Idle", message: "Ready.", active: false, canCancel: false, needsRecovery: false, bytes: 0, totalBytes: null } }
    };
    const releases = [
        { id: "experimental-1.77.0", version: "1.77.0", experimental: true, date: "2026-08-28", changelog: "<img src=x onerror=bad()>" },
        { id: "experimental-1.75.0", version: "1.75.0", experimental: true, date: "2026-08-16", changelog: "Current notes" },
        { id: "release-1.9.6", version: "1.9.6", experimental: false, date: "2025-11-04", changelog: "Stable notes" }
    ];
    vm.runInNewContext(source, {
        document: {
            getElementById: id => elements.get(id),
            createElement: () => { const element = new Element(); created.push(element); return element; },
            querySelectorAll: () => created.filter(element => element.dataset.firmwareId)
        },
        AbortSignal,
        console,
        localStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) },
        setTimeout: callback => timers.push(callback),
        confirm: message => { confirmations.push(message); return true; },
        fetch: async (url, options) => {
            requests.push({ url, options });
            let data;
            if (url.startsWith("/Firmware/releases")) { data = { releases, minimumSupportedVersion, includeExperimental: state.includeExperimental }; }
            else if (url === "/Firmware/status") {
                if (state.offline) { throw new Error("Network disconnected"); }
                data = structuredClone(state.view);
            } else if (url.startsWith("/Firmware/start")) {
                if (state.startError) {
                    state.view.progress = { ...state.view.progress, phase: "Failed", message: state.startError, active: false, canCancel: false };
                    return { ok: false, text: async () => state.startError };
                }
                state.view.progress = { ...state.view.progress, phase: "Downloading", active: true, canCancel: true };
            } else if (url === "/Firmware/cancel") {
                state.view.progress = { ...state.view.progress, phase: "Canceled", active: false, canCancel: false };
            } else { throw new Error("Unexpected request " + url); }
            return { ok: true, json: async () => data, text: async () => "" };
        }
    });
    await tick();
    return { elements, requests, confirmations, state, async poll() { await timers.shift()(); await tick(); } };
}

test("default experimental latest, version anchors, changelog text and stable filtering", async () => {
    const { elements, state } = await page();
    assert.equal(elements.has("includeExperimental"), false);
    assert.match(elements.get("latestFirmware").children[0].textContent, /1\.77\.0/);
    assert.equal(elements.get("currentFirmware").children[0].href, "#firmware-experimental-1.75.0");
    assert.equal(elements.get("firmwareReleases").children[0].children[1].textContent, "<img src=x onerror=bad()>");
    state.includeExperimental = false;
    await elements.get("refreshFirmware").fire("click");
    assert.match(elements.get("latestFirmware").children[0].textContent, /1\.9\.6/);
    assert.match(elements.get("installLatest").textContent, /Unavailable.*1\.75\.0/);
    assert.equal(elements.get("installLatest").disabled, true);
    assert.equal(elements.get("firmwareReleases").children.length, 2, "Current experimental notes should remain linkable when filtered out");
    assert.equal(elements.get("firmwareReleases").children[1].children[2].children[0].hidden, true, "Filtered experimental reinstall action must be hidden");
});

test("saved stable preference applies on first load and checkbox lives in Settings", async () => {
    const settings = fs.readFileSync(path.resolve(__dirname, "../../Slices/Settings.cshtml"), "utf8");
    assert.match(settings, /id="IncludeExperimentalFirmware"[^>]*onchange="sendCheckboxState\(this\)"/);
    assert.match(markup, /href="\/Settings"/);
    const p = await page("1.75.0", new Map(), false);
    assert.match(p.elements.get("latestFirmware").children[0].textContent, /1\.9\.6/);
    assert.ok(p.requests.some(request => request.url === "/Firmware/releases"));
    const hidden = p.elements.get("firmwareReleases").children[1].children[2].children[0];
    await hidden.handlers.click();
    assert.equal(p.confirmations.length, 0);
});

test("older history targets cannot install even if their disabled click handler is invoked", async () => {
    const p = await page();
    const sections = p.elements.get("firmwareReleases").children;
    const minimum = sections.find(section => section.id === "firmware-experimental-1.75.0").children[2].children[0];
    const older = sections.find(section => section.id === "firmware-release-1.9.6").children[2].children[0];
    assert.equal(minimum.disabled, false);
    assert.equal(older.disabled, true);
    assert.match(older.textContent, /requires 1\.75\.0/);
    await older.handlers.click();
    assert.equal(p.confirmations.length, 0);
    assert.equal(p.requests.filter(request => request.url.startsWith("/Firmware/start")).length, 0);
});

test("minimum-version changes in the API automatically change UI eligibility", async () => {
    for (const minimumVersion of ["1.77.0", "1.78.0"]) {
        const p = await page(minimumVersion);
        const current = p.elements.get("firmwareReleases").children.find(section => section.id === "firmware-experimental-1.75.0");
        assert.equal(current.children[2].children[0].disabled, true);
        assert.ok(current.children[2].children[0].textContent.includes(minimumVersion));
        assert.equal(p.elements.get("installLatest").disabled, minimumVersion === "1.78.0");
    }
});

test("missing or malformed compatibility metadata disables installation", async () => {
    for (const minimum of [null, "unknown"]) {
        const p = await page(minimum);
        assert.equal(p.elements.get("installLatest").disabled, true);
        assert.equal(p.elements.get("firmwareCatalogError").hidden, false);
        assert.match(p.elements.get("firmwareCatalogError").textContent, /compatibility information/);
    }
});

test("single confirmation starts a server job with a same-origin header and cannot double-submit", async () => {
    const p = await page();
    p.elements.get("installLatest").fire("click");
    await tick();
    assert.equal(p.confirmations.length, 1);
    const starts = p.requests.filter(request => request.url.startsWith("/Firmware/start"));
    assert.equal(starts.length, 1);
    assert.match(starts[0].url, /confirmed=true/);
    assert.equal(starts[0].options.headers["X-RT4K-Firmware"], "1");
    assert.equal(p.elements.get("installLatest").disabled, true);
    assert.equal(p.elements.get("firmwarePhase").textContent, "Downloading", "Start should refresh status without waiting for a timer");
    assert.equal(p.elements.get("firmwareProgress").hidden, false);
    await p.poll();
    p.elements.get("installLatest").fire("click");
    assert.equal(p.confirmations.length, 1);
    assert.equal(p.elements.get("cancelFirmware").disabled, false);
});

test("transfer progress, noncancelable flash and network-loss warning", async () => {
    const p = await page();
    Object.assign(p.state.view.progress, { phase: "Transferring", active: true, canCancel: true, bytes: 50, totalBytes: 100 });
    await p.poll();
    assert.equal(p.elements.get("firmwareProgress").value, 50);
    assert.equal(p.elements.get("firmwareProgress").max, 100);
    assert.equal(p.elements.get("cancelFirmware").disabled, false);
    Object.assign(p.state.view.progress, { phase: "Flashing", canCancel: false, bytes: 0, totalBytes: null });
    await p.poll();
    assert.equal(p.elements.get("cancelFirmware").disabled, true);
    assert.equal(p.elements.get("firmwareProgress").value, undefined);
    p.state.offline = true;
    await p.poll();
    assert.equal(p.elements.get("firmwareConnectionError").hidden, false);
    assert.match(p.elements.get("firmwareConnectionError").textContent, /may still be running/);
    assert.equal(p.elements.get("installLatest").disabled, true);
    p.state.offline = false;
    Object.assign(p.state.view.progress, { phase: "Completed", active: false });
    await p.poll();
    assert.equal(p.elements.get("firmwarePhase").textContent, "Update complete");
    assert.equal(p.elements.get("firmwareConnectionError").hidden, true);
});

test("interrupted updates show automatic cleanup without recovery controls or flashing", async () => {
    const p = await page();
    Object.assign(p.state.view.progress, { phase: "WaitingForDevice", needsRecovery: true });
    await p.poll();
    assert.equal(p.elements.has("recoverFirmware"), false);
    assert.equal(p.elements.get("firmwarePhase").textContent, "Waiting for RT4K");
    assert.equal(p.elements.get("cancelFirmware").hidden, true);
    assert.equal(p.elements.get("installLatest").disabled, true);
    assert.equal(p.confirmations.length, 0);
    assert.equal(p.requests.filter(request => request.options.method === "POST").length, 0);
    assert.equal(p.requests.filter(request => request.url.startsWith("/Firmware/start")).length, 0);
});

test("unsupported models and disconnected cleanup block installation", async () => {
    const p = await page();
    p.state.view.model = "RT6X Pro";
    await p.poll();
    assert.equal(p.elements.get("installLatest").disabled, true);
    for (const section of p.elements.get("firmwareReleases").children) {
        assert.equal(section.children[2].children[0].disabled, true);
    }
    p.state.view.connected = false;
    Object.assign(p.state.view.progress, { phase: "WaitingForDevice", needsRecovery: true });
    await p.poll();
    assert.equal(p.elements.get("installLatest").disabled, true);
});

test("warning can be collapsed and expanded with its preference remembered", async () => {
    assert.match(markup, /<details[^>]*id="firmwareWarning"[^>]*open>/);
    assert.match(markup, /<summary[^>]*>[\s\S]*?keep the Pi and RT4K powered on/);
    const storage = new Map();
    const p = await page("1.75.0", storage);
    const warning = p.elements.get("firmwareWarning");
    assert.equal(warning.open, true);
    assert.equal(p.elements.get("firmwareWarningAction").textContent, "Collapse −");
    warning.open = false;
    warning.fire("toggle");
    assert.equal(p.elements.get("firmwareWarningAction").textContent, "Expand +");
    const reloaded = await page("1.75.0", storage);
    assert.equal(reloaded.elements.get("firmwareWarning").open, false);
    warning.open = true;
    warning.fire("toggle");
    assert.equal(storage.get("firmwareWarningCollapsed"), "0");
});

test("device and update status share an indented striped table under an external heading", () => {
    assert.match(markup, /<h2>Update status<\/h2>\s*<div class="w3-panel">\s*<table class="w3-table-all w3-card"/);
    const table = markup.match(/<table[\s\S]*?<\/table>/)[0];
    for (const id of ["firmwareModel", "currentFirmware", "latestFirmware", "firmwarePhase", "firmwareProgress"]) {
        assert.ok(table.includes(`id="${id}"`));
    }
    assert.match(markup, /<h2>Recent firmware and changelogs<\/h2>\s*<div class="w3-panel">/);
});

test("failed start immediately refreshes failure status instead of stale Preparing", async () => {
    const p = await page();
    p.state.startError = "Could not start the update. No firmware was sent.";
    p.elements.get("installLatest").fire("click");
    await tick();
    assert.equal(p.elements.get("firmwarePhase").textContent, "Update failed");
    assert.equal(p.elements.get("firmwareMessage").textContent, p.state.startError);
    assert.equal(p.elements.get("firmwareProgress").hidden, true);
    assert.equal(p.elements.get("cancelFirmware").hidden, true);
    assert.equal(p.elements.get("installLatest").disabled, false);
    await p.poll();
    assert.equal(p.requests.filter(request => request.url.startsWith("/Firmware/start")).length, 1);
});

test("cancel immediately refreshes status without reloading", async () => {
    const p = await page();
    Object.assign(p.state.view.progress, { phase: "Preparing", active: true, canCancel: true });
    await p.poll();
    assert.equal(p.elements.get("firmwareProgress").hidden, false);
    assert.equal(p.elements.get("firmwareProgress").value, undefined);
    assert.equal(p.elements.get("cancelFirmware").disabled, false);
    await p.elements.get("cancelFirmware").fire("click");
    assert.equal(p.elements.get("firmwarePhase").textContent, "Update canceled");
    assert.equal(p.elements.get("firmwareProgress").hidden, true);
});
