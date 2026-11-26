import { docLink } from "../claude.js";
import { hooks, shared } from "../state.js";
import { t } from "../texts.js";
import { ago, cap, esc, fullDate, markdown, pill, shortDate, shortDateTime } from "../util.js";
import { stepPanel } from "./parts.js";

// Questions asked to the PO or the team: one file each in the vault (rule "Questions" of the workspace CLAUDE.md),
// read from /api/questions on their own (no Azure DevOps call). A question is a draft, then waiting once asked, then
// resolved by an answer or dropped. The Questions view lists them all, the Questions tab of a PBI lists its own, and
// the forms write the files through POST /api/questions/<action>, which answers the new list.
export let questions = [], questionForm = null, questionFilter = "waiting";
// Questions unfolded or folded by hand (file -> shown); the others follow the default of their view.
const shownQuestions = new Map();
const questionRank = { waiting: 0, draft: 1, resolved: 2, dropped: 3 };
const questionChannels = ["ado", "teams", "email", "kb", "call", "other"];
export const stateOf = q => q.state in questionRank ? q.state : "draft";
const askedTime = q => new Date(q.asked || q.updated).getTime() || 0;
// Waiting ones first, the oldest on top; then the drafts and the closed ones, the last changed on top.
const sortQuestions = list => [...list].sort((a, b) => questionRank[stateOf(a)] - questionRank[stateOf(b)]
  || (stateOf(a) === "waiting" ? askedTime(a) - askedTime(b) : (b.updated ?? "").localeCompare(a.updated ?? "")));
// A date of a question file: with its time when it has one, as written when it is not a date.
const when = value => !value ? "" : isNaN(new Date(value)) ? value : value.includes("T") ? shortDateTime(value) : shortDate(value);
const messageLink = url => /^https?:\/\//.test(url ?? "") ? `<a class="link" href="${esc(url)}" target="_blank" rel="noopener">${esc(t("qOpenMessage"))}</a>` : "";
const questionNumber = q => { const match = /^q(\d+)-/.exec(q.name); return match ? `Q${Number(match[1])}` : q.title; };
const nowLocal = () => { const now = new Date(); now.setMinutes(now.getMinutes() - now.getTimezoneOffset()); return now.toISOString().slice(0, 16); };
const optionsHtml = (values, names, selected) => values.map(value =>
  `<option value="${esc(value)}"${value === selected ? " selected" : ""}>${esc(names[value] ?? value)}</option>`).join("");

export async function loadQuestions() {
  try {
    const response = await fetch("/api/questions");
    if (response.ok) setQuestions(await response.json());
  } catch { }
}
function setQuestions(list) {
  questions = list;
  updateQuestionsTab();
  drawQuestions();
}
const drawQuestions = () => { renderQuestions(); hooks.renderPbi(); };
// What is typed in an open question form survives a redraw of its view (a refresh, a question unfolded): its values,
// focus and cursor are taken before and put back in the same form after.
export function formValues(view) {
  const form = view.querySelector("form.qform");
  const focused = form?.contains(document.activeElement) ? document.activeElement : null;
  return form && { key: form.dataset.qsubmit + form.dataset.file, busy: form.dataset.busy,
    focus: focused && { name: focused.name, start: focused.selectionStart, end: focused.selectionEnd },
    fields: [...form.elements].filter(field => field.name).map(field => ({ name: field.name, type: field.type, value: field.value, checked: field.checked })) };
}
export function restoreForm(view, kept) {
  const form = view.querySelector("form.qform");
  if (!kept || !form || form.dataset.qsubmit + form.dataset.file !== kept.key) return;
  kept.fields.forEach(({ name, type, value, checked }) => {
    const field = type === "radio" ? [...form.querySelectorAll(`[name="${name}"]`)].find(radio => radio.value === value) : form.elements[name];
    if (!field) return;
    if (type === "radio" || type === "checkbox") field.checked = checked;
    else field.value = value;
    if (name === "pbi") form.elements.follows.innerHTML = followsOptions(Number(value) || null, "");
    if (name === "askedNow") askedToggle(form, checked);
  });
  if (kept.busy) setBusy(form, true);
  // The field that was being typed in keeps the focus and the cursor.
  const field = kept.focus?.name && form.elements[kept.focus.name];
  if (field?.focus) {
    field.focus({ preventScroll: true });
    try { field.setSelectionRange(kept.focus.start, kept.focus.end); } catch { }
  }
}
const askedToggle = (form, on) => { const fields = form.querySelector("[data-asked-fields]"); fields.hidden = fields.disabled = !on; };
// A form being saved cannot be sent again.
const setBusy = (form, on) => { form.dataset.busy = on ? "1" : ""; form.querySelector("[type=submit]").disabled = on; };
export function updateQuestionsTab() {
  const waiting = questions.filter(q => stateOf(q) === "waiting").length;
  document.getElementById("questionsTab").innerHTML = esc(t("tabQuestions")) + (waiting ? pill("TODO", waiting, "small") : "");
}

