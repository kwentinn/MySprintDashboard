using System.Text.Json.Nodes;
using Xunit;

// What the live updates tell: the changes between two readings of Azure DevOps and the vault, and the notifications
// kept on disk. The notifications folder is static, so this class shares the collection of the vault tests.
[Collection("vault")]
public sealed class LiveTests : IDisposable
{
    static readonly DateTimeOffset At = new(2026, 11, 26, 10, 42, 0, TimeSpan.FromHours(1));
    readonly string folder = Path.Combine(Path.GetTempPath(), "dashboard-tests-" + Guid.NewGuid().ToString("N"));

    public LiveTests() => Live.Folder = Path.Combine(folder, "notifications");

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    // A reading of /api/today with one PBI of mine (1201, with my pull request 4215) and one pull request to review.
    static JsonObject Today(Action<JsonObject>? change = null)
    {
        var today = JsonNode.Parse("""
            {
              "workItems": [{
                "id": 1201, "title": "Invoice as a PDF", "state": "In Progress", "iteration": "Sprint 12", "changedDate": "2026-11-25T09:00:00Z",
                "files": { "03-job-done": { "file": "PBIs/pbi-1201/03-job-done.md", "updated": "2026-11-25T08:00:00+01:00", "markdown": "---\nupdated: 2026-11-25T08:00:00+01:00\n---\n\n# Job done\n" } },
                "pullRequests": [{
                  "id": 4215, "title": "Invoice PDF endpoint", "status": "active", "isMine": true, "openThreads": 1, "ci": { "status": "succeeded" },
                  "lastPush": "2026-11-25T08:30:00Z", "authorLastComment": null,
                  "reviewers": [{ "name": "Bob", "vote": 0, "isMe": false }, { "name": "Me", "vote": 0, "isMe": true }],
                  "review": null, "threadsReport": null
                }]
              }],
              "reviews": [{
                "id": 4190, "title": "Orders export endpoint", "status": "active", "isMine": false, "openThreads": 0, "ci": { "status": "running" },
                "lastPush": "2026-11-25T07:00:00Z", "authorLastComment": null,
                "reviewers": [{ "name": "Me", "vote": 0, "isMe": true }],
                "workItems": [{ "id": 1187 }], "review": null, "threadsReport": null
              }]
            }
            """)!.AsObject();
        change?.Invoke(today);
        return today;
    }

    static JsonArray Questions(string state = "waiting", int answers = 0) => new(new JsonObject
    {
        ["file"] = "PBIs/pbi-1201/questions/q03-refunded-order.md", ["title"] = "Q3 - Refunded order", ["pbi"] = 1201, ["state"] = state,
        ["answers"] = new JsonArray([.. Enumerable.Range(0, answers).Select(index => (JsonNode)new JsonObject { ["author"] = $"Eve {index + 1}" })]),
    });

    static List<JsonObject> Changes(Action<JsonObject> change, JsonArray? questions = null) =>
        Live.Changes(Live.Snapshot(Today(), Questions()), Live.Snapshot(Today(change), questions ?? Questions()), At);

    static JsonNode Item(JsonObject today) => today["workItems"]![0]!;
    static JsonNode MyPr(JsonObject today) => Item(today)["pullRequests"]![0]!;
    static JsonNode Review(JsonObject today) => today["reviews"]![0]!;

    static string Line(JsonObject change) => string.Join(" ", new[] { "source", "kind", "pbi", "pr", "from", "to", "who", "vote", "count", "open", "file", "state" }
        .Where(name => change[name] is not null).Select(name => $"{name}={change[name]}"));

    [Fact]
    public void Nothing_changed_tells_nothing() => Assert.Empty(Changes(_ => { }));

    [Fact]
    public void A_pbi_tells_its_state_its_sprint_or_that_it_changed()
    {
        Assert.Equal(["source=ado kind=state pbi=1201 from=In Progress to=Code review"],
            Changes(today => { Item(today)["state"] = "Code review"; Item(today)["changedDate"] = "2026-11-26T09:41:00Z"; }).Select(Line));
        Assert.Equal(["source=ado kind=sprint pbi=1201 from=Sprint 12 to=Sprint 13"], Changes(today => Item(today)["iteration"] = "Sprint 13").Select(Line));
        // Azure DevOps only says that it changed: a comment, the description, a link.
        var changed = Assert.Single(Changes(today => Item(today)["changedDate"] = "2026-11-26T09:41:00Z"));
        Assert.Equal("source=ado kind=itemChanged pbi=1201", Line(changed));
        Assert.Equal("Invoice as a PDF", (string?)changed["title"]);
        Assert.Equal(At.ToString("O"), (string?)changed["at"]);
    }

