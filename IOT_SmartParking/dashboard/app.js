"use strict";

const MQTT_URL = "wss://broker.hivemq.com:8884/mqtt";
const MQTT_PREFIX = "dacn-smartparking/lot1";
const API_BASE_URL = "http://localhost:5137/api";
const API_REFRESH_INTERVAL = 15000;
const PARKING_FEE_PER_MINUTE = 2000;
const ESP32_OFFLINE_TIMEOUT = 12000;
const API_TOKEN_STORAGE_KEY = "smartParkingApiToken";
const API_AUTH_RETRY_COOLDOWN = 30000;
const TOPICS = {
    status: `${MQTT_PREFIX}/status`,
    slots: `${MQTT_PREFIX}/slots`,
    sessions: `${MQTT_PREFIX}/sessions`,
    session: `${MQTT_PREFIX}/session`,
    barrier: `${MQTT_PREFIX}/barrier`,
    heartbeat: `${MQTT_PREFIX}/heartbeat`,
    event: `${MQTT_PREFIX}/event`,
    entry: `${MQTT_PREFIX}/entry`,
    exit: `${MQTT_PREFIX}/exit`,
    recovery: `${MQTT_PREFIX}/recovery`,
    command: `${MQTT_PREFIX}/command`
};

const elements = {
    mqttDot: document.getElementById("mqttDot"),
    mqttText: document.getElementById("mqttText"),
    parkingGrid: document.getElementById("parkingGrid"),
    sessionsTable: document.getElementById("sessionsTable"),
    historyTable: document.getElementById("historyTable"),
    sensorHistoryTable: document.getElementById("sensorHistoryTable"),
    systemHistoryTable: document.getElementById("systemHistoryTable"),
    sessionCount: document.getElementById("sessionCount"),
    entrySpot: document.getElementById("entrySpot"),
    eventLog: document.getElementById("eventLog"),
    toast: document.getElementById("toast"),
    pageTitle: document.getElementById("pageTitle"),
    pageSubtitle: document.getElementById("pageSubtitle"),
    overviewGrid: document.querySelector(".dashboard-grid"),
    navItems: document.querySelectorAll(".nav-item[data-view]")
};

const apiData = {
    vehicles: [],
    sessions: [],
    sensorHistory: [],
    systemLogs: [],
    overview: null,
    revenueByDay: [],
    sessionsLoaded: false,
    sensorHistoryState: "loading",
    systemHistoryState: "loading"
};

let apiLoginPromise = null;
let apiLoginBlockedUntil = 0;

async function loginApi() {
    if (apiLoginPromise) return apiLoginPromise;
    if (Date.now() < apiLoginBlockedUntil) {
        throw new Error("Đăng nhập API đang tạm khóa thử lại.");
    }

    apiLoginPromise = (async () => {
        const username = window.prompt("Tên đăng nhập API:", "admin");
        if (!username) throw new Error("Đã hủy đăng nhập API.");

        const password = window.prompt("Mật khẩu API:");
        if (password === null) throw new Error("Đã hủy đăng nhập API.");

        try {
            const response = await fetch(`${API_BASE_URL}/auth/login`, {
                method: "POST",
                headers: { "Content-Type": "application/json", Accept: "application/json" },
                body: JSON.stringify({ username, password })
            });

            if (!response.ok) {
                throw new Error("Đăng nhập API thất bại.");
            }

            const data = await response.json();
            if (!data.accessToken) {
                throw new Error("API không trả về JWT.");
            }

            localStorage.setItem(API_TOKEN_STORAGE_KEY, data.accessToken);
        } catch (error) {
            apiLoginBlockedUntil = Date.now() + API_AUTH_RETRY_COOLDOWN;
            throw error;
        } finally {
            apiLoginPromise = null;
        }
    })();

    return apiLoginPromise;
}

async function apiGet(path, retry = true) {
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 7000);
    let response;
    const token = localStorage.getItem(API_TOKEN_STORAGE_KEY);

    try {
        response = await fetch(`${API_BASE_URL}${path}`, {
            method: "GET",
            headers: {
                Accept: "application/json",
                ...(token ? { Authorization: `Bearer ${token}` } : {})
            },
            signal: controller.signal
        });
    } finally {
        window.clearTimeout(timeout);
    }

    if (response.status === 401) {
        localStorage.removeItem(API_TOKEN_STORAGE_KEY);
        if (retry) {
            await loginApi();
            return apiGet(path, false);
        }
        throw new Error("API yêu cầu đăng nhập JWT.");
    }

    if (!response.ok) {
        throw new Error(`API ${path}: HTTP ${response.status}`);
    }

    return response.json();
}

function unwrapList(data) {
    if (Array.isArray(data)) return data;
    if (Array.isArray(data?.data)) return data.data;
    if (Array.isArray(data?.items)) return data.items;
    if (Array.isArray(data?.chiTiet)) return data.chiTiet;
    if (Array.isArray(data?.$values)) return data.$values;
    return [];
}

async function loadVehiclesFromApi() {
    apiData.vehicles = unwrapList(await apiGet("/phuong-tien"));
    return apiData.vehicles;
}

async function loadSessionsFromApi() {
    apiData.sessions = unwrapList(await apiGet("/phien-gui-xe"));
    apiData.sessionsLoaded = true;
    return apiData.sessions;
}

async function loadHistoryFromApi() {
    apiData.sensorHistoryState = "loading";
    apiData.systemHistoryState = "loading";
    const [sensorResult, systemResult] = await Promise.allSettled([
        apiGet("/lich-su/cam-bien?limit=50"),
        apiGet("/lich-su/he-thong?limit=50")
    ]);

    if (sensorResult.status === "fulfilled") {
        apiData.sensorHistory = unwrapList(sensorResult.value);
        apiData.sensorHistoryState = "ready";
    } else {
        apiData.sensorHistoryState = "error";
        console.error("[API] Không tải được lịch sử cảm biến:", sensorResult.reason);
    }

    if (systemResult.status === "fulfilled") {
        apiData.systemLogs = unwrapList(systemResult.value);
        apiData.systemHistoryState = "ready";
    } else {
        apiData.systemHistoryState = "error";
        console.error("[API] Không tải được nhật ký hệ thống:", systemResult.reason);
    }
}

async function loadOverviewFromApi() {
    apiData.overview = await apiGet("/thong-ke/tong-quan");
    return apiData.overview;
}

async function loadRevenueFromApi(fromDate, toDate) {
    const query = new URLSearchParams({
        tuNgay: fromDate,
        denNgay: toDate
    });

    const data = await apiGet(`/thong-ke/doanh-thu-theo-ngay?${query}`);
    apiData.revenueByDay = unwrapList(data);
    return apiData.revenueByDay;
}

async function refreshApiData() {
    const tasks = [
        ["phương tiện", loadVehiclesFromApi],
        ["phiên gửi xe", loadSessionsFromApi],
        ["lịch sử", loadHistoryFromApi],
        ["thống kê", loadOverviewFromApi]
    ];

    const results = await Promise.allSettled(tasks.map(([, load]) => load()));

    results.forEach((result, index) => {
        if (result.status === "rejected") {
            console.error(`[API] Không tải được ${tasks[index][0]}:`, result.reason);
        }
    });

    return results.some((result) => result.status === "fulfilled");
}

