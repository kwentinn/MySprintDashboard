import { claudeButton, resumeReviewButton, resumeThreadsButton, threadsButton } from "../claude.js";
import { shared } from "../state.js";
import { prAction, signals, votesText } from "../status.js";
import { t } from "../texts.js";
import { ago, cap, esc, markdown, orderPill, pill, section } from "../util.js";
import { backLink, conversationHtml, filePanel, finishPage, pagePlaceholder } from "./parts.js";

// The page of one pull request (#pr/<id>/<tab>), one I review or one of my PBIs: what it asks, its signals, votes,
// work items and description, then its open comment threads whole, my review and the analysis of its threads.
export function renderPr() {
  if (!shared.prRoute) return;
  const view = document.getElementById("prView");
  if (!shared.lastData) { view.innerHTML = pagePlaceholder(); return; }
  const pr = [...shared.lastData.reviews, ...shared.lastData.workItems.flatMap(item => item.pullRequests)].find(candidate => candidate.id === shared.prRoute.id);
  if (!pr) { view.innerHTML = `<div>${backLink()}</div><div class="empty">${esc(t("prNotFound")(shared.prRoute.id))}</div>`; return; }

  const [status, hint] = prAction(pr);
  const active = pr.status === "active";
  const tabs = ["threads", "review", "analysis"];
  const tab = tabs.includes(shared.prRoute.tab) ? shared.prRoute.tab : pr.openThreads > 0 || !pr.review ? "threads" : "review";
  const reports = { review: pr.review, analysis: pr.threadsReport };
  const tabLink = name => `<a href="#pr/${pr.id}/${name}" role="tab" aria-selected="${name === tab}"
      class="${name === tab ? "on" : ""} ${name in reports && !reports[name] ? "missing" : ""}">${esc(t("prTabs")[name])}${
      name === "threads" && pr.openThreads > 0 ? pill("TODO", t("openCount")(pr.openThreads), "small") : ""}</a>`;
  // Its work items: the ones the server linked (a PR I review), else my PBIs that carry it. Mine open their page.
  const mine = new Set(shared.lastData.workItems.map(item => item.id));
  const items = (pr.workItems ?? shared.lastData.workItems.filter(item => item.pullRequests.some(other => other.id === pr.id)))
    .map(w => `${orderPill(w.backlogOrder, w.iteration)} <a href="${mine.has(w.id) ? `#pbi/${w.id}` : `${esc(w.url)}" target="_blank`}">#${w.id}</a> ${esc(w.state)}`).join(" · ");
  const resolved = (pr.totalThreads ?? 0) - (pr.openThreads ?? 0);
  const review = cls => claudeButton("review", pr.id, "claudeReview", "", cls);
  // A review is for an active pull request of someone else: not my own, not a merged or abandoned one.
  const canReview = active && !pr.isMine;
  const panel = tab === "review"
    ? filePanel(pr.review, { translate: claudeButton("review-translate", pr.id, "claudeTranslate", "", "dashed"),
        action: resumeReviewButton(pr, "") || (canReview && review("")),
        missing: { title: t("reviewMissing"), help: canReview ? t("reviewHelp") : "", button: canReview ? review("primary") : "" } })
    : tab === "analysis"
    ? filePanel(pr.threadsReport, { translate: claudeButton("threads-translate", pr.id, "claudeTranslate", "", "dashed"),
        action: resumeThreadsButton(pr, "") || threadsButton(pr, ""),
        missing: { title: t("analysisMissing"), help: t("analysisHelp"), button: threadsButton(pr, "primary") || "" } })
    : `<div class="list">${(pr.threads ?? []).map(thread => `<section class="row pad">${conversationHtml(thread)}</section>`).join("")
        || `<div class="empty">${esc(t("noOpenThread"))}</div>`}
        ${resolved > 0 ? `<section class="row pad muted">${esc(t("resolvedThreads")(resolved))}</section>` : ""}</div>`;

  view.innerHTML = `<div class="links">${backLink()}<a href="${esc(pr.url)}" target="_blank" style="margin-left:auto">${esc(t("openInAdo"))}</a></div>
    <div class="pbi-head">
      <div class="row-meta">${pill(status, cap(hint))}
        <span>${[`PR ${pr.id}`, `${pr.repository} → ${pr.targetBranch}`, pr.author, t("since")(ago(pr.createdDate))].filter(Boolean).map(esc).join(" · ")}</span></div>
      <h1>${esc(pr.title)}</h1>
      ${active ? `<div class="row-line">${signals(pr)}</div><div class="muted">${esc(t("votesLabel"))} ${votesText(pr)}</div>` : ""}
      <div class="muted">${items ? `${esc(t("linkedItems"))} ${items}` : esc(t("noWorkItem"))}</div>
    </div>
    ${pr.description ? `<div class="desc-block"><div class="label">${esc(t("description"))}</div>
      <div class="desc markdown ${shared.descOpen ? "open" : ""}">${markdown(pr.description)}</div>
      <div><button class="link" data-desc-toggle aria-expanded="${shared.descOpen}">${esc(t(shared.descOpen ? "descLess" : "descMore"))}</button></div></div>` : ""}
    ${active ? `<div class="row-actions">${pr.isMine ? "" : review(status === "TODO" ? "primary" : "")}${resumeReviewButton(pr, "") || ""}
      ${threadsButton(pr, pr.isMine ? "primary" : "") || ""}${resumeThreadsButton(pr, "") || ""}</div>` : ""}
    <div class="steptabs" role="tablist">${tabs.map(tabLink).join("")}</div>
    ${panel}`;
  finishPage(view);
}
