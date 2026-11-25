import { claudeButton } from "../claude.js";
import { shared } from "../state.js";
import { t } from "../texts.js";
import { ago, esc, pill } from "../util.js";

// Search (#search/<words>): the words typed in the header, looked for in Azure DevOps (work items and pull requests
// of the project) and in the markdown files of the vault, by GET /api/search. A number alone, "#1201" or "!4215",
// opens the page of that PBI or pull request when it is one of mine.
let searchQuery = "", searchData = null, searchLoading = false, searchScope = "all", searchClosed = false, searchFolder = "";

export async function loadSearch(query) {
  if (query !== searchQuery) { searchData = null; searchFolder = ""; }
  searchQuery = query;
  document.getElementById("searchInput").value = query;
  if (searchData || searchLoading) return renderSearch();
  searchLoading = true;
  renderSearch();
  const asked = `${query}\n${searchClosed}`;
  let data;
  try {
    const response = await fetch(`/api/search?q=${encodeURIComponent(query)}${searchClosed ? "&closed=1" : ""}`);
    data = await response.json();
    if (!response.ok) throw new Error(data.detail || response.statusText);
  } catch (e) {
    data = { words: [], workItems: [], pullRequests: [], vault: [], error: e.message };
  }
  searchLoading = false;
  // Another search was asked meanwhile: this answer is not the one to show.
  if (asked !== `${searchQuery}\n${searchClosed}`) return loadSearch(searchQuery);
  searchData = data;
  renderSearch();
}

