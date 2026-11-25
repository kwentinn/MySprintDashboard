import { openClaude } from "./claude.js";
import { openDocument, openVaultLink, registerDocuments } from "./reader.js";
import { hooks, shared } from "./state.js";
import { lang, setLang, t } from "./texts.js";
import { cap, esc } from "./util.js";
import { loadDaily, updateDailyTab } from "./views/daily.js";
import { renderPbi } from "./views/pbi.js";
import { renderPr } from "./views/pr.js";
import { closeQuestionForm, loadQuestions, questionClick, renderQuestions, updateQuestionsTab } from "./views/questions.js";
import { loadSearch, renderSearch, rerunSearch, searchClick } from "./views/search.js";
import { renderToday, toggleSprint, updateReviewPlaceholder } from "./views/today.js";

// The page: it loads the data, keeps it fresh, reads the address and draws the views (views/*.js).

const refreshSeconds = Number(new URLSearchParams(location.search).get("refresh")) || 600;
let nextRefresh = 0;

// Draws the rows of the last data; called after each refresh and when the language changes.
function render() {
  if (!shared.lastData) return;
  registerDocuments();
  todoCount = renderToday();
  updateTabIcon();
  renderQuestions();
  renderSearch();
  renderPbi();
  renderPr();
}

// Tab icon and title: a checklist, with a badge of the rows to do when there are some ("(3) Today").
let todoCount = 0;
function updateTabIcon() {
  const label = todoCount > 99 ? "99+" : String(todoCount);
  const badge = todoCount
    ? `<circle cx='23' cy='23' r='9' fill='#D85A30'/><text x='23' y='27' text-anchor='middle' font-size='${label.length > 1 ? 9 : 12}' font-weight='700' fill='white' font-family='Segoe UI, sans-serif'>${label}</text>`
    : "<path d='M17 11h7M17 20h7' stroke='#B5D4F4' stroke-width='2.4' stroke-linecap='round'/>";
  const svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'><rect x='3' y='3' width='26' height='26' rx='6' fill='#185FA5'/>"
    + "<path d='M8 11l2 2 4-4M8 20l2 2 4-4' stroke='white' stroke-width='2.4' fill='none' stroke-linecap='round' stroke-linejoin='round'/>"
    + badge + "</svg>";
  document.getElementById("favicon").href = `data:image/svg+xml,${encodeURIComponent(svg)}`;
  document.title = (todoCount ? `(${label}) ` : "") + t("title");
}

// Static texts (data-i18n, data-i18n-title) and the date in the header.
function translatePage() {
  document.documentElement.lang = lang;
  document.querySelectorAll("[data-i18n]").forEach(element => element.textContent = t(element.dataset.i18n));
  document.querySelectorAll("[data-i18n-title]").forEach(element => element.title = element.ariaLabel = t(element.dataset.i18nTitle));
  document.querySelectorAll("[data-i18n-placeholder]").forEach(element => element.placeholder = element.ariaLabel = t(element.dataset.i18nPlaceholder));
  updateReviewPlaceholder();
  updateDailyTab();
  updateQuestionsTab();
  updateTabIcon();
  document.getElementById("today").textContent = cap(new Date().toLocaleDateString(t("locale"), { weekday: "long", day: "numeric", month: "long" }));
  document.getElementById("lang").value = lang;
}

async function load() {
  if (shared.loading) return;
  shared.loading = true;
  loadQuestions();
  const main = document.querySelector("main");
  main.classList.add("loading");
  document.getElementById("updated").innerHTML = `<span class="spinner"></span> ${esc(t("updating"))}`;
  const error = document.getElementById("error");
  try {
    const response = await fetch("/api/today");
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail || response.statusText);
    shared.lastData = data;
    render();
    document.getElementById("updated").dataset.at = data.generatedAt;
    error.style.display = "none";
  } catch (e) {
    error.textContent = t("refreshFailed")(e.message);
    error.style.display = "block";
  } finally {
    shared.loading = false;
    main.classList.remove("loading");
    // A first load that failed leaves an open PBI or PR page without data: stop its spinner.
    if (!shared.lastData) { renderPbi(); renderPr(); }
    nextRefresh = Date.now() + refreshSeconds * 1000;
  }
}

