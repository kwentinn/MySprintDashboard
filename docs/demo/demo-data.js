// Fictional data for the README screenshots (see make-screenshots.ps1). Loaded before the page's script, it answers
// the page's fetch calls and its EventSource with made-up JSON, so that the page runs with no server behind it.
// Organization contoso, project Shop, colleagues Alice, Bob, Carol, Dan and Eve; me is Frank. PBIs 11xx-12xx, pull
// requests 41xx-42xx. Nothing here comes from a real project. Times are relative to now, so that ages read the same
// whenever the screenshots are taken. `?demo=notifications` opens the notifications panel, `?demo=summary` writes
// the AI summary of the daily.
(() => {
  const ago = minutes => new Date(Date.now() - minutes * 60000).toISOString();
  const org = "https://dev.azure.com/contoso/Shop";
  const workItemUrl = id => `${org}/_workitems/edit/${id}`;
  const prUrl = (repo, id) => `${org}/_git/${repo}/pullrequest/${id}`;
  const sprint = { path: "Shop\\Sprint 12", name: "Sprint 12" };

  // ---------------------------------------------------------------- vault files
  const file = (path, minutes, fields, body, fr) => {
    const head = Object.entries({ updated: ago(minutes), session: "0b065092-c6f8-402d-87c9-f8d88b1aa541", sessions: "[0b065092-c6f8-402d-87c9-f8d88b1aa541]", ...fields })
      .map(([key, value]) => `${key}: ${value}`).join("\n");
    const report = { file: path, updated: ago(minutes), markdown: `---\n${head}\n---\n\n${body}\n` };
    if (fr) report.fr = { file: path.replace(/\.md$/, ".fr.md"), updated: ago(minutes), markdown: `---\n${head}\n---\n\n${fr}\n` };
    return report;
  };
  const status = (id, minutes, step, next, blockedBy, text) =>
    file(`PBIs/pbi-${id}/00-status.md`, minutes, { step, next, blockedBy: blockedBy ? `"${blockedBy}"` : '""' }, text);

  const summary1201 = file("PBIs/pbi-1201/01-summary.md", 4300, {}, `# Summary

## Goal
Let a customer download the invoice of a paid order as a PDF, from the order history.

## Acceptance criteria
1. \`GET /orders/{id}/invoice\` returns a PDF for an order the customer owns, 404 otherwise.
2. An order that is not yet paid has no invoice: 409.
3. The file is named \`invoice-{orderNumber}.pdf\` and weighs less than 200 KB for a ten-line order.

## Out of scope
Credit notes, invoices sent by e-mail, the back-office bulk export.`);
  const analysis1201 = file("PBIs/pbi-1201/02-analysis.md", 4100, {}, `# Analysis

1. Add \`GET /orders/{id}/invoice\` to \`OrdersController\`, with the ownership check of the order history.
2. Build the PDF in a new \`InvoiceRenderer\`, fed by the totals the confirmation e-mail already computes.
3. Cover 404, 409 and the file size with integration tests.

| Option | Pro | Con |
| :----- | :-- | :-- |
| Render with the existing PDF library | no new dependency | the font is embedded in full |
| A separate rendering service | isolated | one more service to run |

The first option, then subset the font.`);
  const jobDone1201 = file("PBIs/pbi-1201/03-job-done.md", 190, {}, `# Job done

Branch \`feature/1201-invoice-pdf\`, two commits, PR 4215 to \`develop\`.

- \`GET /orders/{id}/invoice\` in \`OrdersController\`: 404 for an order of another customer, 409 while the order is unpaid.
- \`InvoiceRenderer\` builds the PDF from the totals of the confirmation e-mail, so both always show the same numbers.
- 18 integration tests, all green.

## Decisions taken while coding

- The whole font is embedded for now: a ten-line invoice weighs 400 KB, above the 200 KB of the acceptance criteria. Bob raised it on the PR.
- The file name uses the order number, not the technical id.

## Left to do

Subset the font to the characters used, then ask Dan for a review.`, `# Travail fait

Branche \`feature/1201-invoice-pdf\`, deux commits, PR 4215 vers \`develop\`.

- \`GET /orders/{id}/invoice\` dans \`OrdersController\` : 404 pour la commande d'un autre client, 409 tant que la commande n'est pas payée.
- \`InvoiceRenderer\` construit le PDF à partir des totaux de l'e-mail de confirmation, les deux affichent donc toujours les mêmes montants.
- 18 tests d'intégration, tous verts.`);
  const index1201 = file("PBIs/pbi-1201/06-open-questions.md", 7 * 1440, {}, `# 1201 - open questions

Asked to the PO while the invoice rules were written.

## Questions

- [[PBIs/pbi-1201/questions/q01-invoice-language|Q1 - Which language for the invoice?]]
- [[PBIs/pbi-1201/questions/q02-tax-line-label|Q2 - Which label for the tax line of a mixed-rate order?]]
- [[PBIs/pbi-1201/questions/q03-refunded-order|Q3 - Should a refunded order still offer its invoice?]]`);

  // ---------------------------------------------------------------- pull requests
  const reviewer = (name, vote, extra = {}) => ({ name, vote, isRequired: true, isMe: false, ...extra });
  const me = vote => reviewer("Frank", vote, { isMe: true, isRequired: false });
  const pr = fields => ({
    status: "active", isDraft: false, mergeStatus: "succeeded", targetBranch: "develop", isMine: false, closedDate: null, myLastAction: null, lastPush: null,
    authorLastComment: null, openThreads: 0, totalThreads: 0, threads: [], ci: { status: "succeeded" }, reviewers: [], review: null, threadsReport: null,
    reviewSession: null, threadsSession: null, workItems: [], description: "", ...fields,
    url: prUrl(fields.repository, fields.id),
  });
  const bobThread = {
    file: "src/Mail.Service/FontSubsetter.cs", line: 57, url: `${org}/_git/Mail.Service/pullrequest/4204?discussionId=1`,
    comments: [
      { author: "Frank", date: ago(30 * 60), content: "The glyph set is built from the whole template: could it come from the rendered text instead? A subset of the characters used is a lot smaller." },
      { author: "Carol", date: ago(26 * 60), content: "Good point, I will compute it from the text of the attachment and keep the euro sign always." },
    ],
  };
  const prs = {
    4220: pr({ id: 4220, title: "Orders: paginate the order history endpoint", repository: "Orders.Api", author: "Alice", createdDate: ago(190), lastActivity: ago(40),
      reviewers: [reviewer("Dan", 0), me(0)], workItems: [{ id: 1219, state: "In Progress", backlogOrder: 6, iteration: "Sprint 12", url: workItemUrl(1219) }],
      description: "Adds `page` and `pageSize` to `GET /orders`, with a default of 25 and a maximum of 100.\n\n- keeps the order of the history, newest first\n- `Link` header for the next page" }),
    4211: pr({ id: 4211, title: "Orders: answer 409 for the invoice of an unpaid order", repository: "Orders.Api", author: "Bob", createdDate: ago(2 * 1440), lastActivity: ago(70),
      myLastAction: ago(30 * 60), lastPush: ago(75), openThreads: 1, totalThreads: 3, reviewers: [reviewer("Dan", 5), me(0)],
      workItems: [{ id: 1201, state: "Code review", backlogOrder: 2, iteration: "Sprint 12", url: workItemUrl(1201) }] }),
    4204: pr({ id: 4204, title: "Mail: subset the font in PDF attachments", repository: "Mail.Service", author: "Carol", createdDate: ago(30 * 60), lastActivity: ago(26 * 60),
      myLastAction: ago(20 * 60), lastPush: ago(2 * 1440), authorLastComment: ago(26 * 60), openThreads: 2, totalThreads: 5, reviewers: [reviewer("Bob", 5), me(0)],
      threads: [bobThread, { file: "tests/FontSubsetterTests.cs", line: 12, url: `${org}/_git/Mail.Service/pullrequest/4204?discussionId=2`,
        comments: [{ author: "Frank", date: ago(30 * 60), content: "Add a test with a character outside the Latin range, the subset must not drop it." }] }],
      reviewSession: { startedAt: ago(2 * 1440), sessionId: "r1" }, threadsSession: { startedAt: ago(20 * 60), sessionId: "t1" },
      workItems: [{ id: 1160, state: "Done", backlogOrder: 11, iteration: "Sprint 11", url: workItemUrl(1160) }],
      description: "The attachments embed the whole font, so a PDF weighs 400 KB.\n\nThis keeps **only the glyphs used** by the text, and always the euro sign." }),
    4198: pr({ id: 4198, title: "Shop.Web: lazy-load the order images", repository: "Shop.Web", author: "Dan", createdDate: ago(4 * 1440), lastActivity: ago(3 * 1440),
      myLastAction: ago(3 * 1440), reviewers: [reviewer("Bob", 10), me(10)], totalThreads: 2,
      review: file("Reviews/4198-review.md", 3 * 1440, {}, "# PR 4198 - lazy-load the order images\n\nApproved. One suggestion on the placeholder size, not blocking.") }),
    4215: pr({ id: 4215, title: "Invoice PDF endpoint", repository: "Orders.Api", author: "Frank", isMine: true, createdDate: ago(1440 + 60), lastActivity: ago(50),
      openThreads: 1, totalThreads: 4, reviewers: [reviewer("Bob", 0), reviewer("Dan", 0), me(0)], lastPush: ago(190),
      threads: [{ file: "src/Orders.Api/InvoiceRenderer.cs", line: 42, url: `${org}/_git/Orders.Api/pullrequest/4215?discussionId=1`,
        comments: [{ author: "Bob", date: ago(6 * 60), content: "Embedding the whole font makes each PDF 400 KB; could we subset it? The acceptance criteria ask for less than 200 KB." },
          { author: "Dan", date: ago(4 * 60), content: "Agreed, the e-mail attachments already do it with `FontSubsetter`." }] }] }),
    4190: pr({ id: 4190, title: "Orders export endpoint", repository: "Orders.Api", author: "Frank", isMine: true, createdDate: ago(3 * 1440), lastActivity: ago(2 * 1440),
      reviewers: [reviewer("Bob", 0), reviewer("Dan", 0), me(0)] }),
  };

  // ---------------------------------------------------------------- my PBIs
  const item = fields => ({
    type: "Product Backlog Item", iterationPath: sprint.path, iteration: "Sprint 12", tags: "", description: "", acceptanceCriteria: "", sessions: [], history: [],
    pullRequests: [], files: {}, lastSession: null, ...fields, url: workItemUrl(fields.id),
  });
  const workItems = [
    item({ id: 1201, title: "Customer can download an invoice as a PDF", state: "Code review", backlogOrder: 2, tags: "invoice; pdf", changedDate: ago(190),
      description: "<p>As a customer, I want to download the invoice of a past order as a PDF from my order history, so that I can hand it to my accounting department without asking support.</p><p>The PDF shows the seller and buyer addresses, the order lines with their unit price, the discounts, the tax per rate and the total. It uses the same numbers as the order confirmation e-mail.</p>",
      acceptanceCriteria: "<p>1. GET /orders/{id}/invoice returns a PDF for an order the customer owns, 404 otherwise.</p><p>2. An order that is not yet paid has no invoice: the endpoint answers 409.</p><p>3. The file is named invoice-{orderNumber}.pdf and weighs less than 200 KB for a ten-line order.</p>",
      lastSession: { startedAt: ago(3 * 1440), sessionId: "s1", kind: "work" }, sessions: [{ startedAt: ago(3 * 1440), sessionId: "s1", kind: "work" }],
      history: [{ type: "created", date: ago(9 * 1440), to: "New", by: "Eve" }, { type: "sprint", date: ago(7 * 1440), from: "Sprint 11", to: "Sprint 12", by: "Eve" },
        { type: "assigned", date: ago(6 * 1440), to: "Frank", by: "Eve" }, { type: "state", date: ago(5 * 1440), from: "New", to: "In Progress", by: "Frank" },
        { type: "state", date: ago(190), from: "In Progress", to: "Code review", by: "Frank" }],
      pullRequests: [prs[4215]],
      files: { "00-status": status(1201, 190, "03-job-done", "Answer Bob on the font size, then ask Dan for a review", "", "The font is embedded in full, so a PDF weighs 400 KB, above the 200 KB of the criteria. Subsetting it is the one thing left before the review."),
        "01-summary": summary1201, "02-analysis": analysis1201, "03-job-done": jobDone1201, "06-open-questions": index1201 } }),
    item({ id: 1187, title: "Back-office export of orders", state: "In Progress", backlogOrder: 5, changedDate: ago(2 * 1440),
      lastSession: { startedAt: ago(2 * 1440), sessionId: "s2", kind: "work" }, pullRequests: [prs[4190]],
      files: { "00-status": status(1187, 2 * 1440, "03-job-done", "Wait for Dan's answer on the 10 000 rows cap", "", "The export streams the orders as CSV. The row cap waits for an answer.") } }),
    item({ id: 1210, title: "Credit note number on a refunded invoice", state: "In Progress", backlogOrder: 8, changedDate: ago(26 * 60),
      lastSession: { startedAt: ago(26 * 60), sessionId: "s3", kind: "work" },
      files: { "00-status": status(1210, 26 * 60, "02-analysis", "Write the analysis once Eve has answered", "Eve's answer on the numbering (Q1)", "Waiting for the numbering rule before anything is written.") } }),
  ];
  const reviews = [prs[4220], prs[4211], prs[4204], prs[4198]];
  const today = () => ({ generatedAt: new Date().toISOString(), currentSprint: sprint, workItems, reviews });

  // ---------------------------------------------------------------- questions
  const question = (n, slug, fields) => ({
    file: `PBIs/pbi-1201/questions/q0${n}-${slug}.md`, name: `q0${n}-${slug}`, pbi: 1201, to: null, channel: null, asked: null, link: null, follows: null,
    updated: ago(60), notes: "", answers: [], ...fields,
  });
  const questions = [
    question(3, "refunded-order", { title: "Q3 - Should a refunded order still offer its invoice?", state: "waiting", to: "Eve (PO)", channel: "ado", asked: ago(7 * 1440 + 90),
      link: `${org}/_workitems/edit/1201#21829996`, follows: "q01-invoice-language",
      question: "A refunded order keeps its invoice in accounting, but the customer may read it as still due.\n\nOptions: (a) keep the download, (b) hide it once the credit note exists. My preference: (a). Default applied while unanswered: (a).",
      answers: [{ date: ago(6 * 1440 + 20 * 60), author: "Eve", source: "teams", link: "https://teams.microsoft.com/l/message/19:demo@thread.v2/1", text: "I need to check with legal, I come back to you this week." }] }),
    question(2, "tax-line-label", { title: "Q2 - Which label for the tax line of a mixed-rate order?", state: "draft",
      question: "The confirmation e-mail prints one line per rate. Should the PDF do the same, or a single VAT total? Default applied while unanswered: one line per rate." }),
    question(1, "invoice-language", { title: "Q1 - Which language for the invoice: the shop's or the customer's?", state: "resolved", to: "Eve (PO)", channel: "teams", asked: ago(10 * 1440),
      question: "The invoice is generated at download time.",
      answers: [{ date: ago(9 * 1440 + 60), author: "Eve", source: "teams", link: "https://teams.microsoft.com/l/message/19:demo@thread.v2/2", text: "The customer's language, like the confirmation e-mail." }] }),
    { file: "Questions/q-2026-11-18-invoice-numbering.md", name: "q-2026-11-18-invoice-numbering", pbi: null, title: "Who owns the invoice numbering sequence after the migration?",
      state: "waiting", to: "Bob, Dan", channel: "email", asked: ago(9 * 1440), link: null, follows: null, updated: ago(9 * 1440), notes: "",
      question: "Both services write invoice numbers today.", answers: [] },
    { file: "PBIs/pbi-1187/questions/q02-row-cap.md", name: "q02-row-cap", pbi: 1187, title: "Q2 - Is the export allowed to stream more than 10 000 rows?",
      state: "waiting", to: "Dan", channel: "teams", asked: ago(150), link: null, follows: null, updated: ago(150), notes: "", question: "The streaming export has no cap yet.", answers: [] },
  ];

  // ---------------------------------------------------------------- notifications
  const note = (minutes, fields) => ({ id: Math.random().toString(16).slice(2), at: ago(minutes), source: "ado", pbi: null, pr: null, title: null, ...fields });
  const notifications = [
    note(8, { kind: "state", pbi: 1201, title: "Customer can download an invoice as a PDF", from: "In Progress", to: "Code review" }),
    note(8, { kind: "threadsOpened", pbi: 1187, pr: 4190, title: "Orders export endpoint", count: 1, open: 1 }),
    note(8, { kind: "push", pbi: 1201, pr: 4211, title: "Orders: answer 409 for the invoice of an unpaid order" }),
    note(55, { source: "vault", kind: "answer", pbi: 1201, title: "Q3 - Should a refunded order still offer its invoice?", who: "Eve", state: "waiting", file: "PBIs/pbi-1201/questions/q03-refunded-order.md" }),
    note(190, { kind: "vote", pbi: 1201, pr: 4215, title: "Invoice PDF endpoint", who: "Bob", vote: 10 }),
    note(26 * 60, { source: "vault", kind: "file", pbi: 1210, file: "PBIs/pbi-1210/00-status.md" }),
  ];

  // ---------------------------------------------------------------- the daily
  const previousWorkingDay = () => {
    const day = new Date();
    do day.setDate(day.getDate() - 1); while (day.getDay() === 0 || day.getDay() === 6);
    return day;
  };
  const isoDay = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
  const daily = () => {
    const day = previousWorkingDay();
    const at = (hour, minute) => new Date(day.getFullYear(), day.getMonth(), day.getDate(), hour, minute).toISOString();
    return { day: isoDay(day), groups: [
      { type: "pbi", id: 1201, title: "Customer can download an invoice as a PDF", url: workItemUrl(1201), iteration: "Sprint 12", backlogOrder: 2, events: [
        { time: at(9, 12), kind: "state", text: "New → In Progress" }, { time: at(10, 40), kind: "commit", text: "Render the invoice with the totals of the confirmation e-mail" },
        { time: at(16, 12), kind: "file", text: "PBIs/pbi-1201/03-job-done.md" }] },
      { type: "pr", id: 4215, title: "Invoice PDF endpoint", url: prUrl("Orders.Api", 4215), author: "Frank", isMine: true, workItems: [{ id: 1201, backlogOrder: 2, iteration: "Sprint 12" }],
        events: [{ time: at(16, 5), kind: "created", text: "Invoice PDF endpoint" }, { time: at(17, 20), kind: "reply", text: "Agreed, subsetting the font next" }] },
      { type: "pr", id: 4204, title: "Mail: subset the font in PDF attachments", url: prUrl("Mail.Service", 4204), author: "Carol", isMine: false,
        workItems: [{ id: 1160, backlogOrder: 11, iteration: "Sprint 11" }], events: [
          { time: at(11, 30), kind: "thread", text: "The glyph set is built from the whole template: could it come from the rendered text instead?" },
          { time: at(11, 31), kind: "vote", text: "-5" }, { time: at(14, 2), kind: "report", text: "Reviews/4204-threads.md" }] },
      { type: "pbi", id: 1187, title: "Back-office export of orders", url: workItemUrl(1187), iteration: "Sprint 12", backlogOrder: 5, events: [
        { time: at(15, 45), kind: "comment", text: "Waiting for Dan on the 10 000 rows cap" }] },
    ] };
  };

  // ---------------------------------------------------------------- the search
  const searchResults = words => ({
    words, workItems: [
      { id: 1201, title: "Customer can download an invoice as a PDF", type: "Product Backlog Item", state: "Code review", assignedTo: "Frank", iteration: "Sprint 12", url: workItemUrl(1201), where: "title", excerpt: null },
      { id: 1164, title: "Order confirmation e-mail with totals per tax rate", type: "Product Backlog Item", state: "Done", assignedTo: "Dan", iteration: "Sprint 11", url: workItemUrl(1164),
        where: "description", excerpt: "…the same numbers must feed the invoice PDF later, so the totals are computed once in OrderTotals…" },
      { id: 1233, title: "Invoice PDF shows the wrong currency on a foreign order", type: "Bug", state: "New", assignedTo: null, iteration: "Sprint 12", url: workItemUrl(1233), where: "title", excerpt: null },
    ],
    pullRequests: [
      { id: 4215, title: "Invoice PDF endpoint", repository: "Orders.Api", targetBranch: "develop", status: "active", author: "Frank", isMine: true, createdDate: ago(1440 + 60), url: prUrl("Orders.Api", 4215), where: "title", excerpt: null },
      { id: 4230, title: "Reports: export the invoice totals as CSV", repository: "Orders.Api", targetBranch: "develop", status: "active", author: "Alice", isMine: false, createdDate: ago(5 * 60), url: prUrl("Orders.Api", 4230), where: "title", excerpt: null },
      { id: 4102, title: "Font subsetting for PDF attachments", repository: "Mail.Service", targetBranch: "develop", status: "completed", author: "Dan", isMine: false, createdDate: ago(20 * 1440), url: prUrl("Mail.Service", 4102),
        where: "description", excerpt: "…FontSubsetter keeps an invoice-sized PDF under 80 KB…" },
    ],
    vault: [
      { file: "Knowledge/PDF rendering.md", title: "PDF rendering", folder: "Knowledge", pbi: null, updated: ago(6 * 1440), count: 5, question: null,
        passages: ["…an invoice PDF must embed a subset of the font, never the whole file: FontSubsetter, as the mail attachments do…", "…the totals of the invoice come from OrderTotals, the same source as the confirmation e-mail…"] },
      { file: "PBIs/pbi-1201/03-job-done.md", title: "Job done", folder: "PBIs", pbi: 1201, updated: ago(190), count: 4, question: null,
        passages: ["…InvoiceRenderer builds the PDF from the totals of the confirmation e-mail, so both always show the same numbers…", "…a ten-line invoice weighs 400 KB, above the 200 KB of the acceptance criteria…"] },
      { file: "PBIs/pbi-1201/questions/q03-refunded-order.md", title: "Q3 - Should a refunded order still offer its invoice?", folder: "PBIs", pbi: 1201, updated: ago(55), count: 2,
        question: { state: "waiting", to: "Eve (PO)" }, passages: ["…A refunded order keeps its invoice in accounting, but the customer may read it as still due…"] },
      { file: "Reviews/4102-review.md", title: "PR 4102 - Font subsetting for PDF attachments", folder: "Reviews", pbi: null, updated: ago(21 * 1440), count: 2, question: null,
        passages: ["…the subsetter drops the euro sign when no invoice line uses it; add it to the always-kept glyphs…"] },
    ],
  });

  // ---------------------------------------------------------------- the stubs
  const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
  window.fetch = async (input, options = {}) => {
    const url = new URL(String(input), location.href);
    const method = (options.method ?? "GET").toUpperCase();
    if (method === "POST") {
      if (url.pathname.includes("daily-summary")) return json({ summary: [
        "- 2 #1201 Invoice PDF: endpoint written and PR 4215 opened, the font subset is the last thing before the review.",
        "- 5 #1187 Export of orders: waiting for Dan on the 10 000 rows cap.",
        "- Carol's PR 4204 reviewed: asked to build the glyph set from the rendered text, and to test a character outside the Latin range."].join("\n") });
      if (url.pathname.startsWith("/api/notifications/read")) return json({ readUntil: new Date().toISOString() });
      return new Response(null, { status: 204 });
    }
    switch (url.pathname) {
      case "/api/today": return json(today());
      case "/api/daily": return json(daily());
      case "/api/questions": return json(questions);
      case "/api/notifications": return json({ readUntil: null, notifications });
      case "/api/search": return json(searchResults((url.searchParams.get("q") ?? "").toLowerCase().split(/\s+/).filter(Boolean)));
      default: return json({ detail: "No such demo route" }, 404);
    }
  };
  window.EventSource = class {
    constructor() {
      setTimeout(() => {
        this.onopen?.();
        this.onmessage?.({ data: JSON.stringify({ type: "hello", checkedAt: ago(2) }) });
      }, 30);
    }
    close() { }
  };

  // ---------------------------------------------------------------- what a screenshot asks the page to show
  const demo = new URLSearchParams(location.search).get("demo");
  if (demo) addEventListener("load", () => setTimeout(() => {
    if (demo === "notifications") document.getElementById("bell").click();
    if (demo === "summary") document.getElementById("dailySummaryButton").click();
  }, 1500));
})();
