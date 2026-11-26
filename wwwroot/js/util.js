import { lang, t } from "./texts.js";

// HTML that is already safe: `esc` leaves it as it is, so that it can sit among texts that are escaped together.
const safe = html => Object.assign(new String(html), { safe: true });
export const esc = value => value?.safe ? String(value) : String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
export const cap = text => text.replace(/^./, c => c.toUpperCase());
// How long ago, as the words that follow "... ago": minutes under an hour, hours under two days, then days.
export const ago = iso => {
  if (!iso) return "";
  const minutes = (Date.now() - new Date(iso)) / 6e4;
  return minutes < 60 ? t("minutes")(Math.floor(minutes)) : minutes < 48 * 60 ? `${Math.round(minutes / 60)} h` : `${Math.round(minutes / 1440)} ${t("day")}`;
};
// A date in full, with its time when it has one: what hovering an age shows.
export const fullDate = iso => !iso ? "" : new Date(iso).toLocaleString(t("locale"), String(iso).includes("T") ? { dateStyle: "full", timeStyle: "short" } : { dateStyle: "full" });
// An age in a sentence (`phrase` puts the age in its words, "2 minutes" in "2 minutes ago"), with the date and time
// on hover. Safe HTML: it goes through `esc` unchanged.
export const aged = (iso, phrase = age => age) => iso ? safe(`<span class="age" title="${esc(fullDate(iso))}">${esc(phrase(ago(iso)))}</span>`) : "";

export const pill = (status, text, extra = "", title = "") => `<span class="pill ${status} ${extra}"${title ? ` title="${esc(title)}"` : ""}>${esc(text)}</span>`;
export const orderPill = (order, sprint) => order ? `<span class="order" title="${esc(t("backlogOrderTitle")(sprint ?? ""))}">${order}</span>` : "";

export const shortDate = iso => new Date(iso).toLocaleDateString(t("locale"), { day: "2-digit", month: "2-digit" });
export const shortDateTime = iso => `${shortDate(iso)} ${new Date(iso).toLocaleTimeString(t("locale"), { hour: "2-digit", minute: "2-digit" })}`;

// Most recent of some ISO dates (missing ones ignored), as an ISO string.
export function latest(...dates) {
  const times = dates.filter(Boolean).map(date => new Date(date).getTime());
  return times.length ? new Date(Math.max(...times)).toISOString() : null;
}

// Obsidian links between notes, [[Folder/Note|text]] or [[Note#Section]], become markdown links to the note's file
// from the root of the vault ("/Folder/Note.md"), which the reader opens (see vaultPath).
const wikilinks = text => text.replace(/\[\[([^\]|#\n]+)(?:#[^\]|\n]*)?(?:\|([^\]\n]+))?\]\]/g,
  (_, target, alias) => `[${alias ?? target.trim().split("/").pop()}](</${target.trim()}${/\.md$/i.test(target.trim()) ? "" : ".md"}>)`);

// Markdown of the vault and of Azure DevOps, rendered with marked and sanitized with DOMPurify; raw text if the CDN
// is unreachable.
export const markdown = text => window.marked && window.DOMPurify
  ? DOMPurify.sanitize(marked.parse(wikilinks(text)), { ADD_ATTR: ["target"] })
  : `<pre>${esc(text)}</pre>`;
// HTML written in Azure DevOps (a PBI's description): sanitized, or its text alone if the CDN is unreachable. Its
// inline styles go too: colors pasted into Azure DevOps would fight the page's theme (black text in dark mode).
export const safeHtml = html => window.DOMPurify ? DOMPurify.sanitize(html, { FORBID_ATTR: ["style"] }) : esc(html.replace(/<[^>]+>/g, " "));
const section = (text, name) => {
  const match = text.match(new RegExp(`^##\\s+${name}\\s*$([\\s\\S]*?)(?=^##\\s|(?![\\s\\S]))`, "m"));
  return match ? match[1].trim() : "";
};
// Reports come in English, with a French translation under "fr" when the session wrote one: show it in French.
export const localized = report => report && lang === "fr" && report.fr ? report.fr : report;
// PBI working files (PBIs/pbi-<id>/00-status.md ... 07-pr-description.md, rule in the workspace CLAUDE.md). Each starts
// with a YAML frontmatter (updated, session, sessions, and step / next / blockedBy for the status): flat "key: value"
// lines are enough here. A "#" comment needs a space after it, so "#247129" stays in a value.
export const frontmatter = text => {
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/.exec(text ?? "");
  const fields = {};
  if (match) match[1].split(/\r?\n/).forEach(line => {
    const field = /^(\w+):\s*(.*?)(?:\s+#\s.*)?$/.exec(line);
    if (field) fields[field[1]] = field[2].trim().replace(/^(["'])(.*)\1$/, "$2");
  });
  return { fields, body: match ? text.slice(match[0].length) : text ?? "" };
};
export const pbiSteps = ["01-summary", "02-analysis", "03-job-done", "04-ai-review", "05-test-instructions", "06-open-questions", "07-pr-description"];

// The status file of a PBI in the page language, with its frontmatter fields (step, next, blockedBy) and its text.
export const statusOf = item => {
  const file = localized(item.files?.["00-status"]);
  return file ? { file, ...frontmatter(file.markdown) } : null;
};