function getField(object, ...names) {
    for (const name of names) {
        if (object?.[name] !== undefined && object?.[name] !== null) {
            return object[name];
        }

        const foundKey = Object.keys(object || {}).find(
            (key) => key.toLowerCase() === name.toLowerCase()
        );

        if (foundKey) return object[foundKey];
    }

    return null;
}

function normalizePlate(plate) {
    return String(plate || "").trim().toUpperCase();
}

function getHistoryIdentity(sessionId, plate) {
    return `${String(sessionId ?? "").trim()}|${normalizePlate(plate)}`;
}

function formatApiDate(value) {
    if (!value) return "—";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString("vi-VN");
}

function formatApiMoney(value) {
    const amount = Number(value);
    return Number.isFinite(amount) ? `${amount.toLocaleString("vi-VN")} đ` : "—";
}

function renderApiTable(target, columns, rows) {
    if (!target) return;
    const table = target.closest("table") || target;
    const tbody = target.tagName === "TBODY" ? target : table.querySelector("tbody");

    if (!tbody) return;
    tbody.replaceChildren();

    if (!rows.length) {
        const tr = document.createElement("tr");
        const td = document.createElement("td");
        td.colSpan = columns.length;
        td.textContent = "Chưa có dữ liệu";
        tr.appendChild(td);
        tbody.appendChild(tr);
        return;
    }

    rows.forEach((item) => {
        const tr = document.createElement("tr");
        columns.forEach((column) => {
            const td = document.createElement("td");
            const value = column.value(item);
            td.textContent = value == null ? "—" : String(value);
            tr.appendChild(td);
        });
        tbody.appendChild(tr);
    });
}

let client = null;
let toastTimer = null;
let toastProtectedUntil = 0;
let lastESP32Heartbeat = 0;
let activeSessions = [];
const pendingExitFees = {};
let parkingHistory = [];
let slotsSynchronized = false;

try {
    const savedHistory = JSON.parse(localStorage.getItem("smartParkingHistory") || "[]");
    parkingHistory = Array.isArray(savedHistory) ? savedHistory : [];
} catch {
    localStorage.removeItem("smartParkingHistory");
}
let parkingSlots = Array.from({ length: 12 }, (_, index) => ({
    id: `P${String(index + 1).padStart(2, "0")}`,
    occupied: false,
    sessionId: -1,
    distance: 0
}));

function setMqttConnection(connected, label) {
    elements.mqttDot.className = `status-dot ${connected ? "online" : "offline"}`;
    elements.mqttText.textContent = label;
}

function updateBadge(id, label, tone) {
    const element = document.getElementById(id);
    element.textContent = label;
    element.className = `badge ${tone}`;
}

function renderParkingSlots() {
    renderEntrySpotOptions();
    const selectedSpotId = elements.entrySpot.value;
    elements.parkingGrid.replaceChildren();
    parkingSlots.forEach((slot) => {
        const card = document.createElement("article");
        const top = document.createElement("div");
        const id = document.createElement("strong");
        const state = document.createElement("span");
        const car = document.createElement("div");
        const details = document.createElement("div");
        const occupied = Boolean(slot.occupied);
        const reserved = !occupied && (slot.reserved === true || Number(slot.sessionId) >= 0);
        const selectable = slotsSynchronized && !occupied && !reserved;
        const distance = Number(slot.distance);

        card.className = `parking-space${occupied ? " occupied" : reserved ? " reserved" : ""}${selectable ? " selectable" : ""}${slot.id === selectedSpotId ? " selected" : ""}`;
        top.className = "space-top";
        id.className = "space-id";
        state.className = "space-state";
        car.className = "car-symbol";
        details.className = "space-info";
        id.textContent = slot.id || "P--";
        state.textContent = occupied ? "ĐÃ CÓ XE" : reserved ? "ĐÃ GIỮ CHỖ" : "CÒN TRỐNG";
        car.textContent = "🚗";
        details.textContent = reserved ? `Đã chọn cho phiên #${slot.sessionId}` : occupied && slot.virtual ? `Phiên #${slot.sessionId} • Đã xác nhận trên dashboard` : `${slot.sessionId >= 0 ? `Phiên #${slot.sessionId}` : "Chưa có xe"} • ${Number.isFinite(distance) ? distance.toFixed(1) : "-"} cm`;
        top.append(id, state);
        card.append(top, car, details);
        if (reserved) {
            const confirmButton = document.createElement("button");
            confirmButton.type = "button";
            confirmButton.className = "space-confirm-button";
            confirmButton.textContent = "Xác nhận xe đã đỗ";
            confirmButton.addEventListener("click", () => confirmParking(slot));
            card.appendChild(confirmButton);
        }
        if (selectable) {
            card.tabIndex = 0;
            card.setAttribute("role", "button");
            card.setAttribute("aria-label", `Chọn ô đỗ ${slot.id}`);
            card.addEventListener("click", () => selectEntrySpot(slot.id));
            card.addEventListener("keydown", (event) => {
                if (event.key === "Enter" || event.key === " ") {
                    event.preventDefault();
                    selectEntrySpot(slot.id);
                }
            });
        }
        elements.parkingGrid.appendChild(card);
    });
}

function renderEntrySpotOptions() {
    const previousValue = elements.entrySpot.value;
    const placeholder = document.createElement("option");
    const freeSlots = parkingSlots.filter((slot) => !slot.occupied && slot.reserved !== true && Number(slot.sessionId) < 0);

    elements.entrySpot.replaceChildren();
    placeholder.value = "";
    if (!slotsSynchronized) {
        placeholder.textContent = "Đang chờ dữ liệu vị trí đỗ...";
        elements.entrySpot.appendChild(placeholder);
        elements.entrySpot.disabled = true;
        return;
    }
    placeholder.textContent = freeSlots.length ? "Chọn một ô còn trống" : "Hiện không còn ô trống";
    elements.entrySpot.appendChild(placeholder);
    freeSlots.forEach((slot) => {
        const option = document.createElement("option");
        option.value = slot.id;
        option.textContent = `${slot.id} • ${Number(slot.distance).toFixed(1)} cm`;
        elements.entrySpot.appendChild(option);
    });
    elements.entrySpot.disabled = freeSlots.length === 0;
    if (freeSlots.some((slot) => slot.id === previousValue)) {
        elements.entrySpot.value = previousValue;
    }
}

function selectEntrySpot(spotId) {
    if (elements.entrySpot.disabled || ![...elements.entrySpot.options].some((option) => option.value === spotId)) {
        return;
    }
    elements.entrySpot.value = spotId;
    renderParkingSlots();
    showToast(`Đã chọn ô đỗ ${spotId}`);
}

