import { shared } from "../state.js";
import { lang, t } from "../texts.js";
import { ago, esc, frontmatter, markdown } from "../util.js";

// Pieces the pages of a PBI and of a pull request share.

// A comment thread of a pull request, whole: its file and line, a link to Azure DevOps and its comments.
export const plainText = text => String(text ?? "").replace(/[`*_>#\[\]]/g, "").replace(/\s+/g, " ").trim();
export const conversationHtml = thread => `<div class="conversation">
    <div class="where">${thread.file ? `<code>${esc(thread.file)}${thread.line ? `:${thread.line}` : ""}</code>` : `<span>${esc(t("prLevelThread"))}</span>`}
      <a href="${esc(thread.url)}" target="_blank">${esc(t("openInAdo"))}</a></div>
    ${thread.comments.map(comment => `<div class="comment"><b>${esc(comment.author)}</b> <span class="muted">· ${esc(t("since")(ago(comment.date)))}</span>
      <div class="markdown">${markdown(comment.content ?? "")}</div></div>`).join("")}
  </div>`;

// A markdown file of the vault in a panel: where it comes from, the FR / EN switch when its French copy exists (else
// `translate`, a button that asks Claude to write it), `action` (run Claude on it again), then the file. A missing
// file says what Claude would write there.
export function filePanel(report, { translate, action, missing }) {
  if (!report) return `<section class="row empty-step"><h2>${esc(missing.title)}</h2><p class="muted">${esc(missing.help)}</p>${missing.button}</section>`;
  // An older file may exist in French only: it then comes as the main one, with nothing to switch to or translate.
  const frenchOnly = report.file.endsWith(".fr.md");
  const shown = shared.docLang ?? lang;
  const doc = shown === "fr" && report.fr ? report.fr : report;
  const { fields, body } = frontmatter(doc.markdown);
  const sessions = (fields.sessions || fields.session || "").replace(/[\[\]]/g, "").split(",").filter(id => id.trim()).length;
  const language = report.fr
    ? `<span class="seg" role="group" aria-label="${esc(t("fileLanguage"))}">${["fr", "en"].map(code =>
        `<button data-doclang="${code}" aria-pressed="${code === shown}">${code.toUpperCase()}</button>`).join("")}</span>`
    : frenchOnly ? "" : translate;
  return `<section class="row panel">
    <div class="panel-bar"><span>${[doc.file.split("/").pop(), t("updatedAgo")(ago(fields.updated || doc.updated)), sessions && t("writtenBySessions")(sessions),
        !report.fr && !frenchOnly && t("englishOnly")].filter(Boolean).map(esc).join(" · ")}</span>
      <span class="row-actions">${language}${action || ""}</span></div>
    <div class="markdown" data-vault="${esc(doc.file)}">${markdown(body)}</div></section>`;
}

// A working file of the PBI, with a button that runs Claude on this step again.
export function stepPanel(item, step) {
  const claude = (kind, label, cls) =>
    `<button class="${cls}" data-claude="${kind}/${item.id}" data-query="&amp;step=${step}" data-label="${label}">${esc(t(label))}</button>`;
  return filePanel(item.files?.[step], { translate: claude("translate", "claudeTranslate", "dashed"), action: claude("step", "claudeStep", ""),
    missing: { title: t("stepMissing")(t("steps")[step]), help: t("stepHelp")[step], button: claude("step", "claudeStepStart", "primary") } });
}

// After a PBI or PR page is drawn: links of the markdown open a new tab, and "show more" goes away when the folded
// description already shows whole.
export function finishPage(view) {
  view.querySelectorAll(".markdown a").forEach(a => a.target = "_blank");
  const desc = view.querySelector(".desc");
  if (desc && !shared.descOpen && desc.scrollHeight <= desc.clientHeight + 2) view.querySelector("[data-desc-toggle]").hidden = true;
}
// A PBI or PR page before the first data: the way back, and a spinner while the data loads (if the load failed, the
// error banner says why).
export const pagePlaceholder = () => `<div>${backLink()}</div>${shared.loading ? `<div class="row placeholder"><span class="spinner"></span>${esc(t("loading"))}</div>` : ""}`;
export const backLink = () => `<a class="link" href="#"><svg aria-hidden="true" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M15 6l-6 6 6 6"/></svg>${esc(t("tabToday"))}</a>`;
