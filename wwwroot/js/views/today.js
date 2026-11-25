import { claudeButton, openClaude, pbiPrimary } from "../claude.js";
import { hooks, shared } from "../state.js";
import { itemAction, itemActivity, prAction, rank, signals } from "../status.js";
import { s, t } from "../texts.js";
import { ago, cap, esc, latest, orderPill, pill, statusOf } from "../util.js";

// A row of the Today tab: the pill says what it asks of me, then what it is, one useful sentence and one main
// action. The title and Details open the page of the PBI or of the pull request, where everything else is.
export function rowHtml({ status, pillText, order = "", meta, title, href, line, lineClass = "", primary }) {
  return `<article class="row ${status}">
    <div class="row-main"><div class="row-text">
        <div class="row-meta">${order}${pill(status, pillText)}<span>${meta}</span></div>
        <h3><a href="${href}">${esc(title)}</a></h3>
        ${line ? `<div class="row-line ${lineClass}">${line}</div>` : ""}</div>
      <div class="row-actions">${primary || ""}<a class="btn" href="${href}">${esc(t("details"))}</a></div></div></article>`;
}

export function reviewRow(pr) {
  const [status, hint] = prAction(pr);
  // Once I approved and wrote a review, the main action is to read it again; else to review.
  const readFirst = status === "OK" && pr.review;
  return { status, activity: latest(pr.lastActivity, pr.review?.updated), html: rowHtml({
    status, pillText: cap(hint),
    meta: `PR ${pr.id} · ${esc(pr.repository)} · ${esc(pr.author)} · ${esc(t("since")(ago(pr.createdDate)))}`,
    title: pr.title, href: `#pr/${pr.id}`, line: signals(pr),
    primary: readFirst ? `<a class="btn" href="#pr/${pr.id}/review">${esc(t("readMyReview"))}</a>`
      : claudeButton("review", pr.id, "claudeReview", "", status === "TODO" ? "primary" : "") }) };
}

export function itemRow(item) {
  const [status, reason] = itemAction(item);
  const state = statusOf(item);
  const activity = itemActivity(item);
  // The one sentence: what blocks, else the next action, else when it last moved.
  const [lineClass, line] = state?.fields.blockedBy ? ["bad", `${esc(t("blockedBy"))} ${esc(state.fields.blockedBy)}`]
    : state?.fields.next ? ["", `<span class="muted">${esc(t("nextAction"))}</span> ${esc(state.fields.next)}`]
    : ["muted", esc(t("changed")(ago(activity)))];
  return { status, activity, html: rowHtml({
    status, pillText: t("statusNames")[status], order: orderPill(item.backlogOrder, item.iteration),
    // The sprint only when the filter shows several.
    meta: [`#${item.id}`, selectedSprints.size !== 1 && item.iteration, item.state, reason].filter(Boolean).map(esc).join(" · "),
    title: item.title, href: `#pbi/${item.id}`, line, lineClass, primary: pbiPrimary(item, status === "TODO" ? "primary" : "") }) };
}

// Rows go by status (to do, in progress, waiting, done), then by last activity, most recent first.
export function fill(id, rows, empty) {
  rows.sort((a, b) => rank[a.status] - rank[b.status] || (b.activity ?? "").localeCompare(a.activity ?? ""));
  document.getElementById(id).innerHTML = rows.length ? rows.map(row => row.html).join("") : `<div class="empty">${esc(empty)}</div>`;
  document.getElementById(`${id}Count`).textContent = `· ${rows.length}`;
  return rows;
}

// Sprint filter on my PBIs: one chip per sprint, several can be on. An empty selection means all sprints. It starts
// on the current sprint and is kept in localStorage; a sprint that no longer has PBIs drops out.
export let selectedSprints = null;
export const sprintOf = item => item.iterationPath?.includes("\\") ? item.iterationPath : "";
export const sprintLabel = path => path ? path.split("\\").pop() : t("noSprint");