function confirmParking(slot) {
    if (!slot?.id || Number(slot.sessionId) < 0) return;
    if (!confirm(`Xác nhận xe của phiên #${slot.sessionId} đã đỗ tại ${slot.id}?`)) return;
    if (publish(TOPICS.command, `CONFIRM_PARKING|${slot.id}`)) showToast(`Đang xác nhận xe đã đỗ tại ${slot.id}`);
}

function formatSessionStatus(status) {
    return { PARKING: "ĐANG CHỜ XE VÀO", EXITING: "ĐANG RA", PARKED: "ĐANG ĐỖ", COMPLETED: "ĐÃ HOÀN TẤT" }[status] || status;
}

function appendRealtimeSessionRow(session) {
    const row = document.createElement("tr");
    const status = String(session.status || "PARKED");
    const spots = Array.isArray(session.spots) && session.spots.length ? session.spots.join(", ") : String(session.reservedSpot || "-");
    const duration = calculateDuration(session.entryTime);
    const estimatedFee = getDisplayedFee(session);
    const values = [
        `#${Number(session.id) || "-"}`,
        String(session.plate || "-"),
        String(session.owner || "-"),
        spots,
        formatUnixTime(session.entryTime),
        duration,
        formatCurrency(estimatedFee)
    ];
    values.forEach((value, index) => {
        const cell = document.createElement("td");
        cell.textContent = value;
        if (index === 1) {
            cell.className = "plate";
        } else if (index === 5) {
            cell.className = "duration";
        } else if (index === 6) {
            cell.className = "fee";
        }
        row.appendChild(cell);
    });
    const statusCell = document.createElement("td");
    const statusBadge = document.createElement("span");
    statusBadge.className = `session-status ${status === "PARKING" ? "parking" : status === "EXITING" ? "exiting" : ""}`;
    statusBadge.textContent = formatSessionStatus(status);
    statusCell.appendChild(statusBadge);
    row.appendChild(statusCell);
    elements.sessionsTable.appendChild(row);
}

function renderSessions(sessions) {
    elements.sessionCount.textContent = `${sessions.length} xe`;
    elements.sessionsTable.replaceChildren();
    if (sessions.length === 0) {
        const row = document.createElement("tr");
        const cell = document.createElement("td");
        row.className = "empty-row";
        cell.colSpan = 8;
        cell.textContent = "Chưa có phiên gửi xe đang hoạt động";
        row.appendChild(cell);
        elements.sessionsTable.appendChild(row);
        return;
    }

    sessions.forEach(appendRealtimeSessionRow);
}

function getApiSessionStatus(session) {
    return String(getField(session, "trangThai") || "PARKING").toUpperCase();
}

function getApiVehicle(session) {
    const vehicleId = Number(getField(session, "maPhuongTien"));
    return apiData.vehicles.find(
        (vehicle) => Number(getField(vehicle, "maPhuongTien")) === vehicleId
    ) || null;
}

function getApiSessionSpots(session) {
    const apiSpots = unwrapList(getField(session, "viTriDos"))
        .map((spot) => getField(spot, "maViTri"))
        .filter(Boolean);

    if (apiSpots.length) return apiSpots.join(", ");

    const espSessionId = String(getField(session, "maPhienEsp32") || "");
    const realtime = activeSessions.find(
        (item) => String(item.id) === espSessionId
    );

    if (Array.isArray(realtime?.spots) && realtime.spots.length) {
        return realtime.spots.join(", ");
    }

    return realtime?.reservedSpot || "—";
}

function calculateApiDuration(entryTime) {
    const date = new Date(entryTime);
    if (Number.isNaN(date.getTime())) return "—";
    return calculateDuration(Math.floor(date.getTime() / 1000));
}

function getApiEstimatedFee(session) {
    const finalFee = Number(getField(session, "tongTien"));
    if (Number.isFinite(finalFee)) return finalFee;

    const entryTime = new Date(getField(session, "thoiGianVao"));
    const rate = Number(getField(session, "donGiaMoiPhut"));
    if (Number.isNaN(entryTime.getTime()) || !Number.isFinite(rate)) return null;

    const seconds = Math.max(0, Math.floor((Date.now() - entryTime.getTime()) / 1000));
    return Math.max(1, Math.ceil(seconds / 60)) * rate;
}

function renderSessionsFromApi() {
    const sessions = apiData.sessions.filter(
        (session) => getApiSessionStatus(session) !== "COMPLETED"
    );
    const apiEspSessionIds = new Set(
        sessions
            .map((session) => String(getField(session, "maPhienEsp32") || ""))
            .filter(Boolean)
    );
    const unsyncedRealtimeSessions = activeSessions.filter(
        (session) => !apiEspSessionIds.has(String(session.id))
    );

    elements.sessionCount.textContent = `${sessions.length + unsyncedRealtimeSessions.length} xe`;
    elements.sessionsTable.replaceChildren();

    if (!sessions.length && !unsyncedRealtimeSessions.length) {
        const row = document.createElement("tr");
        const cell = document.createElement("td");
        row.className = "empty-row";
        cell.colSpan = 8;
        cell.textContent = "Chưa có phiên gửi xe đang hoạt động";
        row.appendChild(cell);
        elements.sessionsTable.appendChild(row);
        return;
    }

    sessions.forEach((session) => {
        const row = document.createElement("tr");
        const vehicle = getApiVehicle(session);
        const status = getApiSessionStatus(session);
        const values = [
            `#${getField(session, "maPhien") || "-"}`,
            getField(session, "bienSo") || getField(vehicle, "bienSo") || "—",
            getField(session, "tenChuXe") || getField(vehicle, "tenChuXe") || "—",
            getApiSessionSpots(session),
            formatApiDate(getField(session, "thoiGianVao")),
            calculateApiDuration(getField(session, "thoiGianVao")),
            formatApiMoney(getApiEstimatedFee(session))
        ];

        values.forEach((value, index) => {
            const cell = document.createElement("td");
            cell.textContent = value;
            if (index === 1) cell.className = "plate";
            if (index === 5) cell.className = "duration";
            if (index === 6) cell.className = "fee";
            row.appendChild(cell);
        });

        const statusCell = document.createElement("td");
        const statusBadge = document.createElement("span");
        statusBadge.className = `session-status ${status === "PARKING" ? "parking" : status === "EXITING" ? "exiting" : ""}`;
        statusBadge.textContent = formatSessionStatus(status);
        statusCell.appendChild(statusBadge);
        row.appendChild(statusCell);
        elements.sessionsTable.appendChild(row);
    });

    unsyncedRealtimeSessions.forEach(appendRealtimeSessionRow);
}

function renderSessionSource() {
    if (apiData.sessionsLoaded) {
        renderSessionsFromApi();
        return;
    }

    renderSessions(activeSessions);
}

function renderOverviewFromApi() {
    const revenue = getCompletedHistory().reduce((total, session) => {
        const fee = Number(session.fee ?? getField(session, "tongTien"));
        return total + (Number.isFinite(fee) ? fee : 0);
    }, 0);
    document.getElementById("totalRevenue").textContent =
        formatApiMoney(revenue);
}

async function initializeApiDashboard() {
    await refreshApiData();

    renderSessionSource();
    renderHistory();
    renderSensorHistoryFromApi();
    renderSystemHistoryFromApi();
    renderOverviewFromApi();
    return true;
}

