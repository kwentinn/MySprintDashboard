import { lang, t } from "../texts.js";
import { cap, esc, markdown, orderPill } from "../util.js";

// Daily tab: what I did on the previous working day (Friday on a Monday), from /api/daily, for the daily meeting.
// The arrows move one working day; a day is loaded once, the refresh button reloads it.
export let dailyDay = null, dailyData = null;
export const isoDay = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
export function stepWorkingDay(date, step) {
  const next = new Date(date);
  do next.setDate(next.getDate() + step); while (next.getDay() === 0 || next.getDay() === 6);
  return next;
}
export const dayLabel = date => cap(date.toLocaleDateString(t("locale"), { weekday: "long", day: "numeric", month: "long" }));

// The tab says "Yesterday", or the day's name when the previous working day is further back (Friday on a Monday).
export function updateDailyTab() {
  const previous = stepWorkingDay(new Date(), -1);
  const yesterday = new Date();
  yesterday.setDate(yesterday.getDate() - 1);
  const name = isoDay(previous) === isoDay(yesterday) ? t("yesterday") : cap(previous.toLocaleDateString(t("locale"), { weekday: "long" }));
  document.getElementById("dailyTab").textContent = t("dailyTab")(name);
  if (dailyDay) renderDaily();
}

export async function loadDaily(force = false) {
  dailyDay ??= stepWorkingDay(new Date(), -1);
  if (!force && dailyData?.day === isoDay(dailyDay)) return renderDaily();
  renderDaily();
  document.getElementById("dailySpinner").hidden = false;
  try {
    const response = await fetch(`/api/daily?day=${isoDay(dailyDay)}`);
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail || response.statusText);
    dailyData = data;
    document.getElementById("error").style.display = "none";
  } catch (e) {
    const error = document.getElementById("error");
    error.textContent = t("refreshFailed")(e.message);
    error.style.display = "block";
  } finally {
    document.getElementById("dailySpinner").hidden = true;
  }
  renderDaily();
}

// One card per work item or pull request. A pull request of mine goes into the card of its work item when that item
// has one that day, so a task reads in one place.
export function dailyGroups() {
  const groups = dailyData.groups.map(group => ({ ...group, events: [...group.events] }));
  const items = new Map(groups.filter(group => group.type !== "pr").map(group => [group.id, group]));
  return groups.filter(group => {
    const target = group.type === "pr" && group.isMine && (group.workItems ?? []).map(w => items.get(w.id)).find(Boolean);
    if (!target) return true;
    target.events.push(...group.events.map(event => ({ ...event, pr: group.id })));
    target.events.sort((a, b) => new Date(a.time) - new Date(b.time));
    (target.pullRequests ??= []).push(group.id);
    return false;
  });
}

export function renderDaily() {
  document.getElementById("dailyTitle").textContent = dayLabel(dailyDay);
  showSummary();
  const container = document.getElementById("daily"), total = document.getElementById("dailyCount");
  total.textContent = "";
  if (!dailyData || dailyData.day !== isoDay(dailyDay)) { container.innerHTML = ""; return; }
  if (!dailyData.groups.length) { container.innerHTML = `<div class="empty">${esc(t("dailyEmpty"))}</div>`; return; }
  const groups = dailyGroups();
  total.textContent = t("dailyTotal")(groups.length, groups.reduce((sum, group) => sum + group.events.length, 0));
  const text = event => (event.kind === "vote" ? t("dailyVotes")[event.text] ?? event.text : event.text) + (event.pr ? ` · PR ${event.pr}` : "");
  const meta = group => group.type === "pr"
    ? [`<a href="${esc(group.url ?? "#")}" target="_blank">PR ${group.id}</a>`, group.author && esc(group.isMine ? t("myPr") : t("prBy")(group.author)),
       group.workItems?.length && `${esc(t("forItems"))} ${group.workItems.map(w => `${orderPill(w.backlogOrder, w.iteration)} #${w.id}`).join(", ")}`]
    : [`<a href="${esc(group.url ?? "#")}" target="_blank">#${group.id}</a>`, esc(group.iteration), group.pullRequests && esc(t("withMyPr")(group.pullRequests.join(", ")))];
  container.innerHTML = groups.map(group => `<article class="row day-card">
      <div class="row-text"><div class="row-meta">${orderPill(group.backlogOrder, group.iteration)}<span>${meta(group).filter(Boolean).join(" · ")}</span></div>
        <h3>${esc(group.title ?? "")}</h3></div>
      <div class="events">${group.events.map(event => `<span class="when">${new Date(event.time).toLocaleTimeString(t("locale"), { hour: "2-digit", minute: "2-digit" })}</span>
        <span><b>${esc(t("dailyKinds")[event.kind] ?? event.kind)}</b> ${esc(text(event))}</span>`).join("")}</div>
    </article>`).join("");
}

// AI summary of the shown day (POST /api/daily-summary, claude -p on the server), kept per day and language.
export const summaries = new Map();
export function showSummary() {
  const summary = dailyDay && summaries.get(`${isoDay(dailyDay)}:${lang}`);
  document.getElementById("dailySummary").innerHTML = summary ? `<div class="markdown">${markdown(summary)}</div>` : `<div class="muted">${esc(t("aiSummaryHint"))}</div>`;
  document.getElementById("dailySummaryButton").textContent = t(summary ? "aiSummaryAgain" : "aiSummary");
}
document.getElementById("dailySummaryButton").onclick = async event => {
  const button = event.currentTarget, box = document.getElementById("dailySummary"), key = `${isoDay(dailyDay)}:${lang}`;
  button.disabled = true;
  box.innerHTML = `<div class="muted"><span class="spinner"></span> ${esc(t("aiSummaryRunning"))}</div>`;
  try {
    const response = await fetch(`/api/daily-summary?day=${isoDay(dailyDay)}&lang=${lang}`, { method: "POST", headers: { "X-Dashboard": "1" } });
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail || response.statusText);
    summaries.set(key, data.summary);
    showSummary();
  } catch (e) {
    box.innerHTML = `<div class="bad">${esc(t("aiSummaryFailed")(e.message))}</div>`;
  } finally {
    button.disabled = false;
  }
};
document.querySelectorAll("[data-day-step]").forEach(button => button.onclick = () => {
  dailyDay = stepWorkingDay(dailyDay, Number(button.dataset.dayStep));
  loadDaily();
});