// A text with the searched words marked. The text is cut at the words first and each piece escaped after, so a word
// never matches inside an escaped character ("39" in "&#39;").
function highlight(text, words) {
  const found = words.filter(Boolean).map(word => word.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  if (!found.length) return esc(text);
  return String(text).split(new RegExp(`(${found.join("|")})`, "gi")).map((part, index) => index % 2 ? `<mark>${esc(part)}</mark>` : esc(part)).join("");
}

export function renderSearch() {
  if (shared.activeView !== "search") return;
  const view = document.getElementById("searchView");
  const head = `<div class="head-row"><h2>${esc(t("searchTitle")(searchQuery))}</h2>${searchLoading ? '<span class="spinner"></span>' : ""}</div>`;
  if (!searchData) { view.innerHTML = head; return; }
  const { words, workItems, pullRequests, vault } = searchData;
  const mark = text => highlight(text ?? "", words);
  const ado = workItems.length + pullRequests.length;
  const chip = (attribute, value, label, on) => `<button ${attribute}="${esc(value)}" aria-pressed="${on}" class="${on ? "on" : ""}">${esc(label)}</button>`;
  const where = result => [t("searchWhere")[result.where], result.excerpt && `« ${mark(result.excerpt)} »`].filter(Boolean).join(" : ");
  const adoLink = url => `<a class="link" href="${esc(url)}" target="_blank">${esc(t("openInAdo"))}</a>`;
  const row = (pillHtml, meta, title, href, line, actions) => `<article class="row"><div class="row-main"><div class="row-text">
      <div class="row-meta">${pillHtml}<span>${meta}</span></div>
      <h3>${href ? `<a href="${href}">${title}</a>` : title}</h3>
      ${line ? `<div class="row-line">${line}</div>` : ""}</div>
    <div class="row-actions">${actions}</div></div></article>`;

  // One of my open PBIs opens its page here; another PBI or bug can be reviewed with Claude (the review is about the
  // pull requests of a PBI, so not a Feature or a Test Suite); any other opens in Azure DevOps.
  const itemRow = item => {
    const mine = shared.lastData?.workItems.some(workItem => workItem.id === item.id);
    return row(pill(mine ? "WAIT" : "", mine ? t("searchMine")(item.type) : item.type),
      [`#${item.id}`, item.iteration, item.state, item.assignedTo].filter(Boolean).map(esc).join(" · "),
      mark(item.title), mine ? `#pbi/${item.id}` : "", where(item),
      mine ? `<a class="btn" href="#pbi/${item.id}">${esc(t("details"))}</a>`
        : `${/^(product backlog item|bug)$/i.test(item.type) ? claudeButton("pbi-review", item.id, "claudeReview") : ""}${adoLink(item.url)}`);
  };
  // A pull request the dashboard already shows opens its page; another active one can be reviewed with Claude, which
  // adds me to its reviewers.
  const known = new Set([...(shared.lastData?.reviews ?? []), ...(shared.lastData?.workItems ?? []).flatMap(item => item.pullRequests)].map(pr => pr.id));
  const prRow = pr => {
    const state = pr.status === "completed" ? t("merged") : pr.status === "abandoned" ? t("abandoned") : "";
    return row(pill(pr.isMine ? "WAIT" : "", pr.isMine ? t("searchMine")("PR") : "PR"),
      [`PR ${pr.id}`, `${pr.repository} → ${pr.targetBranch}`, pr.author, state || t("since")(ago(pr.createdDate))].filter(Boolean).map(esc).join(" · "),
      mark(pr.title), known.has(pr.id) ? `#pr/${pr.id}` : "", where(pr),
      known.has(pr.id) ? `<a class="btn" href="#pr/${pr.id}">${esc(t("details"))}</a>`
        : `${pr.status === "active" && !pr.isMine ? `<button data-claude="review/${pr.id}" data-query="&amp;addReviewer=1" data-label="claudeReview">${esc(t("claudeReview"))}</button>` : ""}${adoLink(pr.url)}`);
  };
  // A file of the vault opens in the reader; a question says its state.
  const folders = [...new Set(vault.map(file => file.folder))].sort();
  const fileRow = file => {
    const question = file.question && [t("qStates")[file.question.state] ?? file.question.state, file.question.to && `${t("qAskedTo")} ${file.question.to}`].filter(Boolean).join(" · ");
    return `<article class="row pad result"><div class="row-meta">${pill(file.question?.state === "waiting" ? "TODO" : file.pbi ? "WAIT" : "", file.pbi ? `PBI #${file.pbi}` : file.folder || "Vault")}
        <span>${[file.file.replaceAll("/", " / "), t("updatedAgo")(ago(file.updated)), file.count > 0 && t("searchPassages")(file.count), question].filter(Boolean).map(esc).join(" · ")}</span></div>
      <h3><a href="/${esc(file.file.split("/").map(encodeURIComponent).join("/"))}">${mark(file.title)}</a></h3>
      ${file.passages.map(passage => `<div class="row-line">${mark(passage)}</div>`).join("")}</article>`;
  };
  const shownFiles = vault.filter(file => !searchFolder || file.folder === searchFolder);

  view.innerHTML = `${head}
    <div class="sprints">${chip("data-search-scope", "all", `${t("searchAll")} · ${ado + vault.length}`, searchScope === "all")}${
      chip("data-search-scope", "ado", `Azure DevOps · ${ado}`, searchScope === "ado")}${chip("data-search-scope", "vault", `Vault · ${vault.length}`, searchScope === "vault")}</div>
    ${searchData.error ? `<div class="bad">${esc(t("refreshFailed")(searchData.error))}</div>` : ""}
    ${searchScope === "vault" ? "" : `<section class="group">
      <div class="head-row"><h2>Azure DevOps <span class="count">· ${ado}</span></h2>
        <div class="sprints">${chip("data-search-closed", "", t("searchClosed"), searchClosed)}</div></div>
      ${searchData.adoError ? `<div class="bad">${esc(t("refreshFailed")(searchData.adoError))}</div>` : ""}
      <div class="list">${[...workItems.map(itemRow), ...pullRequests.map(prRow)].join("") || `<div class="empty">${esc(t("searchNoAdo"))}</div>`}</div></section>`}
    ${searchScope === "ado" ? "" : `<section class="group">
      <div class="head-row"><h2>Vault <span class="count">· ${vault.length}</span></h2>
        ${folders.length > 1 ? `<div class="sprints">${chip("data-search-folder", "", t("searchAllFolders"), !searchFolder)}${
          folders.map(folder => chip("data-search-folder", folder, `${folder || "Vault"} · ${vault.filter(file => file.folder === folder).length}`, searchFolder === folder)).join("")}</div>` : ""}</div>
      <div class="list" data-vault="">${shownFiles.map(fileRow).join("") || `<div class="empty">${esc(t("searchNoVault"))}</div>`}</div></section>`}`;
}

// The search box of the header: Enter searches; "/" anywhere outside a field goes to it.
document.getElementById("searchForm").onsubmit = event => {
  event.preventDefault();
  const query = document.getElementById("searchInput").value.trim();
  if (!query) return;
  const number = /^(#|!|pr\s*)?(\d+)$/i.exec(query), id = number && Number(number[2]);
  const pullRequest = number && number[1] && number[1] !== "#";
  if (id && !pullRequest && shared.lastData?.workItems.some(item => item.id === id)) { location.hash = `#pbi/${id}`; return; }
  if (id && pullRequest && [...shared.lastData?.reviews ?? [], ...(shared.lastData?.workItems ?? []).flatMap(item => item.pullRequests)].some(pr => pr.id === id)) { location.hash = `#pr/${id}`; return; }
  searchData = null;
  if (location.hash === `#search/${encodeURIComponent(query)}`) loadSearch(query);
  else location.hash = `#search/${encodeURIComponent(query)}`;
};
document.addEventListener("keydown", event => {
  if (event.key !== "/" || event.ctrlKey || event.metaKey || event.altKey || event.target.closest?.("input, textarea, select, [contenteditable]")
    || document.getElementById("reviewDialog").open) return;
  event.preventDefault();
  document.getElementById("searchInput").select();
});
// Clicks on the chips of the results (called by the page's click listener).
export function searchClick(e) {
  const scope = e.target.closest("[data-search-scope]"), folder = e.target.closest("[data-search-folder]");
  if (scope) { searchScope = scope.dataset.searchScope; renderSearch(); }
  if (folder) { searchFolder = folder.dataset.searchFolder; renderSearch(); }
  if (e.target.closest("[data-search-closed]")) { searchClosed = !searchClosed; searchData = null; loadSearch(searchQuery); }
}

// The refresh button of the header: the search shown runs again.
export function rerunSearch() { searchData = null; loadSearch(searchQuery); }
