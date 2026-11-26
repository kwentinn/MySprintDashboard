import { hooks, shared } from "../state.js";
import { t } from "../texts.js";
import { esc, shortDate } from "../util.js";

// Live updates and notifications. The server reads Azure DevOps and the vault again every five minutes and sends one
// message each time (GET /api/events, server-sent events): when it read them, and what changed since the last
// time. The header says whether the page is listening; the bell opens the notifications of the last seven days
// (GET /api/notifications), a banner announces the ones that just arrived, and the rows of the Today view carry the
// last unread one of their PBI or pull request.
let notifications = [], readUntil = null, panelOpen = false, shownSource = "all", banner = null;
const live = { connected: false, checkedAt: null, lostAt: null, failed: false };

const sameDay = (a, b) => a.toDateString() === b.toDateString();
const clock = date => date.toLocaleTimeString(t("locale"), { hour: "2-digit", minute: "2-digit" });
// The time of a notification, with its day when it is not today's.
const noteTime = iso => { const date = new Date(iso); return sameDay(date, new Date()) ? clock(date) : `${shortDate(iso)} ${clock(date)}`; };
const unread = note => !readUntil || new Date(note.at) > new Date(readUntil);
// What changed, as a sentence in the page language: the server only sends the kind of change and its values.
const noteText = note => t("notes")[note.kind]?.(note) ?? note.kind;

// The line a row of the Today view gets while its PBI (`field` "pbi") or pull request ("pr") has an unread notification.
export function noteLine(field, id) {
  const note = notifications.find(candidate => candidate[field] === id && unread(candidate));
  return note ? `<div class="row-line"><span class="pill fresh small">${esc(t("updatedAt")(noteTime(note.at)))}</span> ${esc(noteText(note))}</div>` : "";
}

export async function loadNotifications() {
  try {
    const response = await fetch("/api/notifications");
    if (!response.ok) return;
    const data = await response.json();
    readUntil = data.readUntil;
    addNotifications(data.notifications);
    drawNotifications();
  } catch { }
}

// The notifications come from the list of the server and from its messages, sometimes both: each one is kept once
// (by its id), the newest first.
function addNotifications(list) {
  const known = new Set(notifications.map(note => note.id));
  const added = list.filter(note => !known.has(note.id));
  notifications = [...added, ...notifications].sort((a, b) => new Date(b.at) - new Date(a.at));
  return added;
}

// Listens to the server while the tab shows: a hidden tab stops listening, so that the server stops reading Azure
// DevOps when nobody looks, and so that many tabs do not each hold a connection (a browser allows six to one
// host). `reload` draws the page again from the data the server just read.
export function connectLive(reload) {
  if (!window.EventSource) return;
  let events = null;
  const listen = () => {
    events = new EventSource("/api/events");
    events.onopen = () => {
      // Back after a loss or after being hidden: what happened meanwhile is on the server.
      if (live.lostAt || live.wasHidden) { loadNotifications(); reload(); }
      Object.assign(live, { connected: true, lostAt: null, wasHidden: false });
    };
    events.onerror = () => Object.assign(live, { connected: false, lostAt: live.lostAt ?? new Date() });
    events.onmessage = event => received(JSON.parse(event.data), reload);
  };
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) return listen();
    events.close();
    Object.assign(live, { connected: false, lostAt: null, wasHidden: true });
  });
  if (document.hidden) live.wasHidden = true;
  else listen();
}

function received(message, reload) {
  live.failed = message.type === "error";
  if (message.checkedAt) live.checkedAt = message.checkedAt;
  if (message.type !== "update") return;
  const added = addNotifications(message.notifications);
  if (added.length) {
    banner = { count: new Set(added.map(note => note.pr ? `pr${note.pr}` : note.pbi ? `pbi${note.pbi}` : note.file)).size, at: message.checkedAt };
    drawNotifications();
  }
  if (message.changed) reload();
}

// Whether the server pushes the changes: the page then has no need to ask on its own.
export const isLive = () => live.connected;

// The state of the connection, for the header; null when the browser cannot listen.
export function liveHtml() {
  if (!window.EventSource) return null;
  if (!live.connected) return live.lostAt ? `<span class="live off"><i></i>${esc(t("liveLost")(clock(live.lostAt)))}</span>` : `<span class="live"><i></i>${esc(t("liveConnecting"))}</span>`;
  if (live.failed) return `<span class="live off"><i></i>${esc(t("liveAdoError"))}</span>`;
  const seconds = live.checkedAt ? Math.max(0, Math.round((Date.now() - new Date(live.checkedAt)) / 1000)) : null;
  const age = seconds === null ? "" : seconds < 60 ? `${seconds} s` : `${Math.round(seconds / 60)} min`;
  return `<span class="live on"><i></i>${esc(age ? t("liveChecked")(age) : t("liveOn"))}</span>`;
}