// One question: its state (and how long it has been waiting), who it was asked to and where, its answers with their
// source and link, then what can be done with it, or the form that was opened on it.
function questionCard(q, showPbi) {
  const state = stateOf(q);
  const chase = state === "waiting" && Date.now() - askedTime(q) > 7 * 864e5;
  const item = shared.lastData?.workItems.find(workItem => workItem.id === q.pbi);
  const related = other => `<a class="link" href="#" data-qshow="${esc(other.file)}" title="${esc(other.title)}">${esc(questionNumber(other))}</a>`;
  const follows = q.follows && questions.find(other => other.pbi === q.pbi && other.name === q.follows);
  const followers = questions.filter(other => other.pbi === q.pbi && other.follows === q.name);
  const meta = [
    showPbi && (!q.pbi ? esc(t("qNoPbi")) : item ? `<a class="link" href="#pbi/${q.pbi}/06-open-questions">#${q.pbi} ${esc(item.title)}</a>` : `#${q.pbi}`),
    state === "draft" && !q.asked ? esc(t("qDraftMeta"))
      : [q.to ? `${esc(t("qAskedTo"))} <b>${esc(q.to)}</b>` : esc(t("qAskedOnly")), esc(t("qChannels")[q.channel] ?? q.channel ?? ""), esc(when(q.asked))].filter(Boolean).join(" · "),
    messageLink(q.link),
    follows && `${esc(t("qFollows"))} ${related(follows)}`,
    followers.length && `${esc(t("qLedTo"))} ${followers.map(related).join(", ")}`,
  ].filter(Boolean).join(" · ");
  // The text of the question shows at once in the tab of its PBI while it is open, and on demand elsewhere.
  const open = shownQuestions.get(q.file) ?? (!showPbi && questionRank[state] < 2);
  const button = (kind, label, cls = "") => `<button class="${cls}" data-qform="${kind}" data-file="${esc(q.file)}">${esc(t(label))}</button>`;
  const setState = (value, label) => `<button class="link" data-qstate="${value}" data-file="${esc(q.file)}">${esc(t(label))}</button>`;
  const toggle = `<button class="link" data-qtoggle="${esc(q.file)}" data-open="${open}">${esc(t(open ? "qHide" : "qShow"))}</button>`;
  const actions = state === "draft" ? `${button("ask", "qIAsked")}<button data-qcopy="${esc(q.file)}">${esc(t("qCopy"))}</button>${toggle}${setState("dropped", "qDrop")}`
    : state === "waiting" ? `${button("answer", "qAnswer", "primary")}${button("decide", "qDecide")}${toggle}${button("new", "qFollowUp", "link")}${setState("dropped", "qDrop")}`
    : `${toggle}${button("new", "qFollowUp", "link")}${setState(q.asked ? "waiting" : "draft", "qReopen")}`;
  const form = questionForm?.file === q.file && questionForm.kind !== "new";
  return `<article class="row pad question ${state}" data-qcard="${esc(q.file)}" data-vault="${esc(q.file)}">
    <div class="row-meta">${pill(state === "waiting" ? "TODO" : state === "resolved" ? "OK" : "", [t("qStates")[state], state === "waiting" && ago(q.asked || q.updated), chase && t("qChase")].filter(Boolean).join(" · "), "", state === "waiting" ? fullDate(q.asked || q.updated) : "")}<span>${meta}</span></div>
    <h3>${esc(q.title)}</h3>
    ${open && q.question ? `<div class="markdown">${markdown(q.question)}</div>` : ""}
    ${q.answers.map(answer => `<div class="answer"><div class="row-meta"><b>${esc(answer.author)}</b><span>${esc(when(answer.date))}</span>
        ${pill("WAIT", t("qChannels")[answer.source] ?? answer.source, "small")}${messageLink(answer.link)}</div>
      <div class="markdown">${markdown(answer.text)}</div></div>`).join("")}
    ${open && q.notes ? `<div><div class="label">${esc(t("qNotes"))}</div><div class="markdown">${markdown(q.notes)}</div></div>` : ""}
    ${form ? (questionForm.kind === "ask" ? askForm(q) : answerForm(q, questionForm.kind === "decide")) : `<div class="row-actions">${actions}</div>`}</article>`;
}