function requestApiRefresh() {
    window.setTimeout(() => {
        initializeApiDashboard();
    }, 1500);
}

function handleStatus(message) {
    if (message === "ONLINE") {
        updateBadge("wifiStatus", "TRỰC TUYẾN", "online");
        updateBadge("ntpStatus", "ĐANG ĐỒNG BỘ", "unknown");
        return;
    }
    try {
        const data = JSON.parse(message);
        document.getElementById("totalSpaces").textContent = data.total ?? 12;
        document.getElementById("availableSpaces").textContent = data.available ?? 0;
        document.getElementById("occupiedSpaces").textContent = data.occupied ?? 0;
        document.getElementById("vehicleCount").textContent = data.vehicles ?? 0;
        updateBadge("wifiStatus", data.wifi ? "TRỰC TUYẾN" : "NGOẠI TUYẾN", data.wifi ? "online" : "offline");
        updateBadge("ntpStatus", data.timeSynced ? "ĐÃ ĐỒNG BỘ" : "CHƯA ĐỒNG BỘ", data.timeSynced ? "synced" : "offline");
        updateBadge("barrierStatus", data.barrier === "OPEN" ? "MỞ" : "ĐÓNG", data.barrier === "OPEN" ? "open" : "closed");
        updateBadge("emergencyStatus", data.emergency ? "KHẨN CẤP" : "BÌNH THƯỜNG", data.emergency ? "emergency" : "safe");
    } catch (error) {
        addLog("LỖI", "Dữ liệu JSON không hợp lệ từ /status");
        console.error(error);
    }
}

function handleSlots(message) {
    try {
        const slots = JSON.parse(message);
        if (!Array.isArray(slots)) {
            return;
        }
        parkingSlots = slots;
        slotsSynchronized = true;
        renderParkingSlots();
    } catch (error) {
        addLog("LỖI", "Dữ liệu JSON không hợp lệ từ /slots");
        console.error(error);
    }
}

function handleSessions(message) {
    try {
        const sessions = JSON.parse(message);
        if (Array.isArray(sessions)) {
            activeSessions = sessions;
            renderSessionSource();
        }
    } catch (error) {
        addLog("LỖI", "Dữ liệu JSON không hợp lệ từ /sessions");
        console.error(error);
    }
}

function handleSessionEvent(message) {
    const parts = message.split("|");
    if (message.startsWith("ENTRY_OK")) {
        const sessionId = Number(parts[1]);
        const pickupCode = parts[4] || "";
        const spotPart = parts.find((part) => part.startsWith("SPOT="));
        const spot = spotPart ? spotPart.split("=")[1] : "ô đã chọn";
        const selectedSlot = parkingSlots.find((slot) => slot.id === spot);
        if (selectedSlot) {
            selectedSlot.sessionId = sessionId;
            selectedSlot.reserved = true;
            selectedSlot.occupied = false;
            renderParkingSlots();
        }
        showToast(`Đã tạo phiên và giữ chỗ ${spot}. Mã nhận xe: ${pickupCode}`, 15000, true);
        return;
    }
    if (message.startsWith("PARKED|")) {
        const sessionId = Number(parts[1]);
        const spotsPart = parts.find((part) => part.startsWith("SPOTS="));
        const confirmedSpots = spotsPart ? spotsPart.slice("SPOTS=".length).split(",") : [];
        confirmedSpots.forEach((spotId) => {
            const slot = parkingSlots.find((item) => item.id === spotId);
            if (slot) {
                slot.sessionId = sessionId;
                slot.reserved = false;
                slot.occupied = true;
            }
        });
        renderParkingSlots();
        showToast(`Đã xác nhận xe đỗ tại ${confirmedSpots.join(", ") || "ô đã chọn"}`);
        return;
    }
    if (message.startsWith("EXIT_OK")) {
        const sessionId = Number(parts[1]);
        const feePart = parts.find((item) => item.startsWith("FEE="));
        const reportedFee = feePart ? Number(feePart.split("=")[1]) : 0;
        const activeSession = activeSessions.find(
            (session) => Number(session.id) === sessionId &&
                normalizePlate(session.plate) === normalizePlate(parts[2])
        );
        const apiSession = apiData.sessions.find(
            (session) => Number(getField(session, "maPhienEsp32")) === sessionId &&
                normalizePlate(
                    getField(session, "bienSo") ||
                    getField(getApiVehicle(session), "bienSo")
                ) === normalizePlate(parts[2])
        );
        const apiEntryTime = new Date(getField(apiSession, "thoiGianVao") || 0);
        const entryTimestamp = Number(activeSession?.entryTime) ||
            (Number.isNaN(apiEntryTime.getTime())
                ? 0
                : Math.floor(apiEntryTime.getTime() / 1000));
        const elapsedSeconds = entryTimestamp > 0
            ? Math.max(0, Math.floor(Date.now() / 1000) - entryTimestamp)
            : 0;
        const elapsedFee = entryTimestamp > 0
            ? Math.max(1, Math.ceil(elapsedSeconds / 60)) * PARKING_FEE_PER_MINUTE
            : 0;
        const fee = Math.max(
            Number.isFinite(reportedFee) ? reportedFee : 0,
            elapsedFee
        );
        pendingExitFees[sessionId] = { sessionId, plate: parts[2] || "", fee, verifiedAt: Date.now() };
        showToast(parts.includes("RECOVERY_APPROVED") ? `Đã xác minh thông tin và cho xe ra • ${formatCurrency(fee)}` : `Đã xác thực xe ra • ${formatCurrency(fee)}`);
        return;
    }
    if (message.startsWith("EXIT_COMPLETED")) {
        const sessionId = Number(parts[1]);
        const plate = normalizePlate(parts[2]);
        const oldSession = activeSessions.find(
            (session) => Number(session.id) === sessionId &&
                normalizePlate(session.plate) === plate
        );
        const apiSession = apiData.sessions.find(
            (session) => Number(getField(session, "maPhienEsp32")) === sessionId &&
                normalizePlate(
                    getField(session, "bienSo") ||
                    getField(getApiVehicle(session), "bienSo")
                ) === plate
        );
        const pending = pendingExitFees[sessionId];
        const ownerPart = parts.find((part) => part.startsWith("OWNER="));
        const owner = ownerPart
            ? ownerPart.slice("OWNER=".length).trim()
            : oldSession?.owner || getField(apiSession, "tenChuXe") || "-";
        const apiEntryTime = new Date(getField(apiSession, "thoiGianVao") || 0);
        const apiSpots = apiSession ? getApiSessionSpots(apiSession) : "";
        const entryTimestamp = Number(oldSession?.entryTime) ||
            (Number.isNaN(apiEntryTime.getTime())
                ? 0
                : Math.floor(apiEntryTime.getTime() / 1000));
        const exitTimestamp = Math.floor(Date.now() / 1000);
        const elapsedSeconds = entryTimestamp > 0
            ? Math.max(0, exitTimestamp - entryTimestamp)
            : 0;
        const elapsedFee = entryTimestamp > 0
            ? Math.max(1, Math.ceil(elapsedSeconds / 60)) * PARKING_FEE_PER_MINUTE
            : 0;
        const apiFee = Number(getField(apiSession, "tongTien"));
        const isApiSessionCompleted =
            getApiSessionStatus(apiSession) === "COMPLETED";
        const finalFee = isApiSessionCompleted && Number.isFinite(apiFee)
            ? apiFee
            : Math.max(
                Number(pending?.fee) || 0,
                elapsedFee,
                Number.isFinite(apiFee) ? apiFee : 0
            );
        parkingHistory = parkingHistory.filter(
            (session) => getHistoryIdentity(session.sessionId, session.plate) !==
                getHistoryIdentity(sessionId, plate)
        );
        parkingHistory.unshift({
            sessionId,
            plate,
            owner,
            spots: Array.isArray(oldSession?.spots)
                ? oldSession.spots
                : apiSpots && apiSpots !== "—" ? apiSpots.split(", ") : [],
            entryTime: entryTimestamp,
            exitTime: exitTimestamp,
            fee: finalFee
        });
        parkingSlots.forEach((slot) => {
            if (Number(slot.sessionId) !== sessionId) return;
            slot.sessionId = -1;
            slot.reserved = false;
            slot.occupied = false;
            slot.virtual = false;
        });
        localStorage.setItem("smartParkingHistory", JSON.stringify(parkingHistory));
        delete pendingExitFees[sessionId];
        activeSessions = activeSessions.filter((session) => Number(session.id) !== sessionId);
        renderParkingSlots();
        renderSessionSource();
        renderHistory();
        requestApiRefresh();
        showToast(`Xe ${plate} đã ra khỏi bãi`);
        return;
    }
    if (message.startsWith("PARKING_WRONG_SPOT")) {
        showToast(translateLogMessage("session", message));
        return;
    }
    if (message.includes("DENIED") || message.includes("ERROR") || message.includes("TIMEOUT")) {
        showToast(translateLogMessage("session", message));
    }
}

