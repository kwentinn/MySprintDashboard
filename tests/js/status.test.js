// What a pull request and a PBI ask of me (status.js) and the reading of a vault file's frontmatter (util.js): the
// functions of the page's script that hold rules and need no page. Run with `node --test tests/js/`.
import { test } from "node:test";
import assert from "node:assert/strict";

// texts.js reads the language of the page when it loads: an English page, without a browser.
globalThis.location = { search: "?lang=en" };
globalThis.navigator ??= { language: "en" };
const { prAction, itemAction } = await import("../../wwwroot/js/status.js");
const { frontmatter, ago, latest } = await import("../../wwwroot/js/util.js");

const reviewer = (vote, extra = {}) => ({ name: "Bob", vote, isMe: false, isRequired: true, ...extra });
const me = vote => reviewer(vote, { name: "Me", isMe: true });
const pr = fields => ({ id: 1, status: "active", isMine: false, reviewers: [], openThreads: 0, ...fields });

test("a closed pull request asks nothing", () => {
  assert.deepEqual(prAction(pr({ status: "completed" })), ["OK", "merged"]);
  assert.deepEqual(prAction(pr({ status: "abandoned" })), ["OK", "abandoned"]);
});

test("conflicts come first, mine or not", () => {
  assert.deepEqual(prAction(pr({ mergeStatus: "conflicts", isMine: true, isDraft: true })), ["TODO", "conflicts to resolve"]);
  assert.deepEqual(prAction(pr({ mergeStatus: "conflicts", reviewers: [me(10)] })), ["TODO", "conflicts to resolve"]);
});

test("my pull request: what blocks it, in order", () => {
  const mine = fields => prAction(pr({ isMine: true, reviewers: [reviewer(0)], ...fields }));
  assert.deepEqual(mine({ isDraft: true, ci: { status: "failed" } }), ["TODO", "draft to publish"]);
  assert.deepEqual(mine({ ci: { status: "failed" }, openThreads: 2 }), ["TODO", "CI failed"]);
  assert.deepEqual(mine({ ci: { status: "expired" } }), ["TODO", "CI expired, queue it again"]);
  assert.deepEqual(mine({ openThreads: 2 }), ["TODO", "2 open threads"]);
  assert.deepEqual(mine({ openThreads: 1 }), ["TODO", "1 open thread"]);
  assert.deepEqual(mine({ reviewers: [reviewer(-5)] }), ["TODO", "a reviewer is waiting for you"]);
  assert.deepEqual(mine({}), ["WAIT", "waiting for reviewers"]);
});

test("my pull request: approved by the required reviewers", () => {
  const approved = fields => prAction(pr({ isMine: true, reviewers: [reviewer(10), reviewer(0, { isRequired: false })], ...fields }));
  assert.deepEqual(approved({}), ["TODO", "approved, ready to merge"]);
  assert.deepEqual(approved({ ci: { status: "running" } }), ["WAIT", "approved, CI running"]);
  // Without a required reviewer, every other reviewer must have approved.
  assert.deepEqual(prAction(pr({ isMine: true, reviewers: [reviewer(10, { isRequired: false }), reviewer(0, { isRequired: false })] })),
    ["WAIT", "waiting for reviewers"]);
});

test("a pull request to review: to review, reviewed, to review again", () => {
  const review = fields => prAction(pr({ reviewers: [me(0)], ...fields }));
  assert.deepEqual(review({}), ["TODO", "to review"]);
  assert.deepEqual(prAction(pr({ reviewers: [me(10)] })), ["OK", "approved by me"]);
  assert.deepEqual(prAction(pr({ reviewers: [me(5)] })), ["OK", "approved by me"]);
  // I acted (a comment or a vote): it waits for the author until the author pushes or answers after that.
  const acted = "2026-11-20T10:00:00Z";
  assert.deepEqual(review({ myLastAction: acted }), ["WAIT", "reviewed, waiting for the author"]);
  assert.deepEqual(review({ myLastAction: acted, lastPush: "2026-11-20T09:00:00Z", authorLastComment: "2026-11-19T10:00:00Z" }),
    ["WAIT", "reviewed, waiting for the author"]);
  assert.deepEqual(review({ myLastAction: acted, lastPush: "2026-11-20T11:00:00Z" }), ["TODO", "to review again: new commits"]);
  assert.deepEqual(review({ myLastAction: acted, authorLastComment: "2026-11-20T11:00:00Z" }), ["TODO", "to review again: the author answered"]);
  // "Wait for the author" is my vote: it reads as such, with or without a dated action.
  assert.deepEqual(prAction(pr({ reviewers: [me(-5)] })), ["WAIT", "waiting for the author"]);
  assert.deepEqual(prAction(pr({ reviewers: [me(-5)], myLastAction: acted })), ["WAIT", "waiting for the author"]);
});

test("a PBI asks what its most urgent active pull request asks", () => {
  const item = fields => ({ id: 7, state: "In Progress", pullRequests: [], ...fields });
  assert.deepEqual(itemAction(item({ pullRequests: [
    pr({ id: 10, isMine: true, reviewers: [reviewer(0)] }),               // waiting for reviewers
    pr({ id: 11, isMine: true, reviewers: [reviewer(0)], openThreads: 1 }), // to do
    pr({ id: 12, status: "completed" }),
  ] })), ["TODO", "PR 11 : 1 open thread"]);
  assert.deepEqual(itemAction(item({})), ["WIP", "no active PR"]);
  assert.deepEqual(itemAction(item({ state: "On Hold" })), ["WAIT", "PBI is On Hold"]);
  assert.deepEqual(itemAction(item({ state: "Code review", pullRequests: [pr({ status: "completed" })] })), ["WAIT", "PR merged, PBI is Code review"]);
});

test("frontmatter: flat fields, comments after a spaced #, the body", () => {
  const { fields, body } = frontmatter("---\nupdated: 2026-11-17T12:50:00+01:00\nstep: 03-job-done   # the last step\nnext: \"Ask #247129's owner\"\nblockedBy: ''\nlink: https://example.test/edit/1#42\n---\n\n# Title\n\nText.\n");
  assert.deepEqual(fields, { updated: "2026-11-17T12:50:00+01:00", step: "03-job-done", next: "Ask #247129's owner", blockedBy: "", link: "https://example.test/edit/1#42" });
  assert.equal(body, "\n# Title\n\nText.\n");
  assert.deepEqual(frontmatter("No frontmatter."), { fields: {}, body: "No frontmatter." });
  assert.deepEqual(frontmatter(undefined), { fields: {}, body: "" });
});

test("ago and latest", () => {
  const hours = count => new Date(Date.now() - count * 36e5).toISOString();
  assert.equal(ago(hours(0.5)), "< 1 h");
  assert.equal(ago(hours(5)), "5 h");
  assert.equal(ago(hours(72)), "3 d");
  assert.equal(ago(null), "");
  assert.equal(latest("2026-11-20T10:00:00Z", null, "2026-11-21T08:00:00+01:00", undefined), "2026-11-21T07:00:00.000Z");
  assert.equal(latest(null, undefined), null);
});