    [Fact]
    public void A_pbi_that_arrives_comes_alone_and_one_that_leaves_says_so()
    {
        // Its pull request and its files arrive with it: one notification, not three.
        var before = Live.Snapshot(Today(today => today["workItems"]!.AsArray().Clear()), []);
        Assert.Equal(["source=ado kind=itemNew pbi=1201"], Live.Changes(before, Live.Snapshot(Today(), []), At).Select(Line));
        Assert.Equal(["source=ado kind=itemGone pbi=1201"], Changes(today => today["workItems"]!.AsArray().Clear(), Questions()).Select(Line).Where(line => line.Contains("item")));
    }

    [Fact]
    public void My_pull_request_tells_votes_threads_ci_and_its_end()
    {
        Assert.Equal(["source=ado kind=vote pbi=1201 pr=4215 who=Bob vote=10"], Changes(today => MyPr(today)["reviewers"]![0]!["vote"] = 10).Select(Line));
        // My own vote and my own pushes are not news to me.
        Assert.Empty(Changes(today => { MyPr(today)["reviewers"]![1]!["vote"] = 10; MyPr(today)["lastPush"] = "2026-11-26T09:00:00Z"; MyPr(today)["authorLastComment"] = "2026-11-26T09:00:00Z"; }));
        Assert.Equal(["source=ado kind=threadsOpened pbi=1201 pr=4215 count=2 open=3"], Changes(today => MyPr(today)["openThreads"] = 3).Select(Line));
        Assert.Equal(["source=ado kind=threadsResolved pbi=1201 pr=4215 count=1 open=0"], Changes(today => MyPr(today)["openThreads"] = 0).Select(Line));
        // Threads that could not be read (null) are not a change.
        Assert.Empty(Changes(today => MyPr(today)["openThreads"] = null));
        Assert.Equal(["source=ado kind=ci pbi=1201 pr=4215 to=failed"], Changes(today => MyPr(today)["ci"]!["status"] = "failed").Select(Line));
        Assert.Empty(Changes(today => MyPr(today)["ci"]!["status"] = "running"));
        Assert.Equal(["source=ado kind=prStatus pbi=1201 pr=4215 to=completed"], Changes(today => MyPr(today)["status"] = "completed").Select(Line));
    }

    [Fact]
    public void Two_reviewers_with_the_same_first_name_keep_their_own_vote()
    {
        static JsonNode Twins(int first, int second) => new JsonArray(
            new JsonObject { ["id"] = "a1", ["name"] = "Bob", ["vote"] = first, ["isMe"] = false },
            new JsonObject { ["id"] = "b2", ["name"] = "Bob", ["vote"] = second, ["isMe"] = false });
        var before = Live.Snapshot(Today(today => MyPr(today)["reviewers"] = Twins(0, 10)), []);
        var after = Live.Snapshot(Today(today => MyPr(today)["reviewers"] = Twins(5, 10)), []);

        Assert.Equal(["source=ado kind=vote pbi=1201 pr=4215 who=Bob vote=5"], Live.Changes(before, after, At).Select(Line));
    }

    [Fact]
    public void A_snapshot_of_an_earlier_version_still_compares_its_votes()
    {
        var before = Live.Snapshot(Today(), []);
        before["pullRequests"]!["4215"]!["votes"] = new JsonObject { ["Bob"] = 10 };
        var after = Live.Snapshot(Today(today => MyPr(today)["reviewers"]![0]!["vote"] = 10), []);

        Assert.Empty(Live.Changes(before, after, At));
    }

    [Fact]
    public void Each_notification_has_its_own_id()
    {
        var changes = Changes(today => { Item(today)["state"] = "Done"; MyPr(today)["openThreads"] = 3; });

        Assert.Equal(2, changes.Select(change => (string?)change["id"]).Where(id => id?.Length == 32).Distinct().Count());
    }

