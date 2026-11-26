import { claudeButton, docLink, implemented, pbiPrimary, resumeThreadsButton, threadsButton } from "../claude.js";
import { shared } from "../state.js";
import { itemAction, itemActivity, prAction, signals, votesText } from "../status.js";
import { t } from "../texts.js";
import { aged, cap, esc, markdown, orderPill, pbiSteps, pill, safeHtml, shortDateTime, statusOf } from "../util.js";
import { backLink, conversationHtml, finishPage, pagePlaceholder, plainText, stepPanel } from "./parts.js";
import { formValues, questions, questionsPanel, restoreForm, stateOf } from "./questions.js";

// The page of one PBI (#pbi/<id>/<tab>): its header (backlog order, status, description folded to four lines), the
// next action of its status file, then one tab per working file of the vault, its pull requests and its history.
export function renderPbi() {
  if (!shared.pbiRoute) return;
  const view = document.getElementById("pbiView");
  if (!shared.lastData) { view.innerHTML = pagePlaceholder(); return; }
  const item = shared.lastData.workItems.find(workItem => workItem.id === shared.pbiRoute.id);
  if (!item) { view.innerHTML = `<div>${backLink()}</div><div class="empty">${esc(t("pbiNotFound")(shared.pbiRoute.id))}</div>`; return; }

  const [status] = itemAction(item);
  const state = statusOf(item), keptForm = formValues(view);
  const tabs = [...pbiSteps, ...(item.pullRequests.length ? ["pr"] : []), "history"];
  const tab = tabs.includes(shared.pbiRoute.tab) ? shared.pbiRoute.tab
    : item.files?.[state?.fields.step] ? state.fields.step : pbiSteps.find(step => item.files?.[step]) ?? pbiSteps[0];
  const openThreads = item.pullRequests.reduce((sum, pr) => sum + (pr.openThreads ?? 0), 0);
  const tabLink = name => {
    const step = pbiSteps.includes(name);
    const label = step ? t("steps")[name] : name === "pr" ? t("tabPr") : t("history");
    const waiting = name === "06-open-questions" && questions.filter(q => q.pbi === item.id && stateOf(q) === "waiting").length;
    const tag = waiting ? pill("TODO", t("qCounts").waiting(waiting), "small")
      : name === state?.fields.step ? pill("step", t("currentStep"), "small")
      : name === "pr" && openThreads ? pill("TODO", t("openThreads")(openThreads), "small") : "";
    return `${name === "pr" || (name === "history" && !tabs.includes("pr")) ? '<span class="sep" aria-hidden="true"></span>' : ""}
      <a href="#pbi/${item.id}/${name}" role="tab" aria-selected="${name === tab}" class="${name === tab ? "on" : ""} ${step && !item.files?.[name] && !(name === "06-open-questions" && questions.some(q => q.pbi === item.id)) ? "missing" : ""}">${esc(label)}${tag}</a>`;
  };
  const description = [item.description, item.acceptanceCriteria && `<h4>${esc(t("acceptance"))}</h4>${item.acceptanceCriteria}`].filter(Boolean).join("");
  const firstLine = state && plainText(state.body.split(/\r?\n/).find(line => line.trim()) ?? "");

  view.innerHTML = `<div class="links">${backLink()}<a href="${esc(item.url)}" target="_blank" style="margin-left:auto">${esc(t("openInAdo"))}</a></div>
    <div class="pbi-head">
      <div class="row-meta">${orderPill(item.backlogOrder, item.iteration)}${pill(status, t("statusNames")[status])}
        <span>${[`#${item.id}`, item.type, item.iteration, item.state, item.tags, aged(itemActivity(item), t("lastActivity"))].filter(Boolean).map(esc).join(" · ")}</span></div>
      <h1>${esc(item.title)}</h1>
    </div>
    ${description ? `<div class="desc-block"><div class="label">${esc(t("description"))}</div>
      <div class="desc markdown ${shared.descOpen ? "open" : ""}">${safeHtml(description)}</div>
      <div><button class="link" data-desc-toggle aria-expanded="${shared.descOpen}">${esc(t(shared.descOpen ? "descLess" : "descMore"))}</button></div></div>` : ""}
    <section class="row banner"><div class="row-text">
        ${state ? `<div class="label">${aged(state.fields.updated || state.file.updated, t("nextHead"))}</div>
          <div class="next">${esc(state.fields.next || firstLine)}</div>
          <div class="${state.fields.blockedBy ? "bad" : "muted"}">${state.fields.blockedBy ? `${esc(t("blockedBy"))} ${esc(state.fields.blockedBy)}` : esc(t("notBlocked"))}
            · ${docLink(`pbi:${item.id}:00-status`, t("readStatus"))}</div>`
          : `<div class="muted">${esc(t("noStatus"))}</div>`}</div>
      <span class="row-actions">${item.lastSession && !implemented(item) ? claudeButton("pbi", item.id, "claudePbi", "", "link") : ""}${pbiPrimary(item, "primary")}</span></section>
    <div class="steptabs" role="tablist">${tabs.map(tabLink).join("")}</div>
    ${tab === "pr" ? pullRequestsPanel(item) : tab === "history" ? historyPanel(item) : tab === "06-open-questions" ? questionsPanel(item) : stepPanel(item, tab)}`;
  restoreForm(view, keptForm);
  finishPage(view);
}

// The pull requests of the PBI: what each asks, then its open comment threads whole. Resolved threads are only counted.
function pullRequestsPanel(item) {
  return `<div class="list">${item.pullRequests.map(pr => {
    const [status, hint] = prAction(pr);
    const active = pr.status === "active";
    const resolved = (pr.totalThreads ?? 0) - (pr.openThreads ?? 0);
    return `<section class="row ${active ? "" : "dim"}"><div class="row-main"><div class="row-text">
          <div class="row-meta">${pill(status, cap(hint))}<span>PR ${pr.id} · ${esc(pr.repository)}${active ? ` → ${esc(pr.targetBranch)} · ${aged(pr.createdDate, t("since"))}` : ""}</span></div>
          <h3><a href="#pr/${pr.id}">${esc(pr.title)}</a></h3>
          ${active ? `<div class="row-line">${signals(pr)}</div><div class="muted">${esc(t("votesLabel"))} ${votesText(pr)}</div>` : ""}</div>
        <div class="row-actions">${threadsButton(pr, "primary") || ""}${resumeThreadsButton(pr, "") || ""}<a class="btn" href="#pr/${pr.id}">${esc(t("details"))}</a></div></div></section>
      ${(pr.threads ?? []).map(thread => `<section class="row pad">${conversationHtml(thread)}</section>`).join("")}
      ${active && resolved > 0 ? `<section class="row pad muted">${esc(t("resolvedThreads")(resolved))}</section>` : ""}`;
  }).join("")}</div>`;
}

function historyPanel(item) {
  const text = event => ({
    created: () => t("evCreated")(event.to),
    state: () => t("evState")(event.from ?? "?", event.to),
    sprint: () => t("evSprint")(event.from ?? "-", event.to ?? "-"),
    assigned: () => t("evAssigned")(event.to),
    prCreated: () => t("evPrCreated")(event.pr),
    prMerged: () => t("evPrMerged")(event.pr),
    prAbandoned: () => t("evPrAbandoned")(event.pr),
    session: () => t("evSession"),
  })[event.type]();
  const events = historyEvents(item);
  if (!events.length) return `<div class="empty">${esc(t("noHistory"))}</div>`;
  return `<section class="row timeline">${events.map(event => `<span class="when">${shortDateTime(event.date)}</span>
    <span>${["state", "created"].includes(event.type) ? `<b>${esc(text(event))}</b>` : esc(text(event))}${event.by ? ` · ${esc(event.by)}` : ""}</span>`).join("")}</section>`;
}

// History of a PBI: state, sprint and assignment changes from Azure DevOps, PRs created and closed, Claude sessions,
// newest first.
function historyEvents(item) {
  const events = (item.history ?? []).map(event => ({ ...event }));
  item.pullRequests.forEach(pr => {
    if (pr.createdDate) events.push({ type: "prCreated", date: pr.createdDate, by: pr.author, pr: pr.id });
    if (pr.closedDate && pr.status !== "active") events.push({ type: pr.status === "completed" ? "prMerged" : "prAbandoned", date: pr.closedDate, pr: pr.id });
  });
  (item.sessions ?? []).forEach(session => events.push({ type: "session", date: session.startedAt }));
  return events.sort((a, b) => new Date(b.date) - new Date(a.date));
}