function handleBarrierEvent(message) {
    const [state, sourcePart] = message.split("|");
    const source = sourcePart?.replace("SOURCE=", "") || "SYSTEM";
    if (state === "OPEN" || state === "EMERGENCY_OPEN") {
        updateBadge("barrierStatus", "MỞ", "open");
        if (state === "EMERGENCY_OPEN") updateBadge("emergencyStatus", "KHẨN CẤP", "emergency");
        showToast(`Cổng đã mở (${translateBarrierSource(source)})`);
    } else if (state === "CLOSED") {
        updateBadge("barrierStatus", "ĐÓNG", "closed");
        if (source.includes("EMERGENCY")) updateBadge("emergencyStatus", "BÌNH THƯỜNG", "safe");
    }
}

function handleHeartbeat(message) {
    try {
        const data = JSON.parse(message);
        if (data.online === true) {
            lastESP32Heartbeat = Date.now();
            updateESP32Status(true);
        }
    } catch (error) {
        addLog("LỖI", "Dữ liệu JSON không hợp lệ từ /heartbeat");
        console.error(error);
    }
}

function handleWokwiEvent(message) {
    if (message === "WOKWI_BARRIER_BUTTON") {
        updateBadge("barrierStatus", "MỞ", "open");
    } else if (message === "WOKWI_EMERGENCY_ON") {
        updateBadge("barrierStatus", "MỞ", "open");
        updateBadge("emergencyStatus", "KHẨN CẤP", "emergency");
    } else if (message === "WOKWI_EMERGENCY_OFF") {
        updateBadge("barrierStatus", "ĐÓNG", "closed");
        updateBadge("emergencyStatus", "BÌNH THƯỜNG", "safe");
    }
    showToast(translateLogMessage("event", message));
}

function translateBarrierSource(source) {
    return {
        WOKWI_BUTTON: "nút MỞ CỔNG Wokwi",
        WOKWI_EMERGENCY: "nút khẩn cấp Wokwi",
        DASHBOARD: "dashboard",
        ENTRY: "xe vào",
        EXIT: "xe ra",
        AUTO_CLOSE: "tự đóng"
    }[source] || "hệ thống";
}

function updateESP32Status(online) {
    updateBadge("esp32Status", online ? "TRỰC TUYẾN" : "NGOẠI TUYẾN", online ? "online" : "offline");
}

function calculateDuration(entryTimestamp) {
    if (!entryTimestamp) {
        return "-";
    }
    let seconds = Math.floor(Date.now() / 1000) - Number(entryTimestamp);
    seconds = Math.max(0, Number.isFinite(seconds) ? seconds : 0);
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const remainingSeconds = seconds % 60;
    return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(remainingSeconds).padStart(2, "0")}`;
}

function calculateEstimatedFee(entryTimestamp) {
    if (!entryTimestamp) {
        return 0;
    }
    let seconds = Math.floor(Date.now() / 1000) - Number(entryTimestamp);
    seconds = Math.max(0, Number.isFinite(seconds) ? seconds : 0);
    return Math.max(1, Math.ceil(seconds / 60)) * PARKING_FEE_PER_MINUTE;
}

function getDisplayedFee(session) {
    const controllerFee = Number(session?.currentFee);
    const dashboardFee = calculateEstimatedFee(session?.entryTime);
    return Math.max(Number.isFinite(controllerFee) ? controllerFee : 0, dashboardFee);
}

function formatCurrency(amount) {
    return `${new Intl.NumberFormat("vi-VN").format(Number(amount) || 0)} đ`;
}

function updateRevenue() {
    renderOverviewFromApi();
}

function appendCell(row, value, className = "") {
    const cell = document.createElement("td");
    cell.textContent = value;
    cell.className = className;
    row.appendChild(cell);
}

function getCompletedHistory() {
    const completed = apiData.sessions.filter(
        (session) => getApiSessionStatus(session) === "COMPLETED"
    );
    const apiSessionIdentities = new Set(
        completed
            .map((session) => getHistoryIdentity(
                getField(session, "maPhienEsp32"),
                getField(session, "bienSo") ||
                    getField(getApiVehicle(session), "bienSo")
            ))
    );
    const localHistory = parkingHistory.filter(
        (session) => !apiSessionIdentities.has(
            getHistoryIdentity(session.sessionId, session.plate)
        )
    );
    const history = [...completed, ...localHistory].sort((a, b) => {
        const aExitTime = Number(a.exitTime) ||
            Math.floor(new Date(getField(a, "thoiGianRa") || 0).getTime() / 1000);
        const bExitTime = Number(b.exitTime) ||
            Math.floor(new Date(getField(b, "thoiGianRa") || 0).getTime() / 1000);
        return bExitTime - aExitTime;
    });
    return history;
}

function renderHistory() {
    renderApiTable(
        elements.historyTable,
        [
            { value: (session) => `#${session.sessionId ?? getField(session, "maPhien") ?? "-"}` },
            { value: (session) => session.plate || getField(session, "bienSo") || getField(getApiVehicle(session), "bienSo") || "—" },
            { value: (session) => session.owner || getField(session, "tenChuXe") || getField(getApiVehicle(session), "tenChuXe") || "—" },
            { value: (session) => Array.isArray(session.spots) ? session.spots.join(", ") || "—" : getApiSessionSpots(session) },
            { value: (session) => session.entryTime ? formatUnixTime(session.entryTime) : formatApiDate(getField(session, "thoiGianVao")) },
            { value: (session) => session.exitTime ? formatUnixTime(session.exitTime) : formatApiDate(getField(session, "thoiGianRa")) },
            { value: (session) => session.fee != null ? formatCurrency(session.fee) : formatApiMoney(getField(session, "tongTien")) }
        ],
        getCompletedHistory()
    );
}