function tick() {
  const updated = document.getElementById("updated");
  const at = updated.dataset.at ? new Date(updated.dataset.at).toLocaleTimeString(t("locale"), { hour: "2-digit", minute: "2-digit" }) : "-";
  if (!shared.loading) updated.textContent = t("updated")(at);
  if (!shared.loading && Date.now() >= nextRefresh) load();
}

document.getElementById("lang").onchange = event => {
  setLang(event.target.value);
  shared.docLang = null;
  try { localStorage.setItem("dashboard.lang", lang); } catch { }
  translatePage();
  render();
  if (!shared.loading) tick();
};
document.getElementById("refresh").onclick = () => {
  if (shared.activeView === "daily") return loadDaily(true);
  // On the Search view, the search shown runs again too.
  if (shared.activeView === "search") rerunSearch();
  load();
};

// The views share the page: Today, the daily (#daily), one PBI (#pbi/<id>/<tab>) and one pull request
// (#pr/<id>/<tab>). The address keeps the view, so a reload and the browser's back button work.
let shownPage = "", listScroll = 0;
function route() {
  const [name, id, tab] = location.hash.slice(1).split("/");
  const view = ["daily", "questions"].includes(name) || (name === "search" && id) ? name : ["pbi", "pr"].includes(name) && Number(id) ? name : "today";
  // Another PBI or pull request starts folded, from its top; a tab of the same one stays where it is. Leaving the
  // list for such a page keeps the place in the list, to come back to it.
  const isPage = name => name === "pbi" || name === "pr";
  const changed = shownPage !== `${view}/${id}`, backToList = isPage(shared.activeView) && !isPage(view);
  if (isPage(view) && !isPage(shared.activeView)) listScroll = window.scrollY;
  if (changed) shared.descOpen = false;
  shownPage = `${view}/${id}`;
  shared.activeView = view;
  shared.pbiRoute = view === "pbi" ? { id: Number(id), tab } : null;
  shared.prRoute = view === "pr" ? { id: Number(id), tab } : null;
  document.querySelectorAll(".tabs a").forEach(link => link.classList.toggle("on", link.dataset.tab === view));
  ["today", "daily", "questions", "search", "pbi", "pr"].forEach(key => document.getElementById(`${key}View`).hidden = key !== view);
  if (view === "daily") loadDaily();
  if (view === "search") { let words = id; try { words = decodeURIComponent(id); } catch { } loadSearch(words); }
  closeQuestionForm();
  renderQuestions();
  renderPbi();
  renderPr();
  if (changed && isPage(view)) window.scrollTo(0, 0);
  if (backToList) window.scrollTo(0, listScroll);
}
window.onhashchange = route;

document.addEventListener("click", e => {
  openVaultLink(e);
  const doc = e.target.closest("[data-doc]");
  if (doc) openDocument(doc.dataset.doc);
  const claude = e.target.closest("[data-claude]");
  if (claude && !claude.disabled) openClaude(claude);
  const sprint = e.target.closest("[data-sprint]");
  if (sprint) toggleSprint(sprint.dataset.sprint);
  const fileLanguage = e.target.closest("[data-doclang]");
  if (fileLanguage) { shared.docLang = fileLanguage.dataset.doclang; renderPbi(); renderPr(); }
  if (e.target.closest("[data-desc-toggle]")) { shared.descOpen = !shared.descOpen; renderPbi(); renderPr(); }
  questionClick(e);
  searchClick(e);
});

// The views call these without importing this file.
Object.assign(hooks, { load, render, renderPbi });
translatePage();
load();
route();
setInterval(tick, 1000);