// The forms. The names already met (people asked, authors of answers) are proposed where a name is typed.
const formHtml = (kind, file, title, fields, submit) => `<form class="qform" data-qsubmit="${kind}" data-file="${esc(file ?? "")}">
    <b>${esc(title)}</b>${fields}
    <datalist id="people">${[...new Set(questions.flatMap(q => [...(q.to ?? "").split(","), ...q.answers.map(answer => answer.author)])
      .map(name => name.trim()).filter(Boolean))].sort().map(name => `<option value="${esc(name)}">`).join("")}</datalist>
    <div class="row-actions"><button class="primary" type="submit">${esc(submit)}</button><button type="button" data-qcancel>${esc(t("qCancel"))}</button><span class="note"></span></div></form>`;
// Who a question was asked to, where, when, and the link of the message (pasted from Teams, Azure DevOps or a mail).
const askedFields = q => `<div class="fields">
    <label>${esc(t("qTo"))}<input name="to" required list="people" autocomplete="off" value="${esc(q?.to ?? "")}"></label>
    <label>${esc(t("qChannel"))}<select name="channel">${optionsHtml(questionChannels, t("qChannels"), q?.channel ?? "teams")}</select></label>
    <label>${esc(t("qAskedOn"))}<input name="asked" type="datetime-local" required value="${nowLocal()}"></label>
    <label>${esc(t("qLink"))}<input name="link" type="url" placeholder="https://" value="${esc(q?.link ?? "")}"></label></div>`;
const askForm = q => formHtml("ask", q.file, t("qIAsked"), askedFields(q), t("qSaveAsk"));

// An answer, or my own decision (source "decision", under the name my last decision was written with).
function answerForm(q, decide) {
  const author = decide ? questions.flatMap(other => other.answers).filter(answer => answer.source === "decision").pop()?.author ?? ""
    : (q.to ?? "").split(",")[0].trim();
  return formHtml("answer", q.file, t(decide ? "qDecide" : "qAnswer"), `<div class="fields">
      <label>${esc(t("qSource"))}<select name="source">${optionsHtml([...questionChannels, "decision"], t("qChannels"), decide ? "decision" : q.channel ?? "teams")}</select></label>
      <label>${esc(t("qAuthor"))}<input name="author" required list="people" autocomplete="off" value="${esc(author)}"></label>
      <label>${esc(t("qAnswerDate"))}<input name="date" type="datetime-local" required value="${nowLocal()}"></label>
      <label>${esc(t("qLink"))}<input name="link" type="url" placeholder="https://"></label></div>
    <label>${esc(t("qAnswerText"))}<textarea name="text" required></textarea></label>
    <div class="row-actions"><span class="muted">${esc(t("qAfter"))}</span>
      <label class="check"><input type="radio" name="state" value="resolved" checked>${esc(t("qResolved"))}</label>
      <label class="check"><input type="radio" name="state" value="waiting">${esc(t("qStillWaiting"))}</label></div>`, t("qSaveAnswer"));
}

// A new question: for one of my PBIs (or one that already has questions) or for none, maybe following another one,
// kept as a draft unless it was already asked.
const followsOptions = (pbi, selected) => `<option value=""></option>` + sortQuestions(questions.filter(q => (q.pbi ?? null) === pbi))
  .map(q => `<option value="${esc(q.name)}"${q.name === selected ? " selected" : ""}>${esc(q.title)}</option>`).join("");