function renderApiTableMessage(target, columnCount, message) {
    if (!target) return;
    target.replaceChildren();
    const row = document.createElement("tr");
    const cell = document.createElement("td");
    cell.colSpan = columnCount;
    cell.textContent = message;
    row.appendChild(cell);
    target.appendChild(row);
}

function renderSensorHistoryFromApi() {
    if (apiData.sensorHistoryState === "loading") {
        renderApiTableMessage(elements.sensorHistoryTable, 5, "Đang tải dữ liệu...");
        return;
    }
    if (apiData.sensorHistoryState === "error") {
        renderApiTableMessage(elements.sensorHistoryTable, 5, "Không tải được dữ liệu cảm biến. Hãy kiểm tra Web API đang chạy.");
        return;
    }
    renderApiTable(
        elements.sensorHistoryTable,
        [
            { value: (item) => getField(item, "maViTri") },
            {
                value: (item) => {
                    const distance = getField(item, "khoangCachCM");
                    return distance == null ? "—" : `${distance} cm`;
                }
            },
            { value: (item) => getField(item, "trangThaiCu") },
            { value: (item) => getField(item, "trangThaiMoi") },
            {
                value: (item) => formatApiDate(
                    getField(item, "thoiGianGhiNhan")
                )
            }
        ],
        apiData.sensorHistory
    );
}

function renderSystemHistoryFromApi() {
    if (apiData.systemHistoryState === "loading") {
        renderApiTableMessage(elements.systemHistoryTable, 6, "Đang tải dữ liệu...");
        return;
    }
    if (apiData.systemHistoryState === "error") {
        renderApiTableMessage(elements.systemHistoryTable, 6, "Không tải được nhật ký hệ thống. Hãy kiểm tra Web API đang chạy.");
        return;
    }
    renderApiTable(
        elements.systemHistoryTable,
        [
            { value: (item) => getField(item, "loaiSuKien") },
            { value: (item) => getField(item, "nguonSuKien") },
            { value: (item) => getField(item, "mucDo") },
            { value: (item) => getField(item, "mqttTopic") },
            { value: (item) => getField(item, "noiDung") },
            {
                value: (item) => formatApiDate(
                    getField(item, "thoiGianGhiNhan")
                )
            }
        ],
        apiData.systemLogs
    );
}

function translateLogTopic(topic) {
    return { status: "TRẠNG THÁI", slots: "VỊ TRÍ ĐỖ", sessions: "PHIÊN GỬI XE", session: "SỰ KIỆN", barrier: "CỔNG CHẮN", heartbeat: "NHỊP ESP32", event: "THAO TÁC WOKWI", entry: "XE VÀO", exit: "XE RA", recovery: "XÁC MINH QUÊN MÃ", command: "LỆNH", SENT: "ĐÃ GỬI", SYSTEM: "HỆ THỐNG" }[topic] || topic;
}

function translateLogMessage(topic, message) {
    const fixedMessages = {
        ONLINE: "Thiết bị đã trực tuyến",
        OPEN_BARRIER: "Mở cổng chắn",
        EMERGENCY_ON: "Bật chế độ khẩn cấp",
        EMERGENCY_OFF: "Tắt chế độ khẩn cấp",
        OPEN: "Cổng chắn đã mở",
        CLOSED: "Cổng chắn đã đóng",
        EMERGENCY_OPEN: "Cổng chắn mở do chế độ khẩn cấp",
        WOKWI_BARRIER_BUTTON: "Đã mở cổng bằng nút trên mô hình Wokwi",
        WOKWI_EMERGENCY_ON: "Đã bật chế độ khẩn cấp bằng nút Wokwi",
        WOKWI_EMERGENCY_OFF: "Đã tắt chế độ khẩn cấp bằng nút Wokwi",
        "ENTRY_DENIED|EMERGENCY_MODE": "Từ chối xe vào: đang ở chế độ khẩn cấp",
        "ENTRY_DENIED|PARKING_IN_PROGRESS": "Từ chối xe vào: đang xử lý xe khác",
        "ENTRY_DENIED|PARKING_FULL": "Từ chối xe vào: bãi đã đầy",
        "ENTRY_DENIED|PLATE_ALREADY_EXISTS": "Từ chối xe vào: biển số đã tồn tại",
        "ENTRY_DENIED|TIME_NOT_SYNCED": "Từ chối xe vào: chưa đồng bộ thời gian",
        "ENTRY_DENIED|SPOT_UNAVAILABLE": "Từ chối xe vào: ô đỗ không còn trống",
        "ENTRY_ERROR|INVALID_DATA": "Lỗi xe vào: dữ liệu không hợp lệ",
        "ENTRY_ERROR|SESSION_FULL": "Lỗi xe vào: danh sách phiên đã đầy",
        "ENTRY_ERROR|INVALID_FORMAT": "Lỗi xe vào: sai định dạng dữ liệu",
        "ENTRY_ERROR|SPOT_REQUIRED": "Lỗi xe vào: chưa chọn ô đỗ",
        "ENTRY_ERROR|INVALID_SPOT": "Lỗi xe vào: ô đỗ không hợp lệ",
        "EXIT_DENIED|VEHICLE_NOT_FOUND": "Từ chối xe ra: không tìm thấy xe",
        "EXIT_DENIED|PARKING_IN_PROGRESS": "Từ chối xe ra: xe đang làm thủ tục vào",
        "EXIT_DENIED|EXIT_IN_PROGRESS": "Từ chối xe ra: xe đang làm thủ tục ra",
        "EXIT_DENIED|INVALID_PICKUP_CODE": "Từ chối xe ra: mã nhận xe không đúng",
        "EXIT_ERROR|INVALID_DATA": "Lỗi xe ra: dữ liệu không hợp lệ",
        "EXIT_ERROR|INVALID_FORMAT": "Lỗi xe ra: sai định dạng dữ liệu",
        "RECOVERY_DENIED|VEHICLE_NOT_FOUND": "Không thể xác minh: không tìm thấy xe",
        "RECOVERY_DENIED|PARKING_IN_PROGRESS": "Không thể xác minh: xe chưa vào ô đỗ",
        "RECOVERY_DENIED|EXIT_IN_PROGRESS": "Không thể xác minh: xe đang rời bãi",
        "RECOVERY_DENIED|OWNER_MISMATCH": "Từ chối xác minh: tên tài xế/chủ xe không khớp",
        "RECOVERY_ERROR|INVALID_DATA": "Lỗi xác minh: dữ liệu không hợp lệ",
        "RECOVERY_ERROR|INVALID_FORMAT": "Lỗi xác minh: dữ liệu không hợp lệ",
        "EXIT_TIMEOUT|VEHICLE_STILL_DETECTED": "Xe chưa rời khỏi bãi đúng thời hạn",
        PARKING_TIMEOUT: "Đã hủy giữ chỗ vì xe không vào ô đỗ đúng thời hạn"
    };
    if (fixedMessages[message]) return fixedMessages[message];
    if (message.startsWith("BARRIER_OPEN|")) return "Cổng chắn đã mở";
    if (message.startsWith("BARRIER_CLOSED|")) return "Cổng chắn đã đóng";
    if (message.startsWith("BARRIER_EMERGENCY_OPEN|")) return "Cổng mở do chế độ khẩn cấp";
    if (topic === "barrier") {
        const [state, sourcePart] = message.split("|");
        const source = sourcePart?.replace("SOURCE=", "") || "SYSTEM";
        if (state === "OPEN") return `Cổng chắn đã mở (${translateBarrierSource(source)})`;
        if (state === "EMERGENCY_OPEN") return `Cổng mở khẩn cấp (${translateBarrierSource(source)})`;
        if (state === "CLOSED") return `Cổng chắn đã đóng (${translateBarrierSource(source)})`;
    }
    if (topic === "status" && message.startsWith("{")) return "Đã cập nhật trạng thái bãi xe";
    if (topic === "slots" && message.startsWith("[")) return "Đã cập nhật trạng thái các vị trí đỗ";
    if (topic === "sessions" && message.startsWith("[")) return "Đã cập nhật danh sách phiên gửi xe";
    if (topic === "heartbeat" && message.startsWith("{")) return "ESP32 vẫn đang hoạt động";
    if (message.startsWith("ENTRY_OK|")) return `Xe vào thành công: biển số ${message.split("|")[2] || "-"}`;
    if (message.startsWith("CONFIRM_PARKING|")) return `Đã gửi xác nhận xe đỗ tại ${message.split("|")[1] || "ô đã chọn"}`;
    if (message.startsWith("PARKED|")) return `Xe đã đỗ tại ${message.split("SPOTS=")[1] || "ô đã chọn"}`;
    if (message.startsWith("PARKING_CONFIRM_DENIED|")) return "Không thể xác nhận xe đỗ: phiên hoặc ô đỗ không hợp lệ";
    if (message.startsWith("PARKING_WRONG_SPOT|")) return `Xe chưa đỗ đúng ô đã chọn (${message.split("|")[2] || "-"})`;
    if (message.startsWith("EXIT_OK|")) return `Đã xác thực xe ra: biển số ${message.split("|")[2] || "-"}`;
    if (message.startsWith("EXIT_COMPLETED|")) return `Xe đã ra khỏi bãi: biển số ${message.split("|")[2] || "-"}`;
    if (topic === "recovery") return "Đã gửi yêu cầu xác minh quên mã nhận xe";
    return message;
}