export function sprintsFilter() {
  const counts = new Map();
  shared.lastData.workItems.forEach(item => counts.set(sprintOf(item), (counts.get(sprintOf(item)) ?? 0) + 1));
  const sprints = [...counts.keys()].sort((a, b) => (a === "") - (b === "") || a.localeCompare(b, undefined, { numeric: true }));
  const current = shared.lastData.currentSprint?.path;
  if (selectedSprints === null) {
    let stored = null;
    try { stored = JSON.parse(localStorage.getItem("dashboard.sprints")); } catch { }
    selectedSprints = new Set(Array.isArray(stored) ? stored : counts.has(current) ? [current] : []);
  }
  selectedSprints.forEach(path => { if (!counts.has(path)) selectedSprints.delete(path); });

  const chip = (value, label, on) => `<button data-sprint="${esc(value)}" aria-pressed="${on}" class="${on ? "on" : ""}">${esc(label)}</button>`;
  document.getElementById("sprints").innerHTML =
    sprints.map(path => chip(path, sprintLabel(path) + (path === current ? ` · ${t("currentSprint")}` : ""), selectedSprints.has(path))).join("")
    + chip("*", t("allSprints"), !selectedSprints.size);
  return shared.lastData.workItems.filter(item => !selectedSprints.size || selectedSprints.has(sprintOf(item)));
}

export function toggleSprint(value) {
  if (value === "*") selectedSprints.clear();
  else if (selectedSprints.has(value)) selectedSprints.delete(value);
  else selectedSprints.add(value);
  try { localStorage.setItem("dashboard.sprints", JSON.stringify([...selectedSprints])); } catch { }
  hooks.render();
}

// Draws the rows of the Today view and its counters; returns how many rows are to do.
export function renderToday() {
  const items = sprintsFilter();
  const all = [...fill("reviews", shared.lastData.reviews.map(reviewRow), t("emptyReviews")),
               ...fill("items", items.map(itemRow), t(shared.lastData.workItems.length ? "emptyFilter" : "emptyItems"))];
  const count = status => all.filter(row => row.status === status).length;
  document.getElementById("counts").innerHTML = [["TODO", "todo"], ["WIP", "wip"], ["WAIT", "wait"], ["OK", "done"]]
    .map(([status, label]) => `<span class="legend ${status}" title="${esc(t("statusHelp")[status])}"><i></i>${esc(t(label)(count(status)))}</span>`)
    .join("");
  return count("TODO");
}

// Review of any PBI or pull request typed in the box, picked by the PBI / PR selector. "#249499" means a PBI and
// "!55769" or "PR 55769" a pull request, whatever the selector says. Anything else is refused before calling the server.
export const reviewKind = () => document.getElementById("reviewKind").value;
export const updateReviewPlaceholder = () =>
  document.getElementById("pbiReviewId").placeholder = t(reviewKind() === "pr" ? "prReviewPlaceholder" : "pbiReviewPlaceholder");
export const clearReviewError = () => {
  document.getElementById("pbiReviewId").classList.remove("invalid");
  const note = document.getElementById("pbiReviewError");
  note.textContent = note.title = "";
  note.classList.remove("ok");
};
document.getElementById("pbiReview").onsubmit = event => {
  event.preventDefault();
  const input = document.getElementById("pbiReviewId");
  let value = input.value.trim(), kind = reviewKind();
  if (/^#/.test(value)) { kind = "pbi"; value = value.slice(1); }
  else if (/^(!|pr\s*)/i.test(value)) { kind = "pr"; value = value.replace(/^(!|pr\s*)/i, ""); }
  document.getElementById("reviewKind").value = kind;
  const valid = /^\d{1,9}$/.test(value) && Number(value) > 0;
  clearReviewError();
  input.classList.toggle("invalid", !valid);
  document.getElementById("pbiReviewError").textContent = valid ? "" : t(kind === "pr" ? "prReviewInvalid" : "pbiReviewInvalid");
  const button = document.getElementById("pbiReviewButton");
  if (!valid || button.disabled) return;
  if (kind !== "pr") { openClaude(button, `pbi-review/${Number(value)}`); return; }
  // A typed PR: the server first adds me as an optional reviewer, then the PR shows in my code reviews.
  openClaude(button, `review/${Number(value)}`, "&addReviewer=1").then(result => {
    const note = document.getElementById("pbiReviewError");
    note.classList.toggle("ok", result?.reviewer !== "failed");
    note.textContent = result?.reviewer ? t("reviewerOutcome")[result.reviewer] : "";
    if (result?.reviewer === "failed") note.title = result.detail ?? "";
    if (result?.reviewer === "added") hooks.load();
  });
};
document.getElementById("pbiReviewId").oninput = clearReviewError;
document.getElementById("reviewKind").onchange = () => { clearReviewError(); updateReviewPlaceholder(); };
