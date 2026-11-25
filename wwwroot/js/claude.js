import { hooks } from "./state.js";
import { lang, t } from "./texts.js";
import { esc } from "./util.js";

// Button that opens a Claude session in a new Windows Terminal tab (POST /api/sessions/<kind>/<id>).
export const claudeButton = (kind, id, label, title, cls = "") =>
  `<button class="${cls}" data-claude="${kind}/${id}" data-label="${label}"${title ? ` title="${esc(title)}"` : ""}>${esc(t(label))}</button>`;
// Shown only when the pull request has unresolved threads.
export const threadsButton = (pr, cls) => pr.openThreads > 0
  && claudeButton("threads", pr.id, "claudeThreads", t("threadsTitle")(pr.openThreads, pr.totalThreads - pr.openThreads), cls);
// Resume the last review or thread analysis of a pull request (Reviews/sessions.jsonl), next to the button that starts a new one.
const sessionDate = session => new Date(session.startedAt).toLocaleString(t("locale"), { dateStyle: "short", timeStyle: "short" });
export const resumeReviewButton = (pr, cls) => pr.reviewSession
  && claudeButton("review-resume", pr.id, "claudeResumeReview", t("claudeResumeTitle")(sessionDate(pr.reviewSession)), cls);
export const resumeThreadsButton = (pr, cls) => pr.threadsSession
  && claudeButton("threads-resume", pr.id, "claudeResumeThreads", t("claudeResumeTitle")(sessionDate(pr.threadsSession)), cls);
// The main action of a PBI: resume its last session, else start one (not once it is Implemented).
export const implemented = item => /^implemented$/i.test(item.state);
export const pbiPrimary = (item, cls) => item.lastSession
  ? claudeButton("resume", item.id, "claudeResume", t("claudeResumeTitle")(sessionDate(item.lastSession)), cls)
  : implemented(item) ? "" : claudeButton("pbi", item.id, "claudePbi", "", cls);
export const docLink = (key, text) => `<button class="link" data-doc="${key}">${esc(text)}</button>`;

// Returns the JSON the server sent back, if any (the reviewer outcome of a typed PR).
export async function openClaude(button, target = button.dataset.claude, query = button.dataset.query ?? "") {
  button.disabled = true;
  let ok = false, result = null;
  try {
    // The page language is the language Claude answers in, and adds a French copy of the report.
    const response = await fetch(`/api/sessions/${target}?lang=${lang}${query}`, { method: "POST", headers: { "X-Dashboard": "1" } });
    ok = response.ok;
    if (response.headers.get("content-type")?.includes("json")) result = await response.json();
  } catch { }
  button.textContent = t(ok ? "claudeOpened" : "claudeFailed");
  button.classList.add(ok ? "done" : "failed");
  setTimeout(() => {
    button.disabled = false;
    button.classList.remove("done", "failed");
    button.textContent = t(button.dataset.label);
    // A new PBI session is recorded on the server: refresh so its card gets the Resume button.
    if (ok && target.startsWith("pbi/")) hooks.load();
  }, 4000);
  return result;
}