function addLog(topic, message) {
    const entry = document.createElement("div");
    const time = new Date().toLocaleTimeString("vi-VN");
    entry.className = "log-entry";
    entry.textContent = `${time} [${translateLogTopic(topic)}] ${translateLogMessage(topic, message)}`;
    elements.eventLog.prepend(entry);
    while (elements.eventLog.children.length > 80) {
        elements.eventLog.lastElementChild.remove();
    }
}

function shouldLogMessage(topic) {
    return ["session", "barrier", "event", "command"].includes(topic);
}

function showToast(message, duration = 3500, protect = false) {
    if (Date.now() < toastProtectedUntil && !protect) return;

    elements.toast.textContent = message;
    elements.toast.classList.add("show");
    clearTimeout(toastTimer);
    toastProtectedUntil = protect ? Date.now() + duration : 0;
    toastTimer = setTimeout(() => {
        elements.toast.classList.remove("show");
        toastProtectedUntil = 0;
    }, duration);
}

function publish(topic, message) {
    if (!client || !client.connected) {
        showToast("Chưa kết nối MQTT");
        return false;
    }
    client.publish(topic, message);
    const shortTopic = topic.replace(`${MQTT_PREFIX}/`, "");
    addLog("SENT", `${translateLogTopic(shortTopic)} → ${translateLogMessage(shortTopic, message)}`);
    return true;
}

function updateClock() {
    const now = new Date();
    document.getElementById("clock").textContent = now.toLocaleTimeString("vi-VN");
    document.getElementById("date").textContent = now.toLocaleDateString("vi-VN");
}

function formatUnixTime(timestamp) {
    const value = Number(timestamp);
    return Number.isFinite(value) && value > 0 ? new Date(value * 1000).toLocaleString("vi-VN") : "-";
}

function connectMqtt() {
    if (!window.mqtt) {
        setMqttConnection(false, "Không tải được MQTT.js");
        addLog("LỖI", "Không thể tải thư viện MQTT.js");
        return;
    }
    client = window.mqtt.connect(MQTT_URL, {
        clientId: `smartparking-dashboard-${crypto.randomUUID?.() || Math.random().toString(16).slice(2)}`,
        clean: true,
        connectTimeout: 15000,
        keepalive: 30,
        reconnectPeriod: 5000,
        reconnectOnConnackError: true,
        resubscribe: true
    });
    client.on("connect", () => {
        setMqttConnection(true, "Đã kết nối");
        client.subscribe(`${MQTT_PREFIX}/#`, (error) => {
            if (error) {
                addLog("LỖI", "Không thể đăng ký các topic của bãi xe");
                return;
            }
            addLog("HỆ THỐNG", `Đã đăng ký MQTT: ${MQTT_PREFIX}/#`);
        });
    });
    client.on("offline", () => {
        setMqttConnection(false, "Ngoại tuyến");
        addLog("HỆ THỐNG", "MQTT ngoại tuyến, đang tự kết nối lại");
    });
    client.on("reconnect", () => setMqttConnection(false, "Đang kết nối lại..."));
    client.on("error", () => { setMqttConnection(false, "Lỗi kết nối"); addLog("LỖI", "Không thể kết nối MQTT"); });
    client.on("message", (topic, payload) => {
        const message = payload.toString();
        const shortTopic = topic.replace(`${MQTT_PREFIX}/`, "");
        if (shouldLogMessage(shortTopic)) addLog(shortTopic, message);
        if (topic === TOPICS.status) handleStatus(message);
        else if (topic === TOPICS.slots) handleSlots(message);
        else if (topic === TOPICS.sessions) handleSessions(message);
        else if (topic === TOPICS.session) handleSessionEvent(message);
        else if (topic === TOPICS.barrier) handleBarrierEvent(message);
        else if (topic === TOPICS.heartbeat) handleHeartbeat(message);
        else if (topic === TOPICS.event) handleWokwiEvent(message);
        else if (shortTopic === "alert") {
            try {
                const data = JSON.parse(message);
                if (data.event === "OVERLAP") {
                    showToast(`CẢNH BÁO: Đỗ lấn vạch (OVERLAP) tại ô ${data.slot} và ô ${data.adjacent}!`, "warning");
                } else if (data.event === "UNAUTHORIZED_PARKING") {
                    showToast(`CẢNH BÁO: Phát hiện xe đỗ chui tại ô ${data.slot}!`, "error");
                } else if (data.event === "DEVICE_OFFLINE") {
                    showToast(`MẤT KẾT NỐI ESP32 (LWT Trigger): Các ô đỗ chuyển sang UNKNOWN!`, "error");
                } else if (data.event === "SLOT_REASSIGNED") {
                    showToast(`Đơn ${data.orderId}: Đã tự động đổi sang ô ${data.newSlot}.`, "info");
                } else if (data.event === "FREE_EXIT") {
                    showToast(`Đơn ${data.orderId}: Kích hoạt FreeExit cho xe ra miễn phí.`, "info");
                }
            } catch (e) {
                showToast(message, "info");
            }
        }
    });
}