// Where a notification leads: the page of my PBI (its Questions tab for a question), or of a pull request the
// dashboard shows; nowhere when it is about something that left the page.
function noteHref(note) {
  const data = shared.lastData;
  if (note.pr && [...data?.reviews ?? [], ...(data?.workItems ?? []).flatMap(item => item.pullRequests)].some(pr => pr.id === note.pr)) return `#pr/${note.pr}`;
  if (note.pbi && data?.workItems.some(item => item.id === note.pbi)) return `#pbi/${note.pbi}${note.kind.startsWith("question") || note.kind === "answer" ? "/06-open-questions" : ""}`;
  return "";
}

export function drawNotifications() {
  const count = notifications.filter(unread).length;
  const badge = document.getElementById("bellCount");
  badge.hidden = !count;
  badge.textContent = count > 99 ? "99+" : count;
  document.getElementById("bell").setAttribute("aria-expanded", panelOpen);

  const bannerBox = document.getElementById("liveBanner");
  bannerBox.hidden = !banner;
  if (banner) bannerBox.innerHTML = `<span><b>${esc(t("bannerUpdated")(banner.count, noteTime(banner.at)))}</b></span>
    <span class="row-actions"><button class="link" data-banner-show>${esc(t("showDetail"))}</button><button class="link" data-banner-close aria-label="${esc(t("close"))}">✕</button></span>`;

  const panel = document.getElementById("notificationsPanel");
  panel.hidden = !panelOpen;
  if (!panelOpen) return;
  // Under the header, whose controls (the bell itself, the refresh, the language) stay within reach.
  panel.style.top = `${Math.max(0, document.querySelector("header").getBoundingClientRect().bottom)}px`;
  const chip = (value, label) => `<button data-note-source="${value}" aria-pressed="${value === shownSource}" class="${value === shownSource ? "on" : ""}">${
    esc(label)} · ${notifications.filter(note => value === "all" || note.source === value).length}</button>`;
  const shown = notifications.filter(note => shownSource === "all" || note.source === shownSource);
  let day = "";
  const entries = shown.map(note => {
    const date = new Date(note.at), label = sameDay(date, new Date()) ? t("tabToday") : date.toLocaleDateString(t("locale"), { weekday: "long", day: "numeric", month: "long" });
    const heading = label === day ? "" : `<div class="label note-day">${esc(label)}</div>`;
    day = label;
    const about = note.pr ? `PR ${note.pr}` : note.pbi ? `#${note.pbi}` : "";
    const href = noteHref(note);
    return `${heading}<${href ? `a href="${href}"` : "div"} class="note ${unread(note) ? "unread" : ""}"><i></i><span>
        <span class="note-meta"><b class="${note.source}">${esc(t("noteSources")[note.source] ?? note.source)}</b> · ${esc(clock(date))}${about || note.title ? ` · ${esc([about, note.title].filter(Boolean).join(" "))}` : ""}</span>
        <span class="note-text">${esc(noteText(note))}</span></span></${href ? "a" : "div"}>`;
  }).join("");
  panel.innerHTML = `<div class="note-head"><h2>${esc(t("notificationsTitle"))} <span class="count">· ${esc(t("unreadCount")(count))}</span></h2>
      <button class="icon" data-note-close aria-label="${esc(t("close"))}">✕</button></div>
    <div class="note-bar"><div class="sprints">${chip("all", t("noteSources").all)}${chip("ado", t("noteSources").ado)}${chip("vault", t("noteSources").vault)}</div>
      ${count ? `<button class="link" data-note-read>${esc(t("markAllRead"))}</button>` : ""}</div>
    <div class="note-list">${entries || `<div class="empty">${esc(t("noNotification"))}</div>`}</div>
    <div class="note-foot muted">${esc(t("notesKept"))}</div>`;
}

async function markRead() {
  try {
    const response = await fetch("/api/notifications/read", { method: "POST", headers: { "X-Dashboard": "1" } });
    if (!response.ok) return;
    ({ readUntil } = await response.json());
    banner = null;
    drawNotifications();
    // The rows lose their "updated" line.
    hooks.render();
  } catch { }
}

// Clicks on the bell, the banner and the panel (called by the page's click listener).
export function notificationClick(e) {
  const pick = name => e.target.closest(`[data-${name}]`);
  const source = pick("note-source");
  if (pick("bell")) panelOpen = !panelOpen;
  else if (pick("note-close")) panelOpen = false;
  else if (pick("banner-show")) panelOpen = true;
  else if (pick("banner-close")) banner = null;
  else if (source) shownSource = source.dataset.noteSource;
  else if (pick("note-read")) return markRead();
  // A notification that leads somewhere closes the panel behind it.
  else if (e.target.closest("#notificationsPanel a")) panelOpen = false;
  // A click anywhere else closes the panel, like Escape.
  else if (panelOpen && !e.target.closest("#notificationsPanel")) panelOpen = false;
  else return;
  drawNotifications();
}
document.addEventListener("keydown", event => {
  if (event.key !== "Escape" || !panelOpen) return;
  panelOpen = false;
  drawNotifications();
});
