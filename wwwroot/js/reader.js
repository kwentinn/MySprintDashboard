import { shared } from "./state.js";
import { t } from "./texts.js";
import { ago, esc, frontmatter, localized, markdown } from "./util.js";

// Markdown files of the vault: reviews (Reviews/<pr>-review.md, <pr>-threads.md) and PBI working files
// (PBIs/pbi-<id>/<step>.md), keyed "review:<pr>" / "threads:<pr>" / "pbi:<id>:<step>". Rendered with marked and
// sanitized with DOMPurify; raw text if the CDN is unreachable.
export const documents = new Map();

// A file of the vault as the dialog shows it: its frontmatter becomes one line above the text, with its last change
// and the sessions that wrote it.
export const docText = doc => {
  const { fields, body } = frontmatter(doc.markdown);
  return (fields.session ? `*${t("writtenBy")(ago(fields.updated || doc.updated), fields.sessions || fields.session)}*\n\n` : "") + body;
};

// The documents the dialog opens, in the page language.
export function registerDocuments() {
  documents.clear();
  const add = (key, report, label) => {
    const doc = localized(report);
    if (!doc) return;
    documents.set(key, { file: `${doc.file} · ${label}`, path: doc.file, markdown: docText(doc) });
  };
  shared.lastData.workItems.forEach(item => ["00-status", "06-open-questions"].forEach(step => add(`pbi:${item.id}:${step}`, item.files?.[step], `#${item.id}`)));
}

export function openDocument(key) {
  const doc = documents.get(key);
  if (!doc) return;
  readerTrail.length = 0;
  readerTrail.push(doc.path);
  showDialog(doc.file, markdown(doc.markdown), doc.path);
}

// The dialog is the reader of the vault's markdown files: `file` (relative to the vault) is what the links of the
// text shown are relative to, and Back returns to the file the last link was followed from.
export const readerTrail = [];
export function showDialog(title, html, file = "") {
  document.getElementById("reviewFile").textContent = title;
  document.getElementById("reviewBack").hidden = readerTrail.length < 2;
  const body = document.getElementById("reviewBody");
  body.dataset.vault = file;
  body.innerHTML = html;
  body.querySelectorAll("a").forEach(a => a.target = "_blank");
  const dialog = document.getElementById("reviewDialog");
  if (!dialog.open) dialog.showModal();
  dialog.scrollTop = 0;
}

// The file of the vault a link of a vault text points to, or null for any other link (a site, an anchor). A text of the vault sits in an element whose data-vault is its file: a relative link starts from
// the folder of that file, a link that starts with "/" from the root of the vault.
export function vaultPath(link) {
  const href = link.getAttribute("href");
  if (/^([a-z][a-z0-9+.-]*:|#|\/\/)/i.test(href)) return null;
  let path;
  try { path = decodeURIComponent(href.split(/[?#]/)[0]); } catch { return null; }
  const parts = path.startsWith("/") ? [] : link.closest("[data-vault]").dataset.vault.split("/").slice(0, -1);
  path.split("/").forEach(part => { if (part === "..") parts.pop(); else if (part && part !== ".") parts.push(part); });
  return parts.join("/");
}

// A link to another file of the vault opens in the reader instead of leaving the page: on a click, and on a middle
// click too, which would open a new tab on an address the server does not have.
export function openVaultLink(event) {
  const link = event.target.closest?.("[data-vault] a[href]"), file = link && vaultPath(link);
  if (file) { event.preventDefault(); openVaultFile(file); }
}
document.addEventListener("auxclick", openVaultLink);

// Opens a markdown file of the vault in the reader (GET /api/vault).
export async function openVaultFile(path) {
  // Another kind of file (a PDF, an image) is in the vault too, but the reader only shows markdown: it says so
  // instead of sending the browser to an address the server does not have.
  const isMarkdown = /\.md$/i.test(path);
  let doc = null;
  try {
    const response = isMarkdown && await fetch(`/api/vault?path=${encodeURIComponent(path)}`);
    if (response?.ok) doc = await response.json();
  } catch { }
  if (readerTrail.at(-1) !== path) readerTrail.push(path);
  showDialog(doc?.file ?? path, doc ? markdown(docText(doc)) : `<p class="bad">${esc(t(isMarkdown ? "fileMissing" : "fileNotMarkdown")(path))}</p>`, doc?.file ?? path);
}

document.getElementById("reviewClose").onclick = () => document.getElementById("reviewDialog").close();
document.getElementById("reviewDialog").onclose = () => readerTrail.length = 0;
document.getElementById("reviewBack").onclick = () => { readerTrail.pop(); openVaultFile(readerTrail.at(-1)); };