    [Fact]
    public void A_pull_request_to_review_tells_what_its_author_did()
    {
        Assert.Equal(["source=ado kind=push pbi=1187 pr=4190"], Changes(today => Review(today)["lastPush"] = "2026-11-26T09:00:00Z").Select(Line));
        Assert.Equal(["source=ado kind=authorReply pbi=1187 pr=4190"], Changes(today => Review(today)["authorLastComment"] = "2026-11-26T09:00:00Z").Select(Line));
        // The list only holds the active ones: one that left it was merged or abandoned.
        Assert.Equal(["source=ado kind=prClosed pbi=1187 pr=4190"], Changes(today => today["reviews"]!.AsArray().Clear()).Select(Line));
        var before = Live.Snapshot(Today(today => today["reviews"]!.AsArray().Clear()), []);
        Assert.Equal(["source=ado kind=prToReview pbi=1187 pr=4190"], Live.Changes(before, Live.Snapshot(Today(), []), At).Select(Line));
    }

    [Fact]
    public void The_vault_tells_a_file_written_and_what_happens_to_a_question()
    {
        // The date the file gives itself counts, not the date of the file on disk.
        Assert.Empty(Changes(today => Item(today)["files"]!["03-job-done"]!["updated"] = "2026-11-26T10:00:00+01:00"));
        Assert.Equal(["source=vault kind=file pbi=1201 file=PBIs/pbi-1201/03-job-done.md"],
            Changes(today => Item(today)["files"]!["03-job-done"]!["markdown"] = "---\nupdated: 2026-11-26T10:00:00+01:00\n---\n").Select(Line));
        Assert.Equal(["source=vault kind=file pbi=1201 pr=4215 file=Reviews/4215-threads.md"],
            Changes(today => MyPr(today)["threadsReport"] = new JsonObject { ["file"] = "Reviews/4215-threads.md", ["updated"] = "2026-11-26T10:00:00+01:00", ["markdown"] = "# Threads" }).Select(Line));

        Assert.Equal(["source=vault kind=answer pbi=1201 who=Eve 1 file=PBIs/pbi-1201/questions/q03-refunded-order.md state=resolved"], Changes(_ => { }, Questions("resolved", 1)).Select(Line));
        Assert.Equal(["source=vault kind=questionState pbi=1201 to=dropped file=PBIs/pbi-1201/questions/q03-refunded-order.md"], Changes(_ => { }, Questions("dropped")).Select(Line));
        Assert.Equal(["source=vault kind=questionNew pbi=1201 file=PBIs/pbi-1201/questions/q03-refunded-order.md"],
            Live.Changes(Live.Snapshot(Today(), []), Live.Snapshot(Today(), Questions()), At).Select(Line));
    }

    [Fact]
    public void Notifications_are_kept_by_day_newest_first_with_the_time_they_were_read_up_to()
    {
        var today = new DateOnly(2026, 11, 26);
        Assert.Empty(Live.Notifications(today)["notifications"]!.AsArray());
        Assert.Null(Live.Notifications(today)["readUntil"]);

        Live.AppendNotifications([new JsonObject { ["kind"] = "monday" }], At.AddDays(-3));
        Live.AppendNotifications([new JsonObject { ["kind"] = "first" }, new JsonObject { ["kind"] = "second" }], At);
        Live.AppendNotifications([new JsonObject { ["kind"] = "third" }], At.AddMinutes(1));
        // A write that was cut leaves a line that is not JSON: the others still read.
        File.AppendAllText(Path.Combine(Live.Folder, "2026-11-26.jsonl"), "{\"kind\":\"cu");
        Live.AppendNotifications([new JsonObject { ["kind"] = "too old" }], At.AddDays(-7));

        Assert.Equal(["third", "second", "first", "monday"], Live.Notifications(today)["notifications"]!.AsArray().Select(notification => (string?)notification!["kind"]));
        Assert.Equal(Live.MarkRead(At), (string?)Live.Notifications(today)["readUntil"]);
    }

    [Fact]
    public void Purge_removes_the_days_older_than_a_week_and_nothing_else()
    {
        Directory.CreateDirectory(Live.Folder);
        foreach (var name in new[] { "2026-11-26.jsonl", "2026-11-20.jsonl", "2026-11-19.jsonl", "2026-10-01.jsonl", "read.json", "snapshot.json", "notes.jsonl" })
            File.WriteAllText(Path.Combine(Live.Folder, name), "{}");

        Live.Purge(new DateOnly(2026, 11, 26));

        Assert.Equal(["2026-11-20.jsonl", "2026-11-26.jsonl", "notes.jsonl", "read.json", "snapshot.json"],
            Directory.GetFiles(Live.Folder).Select(Path.GetFileName).Order());
    }
}