function newQuestionForm({ pbi = null, follows = "" }) {
  const titles = new Map([...questions.filter(q => q.pbi).map(q => [q.pbi, ""]), ...(shared.lastData?.workItems ?? []).map(item => [item.id, item.title])]);
  if (pbi) titles.set(pbi, titles.get(pbi) ?? "");
  return formHtml("create", "", t("qNew"), `<div class="fields">
      <label>${esc(t("qPbi"))}<select name="pbi"><option value="">${esc(t("qNoPbi"))}</option>${[...titles.keys()].sort((a, b) => b - a).map(id =>
        `<option value="${id}"${id === pbi ? " selected" : ""}>#${id} ${esc(titles.get(id))}</option>`).join("")}</select></label>
      <label>${esc(t("qFollowsField"))}<select name="follows">${followsOptions(pbi, follows)}</select></label></div>
    <label>${esc(t("qTitle"))}<input name="title" required autocomplete="off"></label>
    <label>${esc(t("qText"))}<textarea name="question"></textarea></label>
    <label class="check"><input type="checkbox" name="askedNow" data-qasked>${esc(t("qAlreadyAsked"))}</label>
    <fieldset data-asked-fields hidden disabled>${askedFields()}</fieldset>`, t("qSaveNew"));
}

// The Questions view: every question of the vault, the ones waiting for an answer first.
export function renderQuestions() {
  if (shared.activeView !== "questions") return;
  const filters = { waiting: ["waiting"], draft: ["draft"], closed: ["resolved", "dropped"] };
  const inFilter = key => questions.filter(q => filters[key].includes(stateOf(q)));
  const waiting = sortQuestions(inFilter("waiting")), shown = sortQuestions(inFilter(questionFilter));
  const view = document.getElementById("questionsView"), kept = formValues(view);
  view.innerHTML = `<div class="head-row">
      <div><h2>${esc(t("qHeadline")(waiting.length))}</h2>
        <div class="muted"${waiting.length ? ` title="${esc(fullDate(waiting[0].asked || waiting[0].updated))}"` : ""}>${esc(cap([waiting.length && t("qOldest")(ago(waiting[0].asked || waiting[0].updated)), t("qDraftsToAsk")(inFilter("draft").length)].filter(Boolean).join(" · ")))}</div></div>
      <button class="primary" data-qform="new">${esc(t("qNew"))}</button></div>
    <div class="sprints">${Object.keys(filters).map(key => `<button data-qfilter="${key}" aria-pressed="${key === questionFilter}" class="${key === questionFilter ? "on" : ""}">${
      esc(t("qFilters")[key])} · ${inFilter(key).length}</button>`).join("")}</div>
    ${questionForm?.kind === "new" ? newQuestionForm(questionForm) : ""}
    <div class="list">${shown.map(q => questionCard(q, true)).join("") || `<div class="empty">${esc(t("qEmptyFilter"))}</div>`}</div>`;
  view.querySelectorAll(".markdown a").forEach(a => a.target = "_blank");
  restoreForm(view, kept);
}

// The Questions tab of a PBI: its questions, a new one, or Claude to draft them (the step 06-open-questions).
export function questionsPanel(item) {
  const list = sortQuestions(questions.filter(q => q.pbi === item.id));
  const claude = `<button data-claude="step/${item.id}" data-query="&amp;step=06-open-questions" data-label="claudeQuestions">${esc(t("claudeQuestions"))}</button>`;
  const add = `<button class="primary" data-qform="new" data-pbi="${item.id}">${esc(t("qNew"))}</button>`;
  const form = questionForm?.kind === "new" ? newQuestionForm(questionForm) : "";
  // A file with no question file next to it is in the older format, its questions inline: shown as the file it is.
  const index = item.files?.["06-open-questions"];
  if (!list.length && index) return `<div class="head-row"><span></span><span class="row-actions">${add}</span></div>${form}${stepPanel(item, "06-open-questions")}`;
  if (!list.length) return `${form}<section class="row empty-step"><h2>${esc(t("qNone"))}</h2>
    <p class="muted">${esc(t("stepHelp")["06-open-questions"])}</p><div class="row-actions">${add}${claude}</div></section>`;
  const counts = Object.keys(questionRank).map(state => [state, list.filter(q => stateOf(q) === state).length]).filter(([, count]) => count)
    .map(([state, count]) => t("qCounts")[state](count)).join(" · ");
  return `<div class="head-row"><span class="muted">${esc(counts)}${index ? ` · ${docLink(`pbi:${item.id}:06-open-questions`, t("qReadIndex"))}` : ""}</span><span class="row-actions">${claude}${add}</span></div>
    ${form}<div class="list">${list.map(q => questionCard(q, false)).join("")}</div>`;
}

async function postQuestion(action, body, form) {
  try {
    const response = await fetch(`/api/questions/${action}`, { method: "POST", headers: { "X-Dashboard": "1", "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail || response.statusText);
    questionForm = null;
    dropForms();
    document.getElementById("error").style.display = "none";
    setQuestions(data);
  } catch (e) {
    if (form?.isConnected) setBusy(form, false);
    const note = form?.querySelector(".note") ?? document.getElementById("error");
    note.textContent = t("qSaveFailed")(e.message);
    note.style.display = "block";
  }
}

// A form that is opened or closed starts from its own values, not from what the form it replaces held.
const dropForms = () => document.querySelectorAll("form.qform").forEach(form => form.remove());
// Clicks on the question cards and forms (called by the page's click listener).
export function questionClick(e) {
  const pick = name => e.target.closest(`[data-${name}]`);
  const formButton = pick("qform"), toggle = pick("qtoggle"), show = pick("qshow"), filter = pick("qfilter"), state = pick("qstate"), copy = pick("qcopy");
  if (formButton) {
    // "New" from a card is a follow-up of its question; from the tab of a PBI it is a question of that PBI.
    const q = questions.find(other => other.file === formButton.dataset.file);
    questionForm = formButton.dataset.qform === "new"
      ? { kind: "new", pbi: q?.pbi ?? (Number(formButton.dataset.pbi) || null), follows: q?.name ?? "" }
      : { kind: formButton.dataset.qform, file: q.file };
    dropForms();
    drawQuestions();
    [...document.querySelectorAll(".qform")].find(form => form.offsetParent)?.scrollIntoView({ block: "center" });
  }
  if (pick("qcancel")) { questionForm = null; dropForms(); drawQuestions(); }
  if (toggle) { shownQuestions.set(toggle.dataset.qtoggle, toggle.dataset.open !== "true"); drawQuestions(); }
  if (show) {
    // The question another one follows or led to: unfolded, under the filter that holds it, and scrolled to.
    e.preventDefault();
    const q = questions.find(other => other.file === show.dataset.qshow);
    shownQuestions.set(q.file, true);
    questionFilter = { waiting: "waiting", draft: "draft" }[stateOf(q)] ?? "closed";
    drawQuestions();
    [...document.querySelectorAll("[data-qcard]")].find(card => card.dataset.qcard === q.file && card.offsetParent)?.scrollIntoView();
  }
  if (filter) { questionFilter = filter.dataset.qfilter; renderQuestions(); }
  if (state) postQuestion("state", { file: state.dataset.file, state: state.dataset.qstate });
  if (copy) {
    const q = questions.find(other => other.file === copy.dataset.qcopy);
    // A question without text is its title; without the clipboard (a page not on localhost) the button says so.
    const done = label => { copy.textContent = t(label); setTimeout(() => copy.textContent = t("qCopy"), 2000); };
    (navigator.clipboard?.writeText(q.question || q.title) ?? Promise.reject()).then(() => done("qCopied"), () => done("qCopyFailed"));
  }
}
document.addEventListener("submit", e => {
  const form = e.target.closest("form.qform");
  if (!form) return;
  e.preventDefault();
  if (form.dataset.busy) return;
  const body = Object.fromEntries(new FormData(form));
  setBusy(form, true);
  if (form.dataset.file) body.file = form.dataset.file;
  if (form.dataset.qsubmit === "create") {
    body.pbi = Number(body.pbi) || null;
    body.pbiTitle = shared.lastData?.workItems.find(item => item.id === body.pbi)?.title ?? "";
    body.state = body.askedNow ? "waiting" : "draft";
  }
  postQuestion(form.dataset.qsubmit, body, form);
});
document.addEventListener("change", e => {
  const form = e.target.closest("form.qform");
  if (!form) return;
  // "Already asked" opens the fields that say to whom and where; another PBI offers its own questions to follow.
  if (e.target.matches("[data-qasked]")) askedToggle(form, e.target.checked);
  if (e.target.name === "pbi") form.elements.follows.innerHTML = followsOptions(Number(e.target.value) || null, "");
});

// Leaving a view closes the form that was open.
export const closeQuestionForm = () => { questionForm = null; };
