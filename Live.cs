using System.Threading.Channels;

// Live updates. The server reads Azure DevOps and the vault again every minute while a page is open, tells what
// changed since the last time (notifications), keeps the result for the page and pushes it through server-sent
// events (GET /api/events). Notifications are kept seven days, one .jsonl file a day, in the notifications folder
// next to the application.
public static class Live
{
    static readonly TimeSpan Period = TimeSpan.FromMinutes(1);
    const int DaysKept = 7;

    // Where the notifications are kept; set once by Program.
    public static string Folder { get; set; } = "";

    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly List<Channel<string>> Pages = [];
    static string? todayJson, todayWithoutDate;
    static DateTimeOffset checkedAt;
    static JsonObject? snapshot;

    // ------------------------------------------------------------ the data of the page, read again when it is old

    // The answer of /api/today: the last one when it is under a minute old, unless `refresh`.
    public static async Task<string> TodayAsync(bool refresh) =>
        !refresh && todayJson is not null && DateTimeOffset.Now - checkedAt < Period ? todayJson : await RefreshAsync();

    // Reads everything again, records what changed since the last time and tells the pages. One at a time.
    public static async Task<string> RefreshAsync()
    {
        await Gate.WaitAsync();
        try
        {
            // Another caller refreshed while this one was waiting.
            if (todayJson is not null && DateTimeOffset.Now - checkedAt < TimeSpan.FromSeconds(5)) return todayJson;
            var today = await Ado.TodayAsync();
            var now = DateTimeOffset.Now;
            var current = Snapshot(today, Questions());
            snapshot ??= LoadSnapshot();
            var changes = snapshot is null ? [] : Changes(snapshot, current, now);
            if (changes.Count > 0) AppendNotifications(changes, now);
            SaveSnapshot(current);
            snapshot = current;
            Purge(DateOnly.FromDateTime(now.Date));

            // The page draws again only when its data changed, whatever the time it was read at.
            var generatedAt = today["generatedAt"]?.DeepClone();
            today.Remove("generatedAt");
            var withoutDate = today.ToJsonString();
            today["generatedAt"] = generatedAt;
            var changed = withoutDate != todayWithoutDate || changes.Count > 0;
            (todayJson, todayWithoutDate, checkedAt) = (today.ToJsonString(), withoutDate, now);
            Tell(new JsonObject
            {
                ["type"] = "update", ["checkedAt"] = now.ToString("O"), ["changed"] = changed,
                ["notifications"] = new JsonArray([.. changes.Select(change => (JsonNode)change)]),
            });
            return todayJson;
        }
        catch (Exception exception)
        {
            Tell(new JsonObject { ["type"] = "error", ["detail"] = exception.Message });
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    // Every minute, while a page listens: nobody to tell, nothing to ask Azure DevOps.
    public static async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Period, stopping);
                if (Listeners == 0) continue;
                try { await RefreshAsync(); }
                catch (Exception exception) { Console.Error.WriteLine($"Live: {exception.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------ the pages that listen (server-sent events)

    public static int Listeners { get { lock (Pages) return Pages.Count; } }

    // The messages for one page, until it leaves. The first one says when the data was last read.
    public static async IAsyncEnumerable<string> ListenAsync([EnumeratorCancellation] CancellationToken leaving)
    {
        var channel = Channel.CreateUnbounded<string>();
        lock (Pages) Pages.Add(channel);
        try
        {
            yield return new JsonObject { ["type"] = "hello", ["checkedAt"] = todayJson is null ? null : checkedAt.ToString("O") }.ToJsonString();
            await foreach (var message in channel.Reader.ReadAllAsync(leaving)) yield return message;
        }
        finally
        {
            lock (Pages) Pages.Remove(channel);
        }
    }

    static void Tell(JsonObject message)
    {
        var text = message.ToJsonString();
        lock (Pages) foreach (var page in Pages) page.Writer.TryWrite(text);
    }

    // ------------------------------------------------------------ what changed

    // What is compared from one reading to the next: my PBIs, their pull requests and the ones I review, the files of
    // the vault the page shows, and the questions. Small, so that it can be kept on disk between two runs.
    public static JsonObject Snapshot(JsonObject today, JsonArray questions)
    {
        var items = new JsonObject();
        var pullRequests = new JsonObject();
        var files = new JsonObject();

        void Track(JsonNode? report, int? pbi, int? pr)
        {
            if ((string?)report?["file"] is not { } file) return;
            // The date the file gives itself: the date of the file on disk also moves when links are rewritten.
            var updated = Regex.Match((string?)report["markdown"] ?? "", @"\A---\r?\n(?:.*\r?\n)*?updated:[ \t]*(\S+)") is { Success: true } match
                ? match.Groups[1].Value : (string?)report["updated"];
            files[file] = new JsonObject { ["updated"] = updated, ["pbi"] = pbi, ["pr"] = pr };
        }
        void PullRequest(JsonNode pr, int? pbi)
        {
            var votes = new JsonObject();
            foreach (var reviewer in pr["reviewers"]?.AsArray() ?? [])
                if ((bool?)reviewer!["isMe"] != true && (string?)reviewer["name"] is { } name) votes[name] = (int?)reviewer["vote"];
            pullRequests[$"{pr["id"]}"] = new JsonObject
            {
                ["title"] = (string?)pr["title"], ["status"] = (string?)pr["status"], ["mine"] = (bool?)pr["isMine"] ?? false, ["pbi"] = pbi,
                ["votes"] = votes, ["open"] = (int?)pr["openThreads"], ["ci"] = (string?)pr["ci"]?["status"],
                ["lastPush"] = (string?)pr["lastPush"], ["authorLastComment"] = (string?)pr["authorLastComment"],
            };
            Track(pr["review"], pbi, (int)pr["id"]!);
            Track(pr["threadsReport"], pbi, (int)pr["id"]!);
        }

        foreach (var item in today["workItems"]?.AsArray() ?? [])
        {
            var id = (int)item!["id"]!;
            items[$"{id}"] = new JsonObject
            {
                ["title"] = (string?)item["title"], ["state"] = (string?)item["state"], ["iteration"] = (string?)item["iteration"],
                ["changedDate"] = (string?)item["changedDate"],
            };
            foreach (var report in item["files"]?.AsObject() ?? []) Track(report.Value, id, null);
            foreach (var pr in item["pullRequests"]?.AsArray() ?? []) PullRequest(pr!, id);
        }
        foreach (var pr in today["reviews"]?.AsArray() ?? [])
            PullRequest(pr!, (int?)pr!["workItems"]?.AsArray().FirstOrDefault()?["id"]);

        var asked = new JsonObject();
        foreach (var question in questions)
        {
            var answers = question!["answers"]!.AsArray();
            asked[(string)question["file"]!] = new JsonObject
            {
                ["title"] = (string?)question["title"], ["pbi"] = (int?)question["pbi"], ["state"] = (string?)question["state"],
                ["answers"] = answers.Count, ["lastAuthor"] = (string?)answers.LastOrDefault()?["author"],
            };
        }
        return new JsonObject { ["items"] = items, ["pullRequests"] = pullRequests, ["files"] = files, ["questions"] = asked };
    }

    // The notifications for what differs between two snapshots, each {at, source, kind, pbi, pr, title, ...}: the
    // page writes the sentence from the kind and its values, in its language.
    public static List<JsonObject> Changes(JsonObject before, JsonObject after, DateTimeOffset at)
    {
        var changes = new List<JsonObject>();
        JsonObject Notify(string source, string kind, int? pbi, int? pr, string? title)
        {
            var change = new JsonObject { ["at"] = at.ToString("O"), ["source"] = source, ["kind"] = kind, ["pbi"] = pbi, ["pr"] = pr, ["title"] = title };
            changes.Add(change);
            return change;
        }
        static JsonObject Part(JsonObject snapshot, string name) => snapshot[name]?.AsObject() ?? [];
        static string? Text(JsonNode? node, string name) => (string?)node?[name];

        // My PBIs: one that appears or leaves my open ones, its state, its sprint, or any other change.
        var (oldItems, newItems) = (Part(before, "items"), Part(after, "items"));
        foreach (var (key, item) in newItems)
        {
            var id = int.Parse(key);
            if (oldItems[key] is not { } old) { Notify("ado", "itemNew", id, null, Text(item, "title")); continue; }
            var count = changes.Count;
            if (Text(old, "state") != Text(item, "state"))
                Notify("ado", "state", id, null, Text(item, "title")).With("from", Text(old, "state")).With("to", Text(item, "state"));
            if (Text(old, "iteration") != Text(item, "iteration"))
                Notify("ado", "sprint", id, null, Text(item, "title")).With("from", Text(old, "iteration")).With("to", Text(item, "iteration"));
            // Azure DevOps says it changed, not what: a comment, the description, a link.
            if (changes.Count == count && Text(old, "changedDate") != Text(item, "changedDate")) Notify("ado", "itemChanged", id, null, Text(item, "title"));
        }
        foreach (var (key, old) in oldItems)
            if (!newItems.ContainsKey(key)) Notify("ado", "itemGone", int.Parse(key), null, Text(old, "title"));

        // Pull requests, mine and the ones I review: votes, threads, CI, and what the author did on one I review.
        var (oldPrs, newPrs) = (Part(before, "pullRequests"), Part(after, "pullRequests"));
        foreach (var (key, pr) in newPrs)
        {
            var id = int.Parse(key);
            var (pbi, title, mine) = ((int?)pr!["pbi"], Text(pr, "title"), (bool?)pr["mine"] == true);
            if (oldPrs[key] is not { } old)
            {
                // A pull request of a PBI that just appeared comes with it; a new one to review is news.
                if (pbi is null || oldItems.ContainsKey($"{pbi}") || !mine) Notify("ado", mine ? "prCreated" : "prToReview", pbi, id, title);
                continue;
            }
            if (Text(old, "status") != Text(pr, "status")) Notify("ado", "prStatus", pbi, id, title).With("to", Text(pr, "status"));
            foreach (var (who, vote) in pr["votes"]?.AsObject() ?? [])
                if ((int?)vote is { } value && value != 0 && (int?)old["votes"]?[who] != value) Notify("ado", "vote", pbi, id, title).With("who", who).With("vote", value);
            if ((int?)old["open"] is { } openBefore && (int?)pr["open"] is { } open && open != openBefore)
                Notify("ado", open > openBefore ? "threadsOpened" : "threadsResolved", pbi, id, title).With("count", Math.Abs(open - openBefore)).With("open", open);
            if (Text(pr, "ci") is { } ci and not "running" && Text(old, "ci") != ci) Notify("ado", "ci", pbi, id, title).With("to", ci);
            if (!mine && Text(pr, "lastPush") is { } push && Text(old, "lastPush") != push) Notify("ado", "push", pbi, id, title);
            if (!mine && Text(pr, "authorLastComment") is { } reply && Text(old, "authorLastComment") != reply) Notify("ado", "authorReply", pbi, id, title);
        }
        // A pull request I reviewed that is no longer listed was closed: the list only holds the active ones.
        foreach (var (key, old) in oldPrs)
            if (!newPrs.ContainsKey(key) && (bool?)old!["mine"] != true && Text(old, "status") == "active")
                Notify("ado", "prClosed", (int?)old["pbi"], int.Parse(key), Text(old, "title"));

        // The vault: a working file or a report written, a question asked, answered or closed.
        var oldFiles = Part(before, "files");
        foreach (var (file, entry) in Part(after, "files"))
            if (Text(oldFiles[file], "updated") != Text(entry, "updated") && (oldFiles[file] is not null || oldItems.ContainsKey($"{entry!["pbi"]}") || entry!["pr"] is not null && oldPrs.ContainsKey($"{entry["pr"]}")))
                Notify("vault", "file", (int?)entry!["pbi"], (int?)entry["pr"], null).With("file", file);
        var oldQuestions = Part(before, "questions");
        foreach (var (file, question) in Part(after, "questions"))
        {
            var (pbi, title) = ((int?)question!["pbi"], Text(question, "title"));
            if (oldQuestions[file] is not { } old) { Notify("vault", "questionNew", pbi, null, title).With("file", file); continue; }
            if ((int?)question["answers"] > (int?)old["answers"])
                Notify("vault", "answer", pbi, null, title).With("file", file).With("who", Text(question, "lastAuthor")).With("state", Text(question, "state"));
            else if (Text(old, "state") != Text(question, "state"))
                Notify("vault", "questionState", pbi, null, title).With("file", file).With("to", Text(question, "state"));
        }
        return changes;
    }

    static JsonObject With(this JsonObject change, string name, JsonNode? value)
    {
        change[name] = value;
        return change;
    }

    // ------------------------------------------------------------ the notifications on disk

    static string DayFile(DateOnly day) => Path.Combine(Folder, $"{day:yyyy-MM-dd}.jsonl");
    static string ReadFile => Path.Combine(Folder, "read.json");
    static string SnapshotFile => Path.Combine(Folder, "snapshot.json");

    public static void AppendNotifications(List<JsonObject> changes, DateTimeOffset at)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(DayFile(DateOnly.FromDateTime(at.Date)), string.Concat(changes.Select(change => change.ToJsonString() + "\n")));
    }

    // {readUntil, notifications}: the notifications of the last seven days, the newest first, and the time up to
    // which they were read. A line that is not JSON (a write that was cut) is left out.
    public static JsonObject Notifications(DateOnly today)
    {
        var notifications = new JsonArray();
        for (var day = today; day > today.AddDays(-DaysKept); day = day.AddDays(-1))
        {
            if (!File.Exists(DayFile(day))) continue;
            foreach (var line in File.ReadAllLines(DayFile(day)).Reverse())
            {
                try { if (JsonNode.Parse(line) is JsonObject notification) notifications.Add((JsonNode)notification); }
                catch (System.Text.Json.JsonException) { }
            }
        }
        string? readUntil = null;
        try { readUntil = File.Exists(ReadFile) ? (string?)JsonNode.Parse(File.ReadAllText(ReadFile))?["until"] : null; }
        catch (System.Text.Json.JsonException) { }
        return new JsonObject { ["readUntil"] = readUntil, ["notifications"] = notifications };
    }

    // Everything up to now is read.
    public static string MarkRead(DateTimeOffset now)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(ReadFile, new JsonObject { ["until"] = now.ToString("O") }.ToJsonString());
        return now.ToString("O");
    }

    // Removes the day files older than seven days: only files this class names, by their date.
    static DateOnly purged;
    public static void Purge(DateOnly today)
    {
        if (purged == today || !Directory.Exists(Folder)) return;
        purged = today;
        foreach (var file in Directory.GetFiles(Folder, "????-??-??.jsonl"))
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out var day) && day <= today.AddDays(-DaysKept))
                File.Delete(file);
    }

    // The last snapshot, kept so that what changed while the application was stopped is told at the next start.
    static JsonObject? LoadSnapshot()
    {
        try { return File.Exists(SnapshotFile) ? JsonNode.Parse(File.ReadAllText(SnapshotFile)) as JsonObject : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    static void SaveSnapshot(JsonObject current)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SnapshotFile, current.ToJsonString());
    }
}
