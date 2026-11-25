import { t } from "./texts.js";
import { cap, esc, latest } from "./util.js";

export const rank = { TODO: 0, WIP: 1, WAIT: 2, OK: 3 };

// What a pull request asks of me, as [status, hint].
export function prAction(pr) {
  if (pr.status === "completed") return ["OK", t("merged")];
  if (pr.status === "abandoned") return ["OK", t("abandoned")];
  if (pr.mergeStatus === "conflicts") return ["TODO", t("conflicts")];
  if (pr.isMine) {
    if (pr.isDraft) return ["TODO", t("draft")];
    if (pr.ci?.status === "failed") return ["TODO", t("ciFailed")];
    if (pr.ci?.status === "expired") return ["TODO", t("ciRequeue")];
    if (pr.openThreads > 0) return ["TODO", t("openThreads")(pr.openThreads)];
    if (pr.reviewers.some(r => r.vote < 0)) return ["TODO", t("reviewerWaiting")];
    const required = pr.reviewers.filter(r => r.isRequired && !r.isMe);
    const approved = (required.length ? required : pr.reviewers.filter(r => !r.isMe)).every(r => r.vote >= 5);
    if (approved && pr.reviewers.length)
      return pr.ci?.status === "running" ? ["WAIT", t("approvedCiRunning")] : ["TODO", t("readyToMerge")];
    return ["WAIT", t("waitingReviewers")];
  }
  const me = pr.reviewers.find(r => r.isMe);
  if (me?.vote > 0) return ["OK", t("approvedByMe")];
  // Not approved yet: to review until I comment or vote, then waiting for the author, and back to me as soon as
  // the author pushes or answers after my last action.
  const after = date => date && new Date(date) > new Date(pr.myLastAction);
  if (!pr.myLastAction) return me?.vote < 0 ? ["WAIT", t("waitingAuthor")] : ["TODO", t("toReview")];
  if (after(pr.lastPush)) return ["TODO", t("reviewAgainPush")];
  if (after(pr.authorLastComment)) return ["TODO", t("reviewAgainReply")];
  return ["WAIT", me?.vote < 0 ? t("waitingAuthor") : t("reviewed")];
}

// What a PBI asks of me, as [status, reason]: the most urgent of its active PRs, else its state.
export function itemAction(item) {
  const active = item.pullRequests.filter(pr => pr.status === "active").map(pr => [pr, ...prAction(pr)]);
  if (active.length) {
    const [pr, status, hint] = active.reduce((a, b) => (rank[b[1]] < rank[a[1]] ? b : a));
    return [status, `PR ${pr.id} : ${hint}`];
  }
  if (/hold|blocked/i.test(item.state)) return ["WAIT", t("reasonOnHold")(item.state)];
  if (item.pullRequests.length && /code review|testing/i.test(item.state)) return ["WAIT", t("reasonMerged")(item.state)];
  return ["WIP", t("reasonInProgress")];
}

// A pull request in one sentence: conflicts, draft, comment threads and the state of its Build policies (the CI
// links to the build). A thread counts once whatever its replies; system threads are left out by the server.
const threadsText = pr => pr.totalThreads == null ? "" : pr.totalThreads === 0 ? t("noThread")
  : pr.openThreads === 0 ? t("allResolved") : t("openOf")(pr.openThreads, pr.totalThreads);
const ciHtml = pr => {
  if (!pr.ci) return "";
  const text = esc(t("ci")[pr.ci.status] ?? `CI ${pr.ci.status}`);
  return pr.ci.buildUrl ? `<a href="${esc(pr.ci.buildUrl)}" target="_blank">${text}</a>` : text;
};
export const signals = pr => {
  const parts = [pr.mergeStatus === "conflicts" && t("conflictsBadge"), pr.isDraft && t("draftBadge"), threadsText(pr)].filter(Boolean);
  // The capital goes to the first word of the sentence, whichever part it is.
  return [...parts.map((text, index) => esc(index ? text : cap(text))), ciHtml(pr)].filter(Boolean).join(" · ");
};
export const votesText = pr => pr.reviewers.length
  ? pr.reviewers.map(r => r.isMe ? `<b>${esc(t("myVotes")[r.vote] ?? r.vote)}</b>` : `${esc(r.name)} ${esc(t("votes")[r.vote] ?? r.vote)}`).join(" · ")
  : esc(t("noReviewer"));

// When a PBI last moved: its change in Azure DevOps, its last session, its pull requests and its working files.
export const itemActivity = item => latest(item.changedDate, item.lastSession?.startedAt, ...item.pullRequests.map(pr => pr.lastActivity),
  ...Object.values(item.files ?? {}).map(file => file.updated));