const viewCopy = {
    dashboard: { title: "Bảng điều khiển bãi xe", subtitle: "Theo dõi và điều khiển bãi xe theo thời gian thực." },
    parking: { title: "Quản lý bãi xe", subtitle: "Theo dõi vị trí đỗ xe và điều khiển xe ra vào." },
    sessions: { title: "Phiên gửi xe", subtitle: "Quản lý các xe đang gửi và xem lại lịch sử giao dịch." },
    system: { title: "Hệ thống", subtitle: "Theo dõi kết nối và trạng thái hoạt động của ESP32." }
};

function setActiveView(view, shouldScroll = true) {
    const selectedView = viewCopy[view] ? view : "dashboard";
    const copy = viewCopy[selectedView];
    let visibleGridPanels = 0;

    document.querySelectorAll("[data-view-panel]").forEach((panel) => {
        const visible = selectedView === "dashboard" || panel.dataset.viewPanel === selectedView;
        panel.hidden = !visible;
        if (visible && panel.parentElement === elements.overviewGrid) {
            visibleGridPanels += 1;
        }
    });
    elements.overviewGrid.hidden = visibleGridPanels === 0;
    elements.overviewGrid.classList.toggle("single-panel", visibleGridPanels === 1);
    elements.pageTitle.textContent = copy.title;
    elements.pageSubtitle.textContent = copy.subtitle;
    document.title = `${copy.title} | Bãi xe thông minh`;

    elements.navItems.forEach((item) => {
        const active = item.dataset.view === selectedView;
        item.classList.toggle("active", active);
        if (active) item.setAttribute("aria-current", "page");
        else item.removeAttribute("aria-current");
    });
    if (shouldScroll) window.scrollTo({ top: 0, behavior: "smooth" });
}

elements.navItems.forEach((item) => {
    item.addEventListener("click", () => setActiveView(item.dataset.view));
});
elements.entrySpot.addEventListener("change", renderParkingSlots);

function isValidMqttField(value, maximumLength) {
    return value.length > 0 && value.length <= maximumLength &&
        !/[|"\\\r\n]/.test(value);
}

document.getElementById("entryForm").addEventListener("submit", (event) => {
    event.preventDefault();
    const plate = document.getElementById("entryPlate").value.trim().toUpperCase();
    const owner = document.getElementById("entryOwner").value.trim();
    const spotId = elements.entrySpot.value;
    if (!plate || !owner) return showToast("Nhập biển số xe và tên chủ xe");
    if (!isValidMqttField(plate, 20) || !isValidMqttField(owner, 100)) return showToast("Biển số hoặc tên có ký tự không hợp lệ");
    if (!spotId) return showToast("Chọn một ô đỗ còn trống trước khi mở cổng");
    const dashboardTimestamp = Math.floor(Date.now() / 1000);
    if (publish(TOPICS.entry, `${plate}|${owner}|${spotId}|${dashboardTimestamp}`)) showToast(`Đang tạo phiên và giữ chỗ ${spotId}`);
});

document.getElementById("exitForm").addEventListener("submit", (event) => {
    event.preventDefault();
    const plate = document.getElementById("exitPlate").value.trim().toUpperCase();
    const pickupCode = document.getElementById("pickupCode").value.trim();
    if (!plate || !pickupCode) return showToast("Nhập biển số xe và mã nhận xe");
    if (!isValidMqttField(plate, 20) || !/^\d{6}$/.test(pickupCode)) return showToast("Biển số hoặc mã nhận xe không hợp lệ");
    const dashboardTimestamp = Math.floor(Date.now() / 1000);
    if (publish(TOPICS.exit, `${plate}|${pickupCode}|${dashboardTimestamp}`)) showToast("Đã gửi yêu cầu xác thực xe ra");
});

document.getElementById("forgotCodeButton").addEventListener("click", () => {
    const plate = document.getElementById("exitPlate").value.trim().toUpperCase();
    if (!plate) return showToast("Nhập biển số xe trước khi xác minh");
    const owner = prompt("Nhập đúng tên tài xế/chủ xe đã đăng ký:");
    if (!owner?.trim()) return;
    if (!isValidMqttField(plate, 20) || !isValidMqttField(owner.trim(), 100)) return showToast("Biển số hoặc tên có ký tự không hợp lệ");
    if (confirm("Chỉ tiếp tục sau khi nhân viên đã kiểm tra giấy tờ tùy thân/GPLX. Xác minh và cho xe ra?")) {
        publish(TOPICS.recovery, `${plate}|${owner.trim()}|${Math.floor(Date.now() / 1000)}`);
    }
});

document.getElementById("openBarrierButton").addEventListener("click", () => publish(TOPICS.command, "OPEN_BARRIER"));
document.getElementById("emergencyOnButton").addEventListener("click", () => {
    if (confirm("Bạn có muốn bật chế độ khẩn cấp?")) publish(TOPICS.command, "EMERGENCY_ON");
});
document.getElementById("emergencyOffButton").addEventListener("click", () => publish(TOPICS.command, "EMERGENCY_OFF"));
document.getElementById("clearLogButton").addEventListener("click", () => elements.eventLog.replaceChildren());
document.getElementById("clearHistoryButton").addEventListener("click", () => {
    initializeApiDashboard();
    showToast("Đang làm mới lịch sử từ máy chủ");
});

renderParkingSlots();
renderSessionSource();
renderHistory();
updateRevenue();
updateClock();
setActiveView("dashboard", false);
setInterval(updateClock, 1000);
setInterval(() => {
    if (apiData.sessionsLoaded || activeSessions.length > 0) {
        renderSessionSource();
    }
}, 1000);
setInterval(() => {
    if (lastESP32Heartbeat === 0) {
        updateESP32Status(false);
        return;
    }
    updateESP32Status(Date.now() - lastESP32Heartbeat <= ESP32_OFFLINE_TIMEOUT);
}, 2000);
setInterval(initializeApiDashboard, API_REFRESH_INTERVAL);
initializeApiDashboard();
connectMqtt();
