#:sdk Microsoft.NET.Sdk.Web

// Morning dashboard: my open PBIs with their pull requests, and the pull requests waiting for my review, with
// my review notes from the vault (Reviews/<pr>-review.md) when there are some.
// Single-file .NET program (needs the .NET 10 SDK).
//
//   dotnet run dashboard.cs        then open http://localhost:5099 (polls every 10 minutes; ?refresh=60 sets it in seconds)
//
// The organization, project, team, vault and PAT variable come from dashboard.settings.json next to this file (see
// dashboard.settings.example.json); the PAT itself is read from that environment variable and only put in the
// Authorization header. The page is index.html next to this file, read on every request so it can be edited without
// a restart. Buttons on the cards open a Claude session (review a pull request, start a PBI) in a new Windows
// Terminal tab.

using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Prompts;
using static Settings;

const string ApiVersion = "api-version=7.1";
// `-- --port 5100` runs a second instance next to the usual one.
var portIndex = Array.IndexOf(args, "--port");
var Url = $"http://localhost:{(portIndex >= 0 && portIndex + 1 < args.Length ? int.Parse(args[portIndex + 1]) : 5099)}";

// A published executable with index.html next to it is on its own: it reads the page and the settings from its own
// folder and opens the browser when it starts. Otherwise (dotnet run) they come from the folder of this file.
var standalone = File.Exists(Path.Combine(AppContext.BaseDirectory, "index.html"));
var home = standalone ? AppContext.BaseDirectory : SourceDirectory();

if (Settings.Load(Path.Combine(home, "dashboard.settings.json")) is { } settingsError)
{
    Console.Error.WriteLine(settingsError);
    return 1;
}
var pat = Environment.GetEnvironmentVariable(PatVariable);
if (string.IsNullOrEmpty(pat))
{
    Console.Error.WriteLine($"{PatVariable} is not set.");
    return 1;
}

using var http = new HttpClient { BaseAddress = new Uri(Organization + "/") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
string? myId = null;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Url);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var page = Path.Combine(home, "index.html");
app.MapGet("/", () => Results.Content(File.ReadAllText(page), "text/html; charset=utf-8"));
app.MapGet("/api/today", async () =>
{
    try
    {
        return Results.Content((await TodayAsync()).ToJsonString(), "application/json");
    }
    catch (Exception exception)
    {
        // File-based apps compile for AOT, so no reflection-based serializer: write the JSON by hand.
        Console.Error.WriteLine(exception.Message);
        return Results.Content(new JsonObject { ["detail"] = exception.Message }.ToJsonString(), "application/json", statusCode: 500);
    }
});
// A short AI summary of a day's activity for the daily meeting, written by Claude Code in print mode (claude -p) from
// the same data as /api/daily, with no tool, no MCP server and no saved session. POST, same guard as the sessions.
app.MapPost("/api/daily-summary", async (HttpRequest request) =>
{
    if (request.Headers.Origin != Url || request.Headers["X-Dashboard"] != "1") return Results.StatusCode(403);
    if (!DateOnly.TryParse(request.Query["day"], out var day)) return Results.BadRequest();
    try
    {
        var summary = await DailySummaryAsync(await DailyAsync(day), request.Query["lang"] == "fr" ? "French" : "English");
        return Results.Content(new JsonObject { ["summary"] = summary }.ToJsonString(), "application/json");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Daily summary: {exception.Message}");
        return Results.Content(new JsonObject { ["detail"] = exception.Message }.ToJsonString(), "application/json", statusCode: 500);
    }
});
// What I did on the previous working day (Friday on a Monday), or on ?day=yyyy-MM-dd, for the daily meeting.
app.MapGet("/api/daily", async (HttpRequest request) =>
{
    try
    {
        var day = DateOnly.TryParse(request.Query["day"], out var asked) ? asked : PreviousWorkingDay(DateOnly.FromDateTime(DateTime.Today));
        return Results.Content((await DailyAsync(day)).ToJsonString(), "application/json");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return Results.Content(new JsonObject { ["detail"] = exception.Message }.ToJsonString(), "application/json", statusCode: 500);
    }
});

// Opens an interactive Claude session in a new Windows Terminal tab, in the workspace, with a fixed prompt. Only the
// numeric id reaches the prompt (never a title read from Azure DevOps). Only the page itself may call it: any other
// site open in the browser could otherwise post to localhost, so the Origin must match and the custom header forces
// a CORS preflight that this server never answers.
// A PBI session (work or review) gets its id from here (claude --session-id) and is recorded in
// <vault>/PBIs/pbi-<id>/sessions.jsonl, so that "resume" can reopen the last one with claude --resume.
app.MapPost("/api/sessions/{kind}/{id:int}", async (HttpRequest request, string kind, int id) =>
{
    if (request.Headers.Origin != Url || request.Headers["X-Dashboard"] != "1") return Results.StatusCode(403);
    // Language of the page (?lang=), which Claude answers in; anything but French means English.
    var language = request.Query["lang"] == "fr" ? "fr" : "en";
    switch (kind)
    {
        case "review":
            // A PR typed in the page (?addReviewer=1) gets me as an optional reviewer first, so that it shows in my
            // code reviews. The review starts either way; the outcome goes back to the page.
            var reviewer = request.Query["addReviewer"] == "1" ? await AddMeAsOptionalReviewerAsync(id) : null;
            var prReviewSession = RecordReviewSession(id, "review");
            OpenClaudeTab($"Review PR {id}", $"--session-id {prReviewSession} {PowerShellQuote(ReviewPrompt(id, prReviewSession, language))}");
            return reviewer is null ? Results.NoContent() : Results.Content(reviewer.ToJsonString(), "application/json");
        case "threads":
            var threadsSession = RecordReviewSession(id, "threads");
            OpenClaudeTab($"Threads PR {id}", $"--session-id {threadsSession} {PowerShellQuote(ThreadsPrompt(id, threadsSession, language))}");
            return Results.NoContent();
        case "review-resume" or "threads-resume":
            var reviewKind = kind.Replace("-resume", "");
            if (LastReviewSession(id, reviewKind) is not { } lastReview) return Results.NotFound();
            OpenClaudeTab($"{(reviewKind == "review" ? "Review" : "Threads")} PR {id}", $"--resume {lastReview["sessionId"]}",
                (string?)lastReview["cwd"]);
            return Results.NoContent();
        // The French copy of a review or of a thread analysis that only exists in English.
        case "review-translate" or "threads-translate":
            var reportName = $"{id}-{kind.Replace("-translate", "")}";
            if (!File.Exists(Path.Combine(ReviewDirectory(), reportName + ".md"))) return Results.NotFound();
            var translateSession = RecordReviewSession(id, kind);
            OpenClaudeTab($"Translate {reportName}",
                $"--session-id {translateSession} {PowerShellQuote(TranslatePrompt(ReviewDirectory(), reportName, translateSession))}");
            return Results.NoContent();
        case "pbi-review":
            var reviewSession = RecordSession(id, "review");
            OpenClaudeTab($"Review PBI {id}", $"--session-id {reviewSession} {PowerShellQuote(PbiReviewPrompt(id, reviewSession, language))}");
            return Results.NoContent();
        case "pbi":
            var workSession = RecordSession(id, "work");
            OpenClaudeTab($"PBI {id}", $"--session-id {workSession} {PowerShellQuote(PbiPrompt(id, workSession, language))}");
            return Results.NoContent();
        // One step of a PBI (?step=03-job-done), from its tab in the PBI page: do it again, or write its French copy.
        case "step" or "translate":
            var step = (string?)request.Query["step"] ?? "";
            if (!StepTasks.ContainsKey(step)) return Results.BadRequest();
            if (kind == "translate" && !File.Exists(Path.Combine(PbiDirectory(id), step + ".md"))) return Results.NotFound();
            var stepSession = RecordSession(id, kind);
            OpenClaudeTab($"PBI {id} {step}", $"--session-id {stepSession} {PowerShellQuote(kind == "step"
                ? StepPrompt(id, step, stepSession, language) : TranslatePrompt(PbiDirectory(id), step, stepSession))}");
            return Results.NoContent();
        case "resume":
            if (LastSession(id) is not { } last) return Results.NotFound();
            OpenClaudeTab($"PBI {id}", $"--resume {last["sessionId"]}");
            return Results.NoContent();
        default:
            return Results.NotFound();
    }
});

// The questions asked to the PO or the team (rule "Questions" in the workspace CLAUDE.md): every question file of the
// vault, parsed. No Azure DevOps call, so the page reads them on their own.
app.MapGet("/api/questions", () => Results.Content(Questions().ToJsonString(), "application/json"));
// The forms of the page write the question files themselves: create a question, say it was asked (to whom, where),
// record an answer with its source and link, or change its state. Same guard as the sessions. Answers the new list.
app.MapPost("/api/questions/{action}", async (HttpRequest request, string action) =>
{
    if (request.Headers.Origin != Url || request.Headers["X-Dashboard"] != "1") return Results.StatusCode(403);
    try
    {
        using var reader = new StreamReader(request.Body);
        var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
        string Text(string name) => ((string?)body[name] ?? "").Trim();
        string Line(string name) => Regex.Replace(Text(name), @"\s+", " ");
        string Required(string name) => Line(name) is { Length: > 0 } value ? value : throw new ArgumentException($"{name} is missing.");
        string OneOf(string name, string[] values) => values.Contains(Line(name)) ? Line(name) : throw new ArgumentException($"{name} is not valid.");
        string Link() => Line("link") is var address && (address == "" || Regex.IsMatch(address, @"^https?://\S+$")) ? address : throw new ArgumentException("link is not an http(s) address.");
        // What "asked" takes: who, where, when and the link of the message.
        Dictionary<string, string> Asked() => new()
        {
            ["state"] = "waiting", ["to"] = Required("to"), ["channel"] = OneOf("channel", QuestionChannels),
            ["asked"] = QuestionDate(Line("asked")), ["link"] = Link(),
        };
        switch (action)
        {
            case "create":
                CreateQuestion((int?)body["pbi"], Required("title"), Text("question"), Line("follows"),
                    Line("state") == "waiting" ? Asked() : new() { ["state"] = "draft" });
                break;
            case "ask":
                UpdateQuestion(QuestionPath(Text("file")), Asked());
                break;
            case "answer":
                var heading = $"### {QuestionDate(Line("date"))} - {Required("author")} - {OneOf("source", [.. QuestionChannels, "decision"])}";
                Required("text");
                var answer = heading + "\n" + (Link() is { Length: > 0 } link ? $"Link: {link}\n" : "") + "\n" + NoHeading(Text("text"), 3);
                UpdateQuestion(QuestionPath(Text("file")), new() { ["state"] = OneOf("state", ["resolved", "waiting"]) }, rest => AddAnswer(rest, answer));
                break;
            case "state":
                UpdateQuestion(QuestionPath(Text("file")), new() { ["state"] = OneOf("state", ["draft", "waiting", "dropped"]) });
                break;
            default:
                return Results.NotFound();
        }
        return Results.Content(Questions().ToJsonString(), "application/json");
    }
    catch (Exception exception)
    {
        return Results.Content(new JsonObject { ["detail"] = exception.Message }.ToJsonString(), "application/json", statusCode: 400);
    }
});

Console.WriteLine($"Dashboard on {Url} (Ctrl+C to stop)");
if (standalone)
    app.Lifetime.ApplicationStarted.Register(() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true }));
await app.RunAsync();
return 0;

// ----------------------------------------------------------------------------------------------- data

// ------------------------------------------------------------ daily: what I did on a given day
// Events of that day, grouped by work item or pull request, from four sources: work item revisions and comments by me
// (ADO), my comments, votes, PRs created or completed (ADO), my commits in the workspace checkouts (git), and the vault
// files and Claude sessions of the dashboard. Every event is {time, kind, text}; a group is {type, id, title, url, events}.
static DateOnly PreviousWorkingDay(DateOnly today) => today.DayOfWeek switch
{
    DayOfWeek.Monday => today.AddDays(-3),
    DayOfWeek.Sunday => today.AddDays(-2),
    _ => today.AddDays(-1),
};

async Task<JsonObject> DailyAsync(DateOnly day)
{
    myId ??= (string)(await GetAsync("_apis/connectionData?api-version=7.1-preview"))["authenticatedUser"]!["id"]!;
    var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
    var to = from.AddDays(1);
    bool InDay(DateTimeOffset? at) => at is { } time && time >= from && time < to;

    // The sources run in parallel: every change to the groups goes through this lock.
    var groups = new Dictionary<string, JsonObject>();
    JsonArray Events(string type, int id, string? title = null, string? url = null)
    {
        lock (groups)
        {
            var key = $"{type}:{id}";
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new JsonObject { ["type"] = type, ["id"] = id, ["events"] = new JsonArray() };
            if (title is not null && group["title"] is null) group["title"] = title;
            if (url is not null && group["url"] is null) group["url"] = url;
            return group["events"]!.AsArray();
        }
    }
    void Add(string type, int id, DateTimeOffset at, string kind, string text)
    {
        lock (groups) Events(type, id).Add((JsonNode)new JsonObject { ["time"] = at.ToString("O"), ["kind"] = kind, ["text"] = text });
    }

    await Task.WhenAll(DailyWorkItemsAsync(day, InDay, Events, Add), DailyPullRequestsAsync(from, InDay, Events, Add));
    DailyCommits(day, Add);
    DailyVaultFiles(InDay, Add);

    // A group without a title is a work item only reached through a commit or a vault file: read its title.
    // Pull requests: title, author (a PR only reached through its review report has neither yet) and linked work items.
    var linked = new Dictionary<int, List<int>>();
    await Task.WhenAll(groups.Values.Where(group => (string)group["type"]! == "pr").ToList().Select(async group =>
    {
        var id = (int)group["id"]!;
        var pr = await GetAsync($"{Project}/_apis/git/pullrequests/{id}");
        Events("pr", id, (string?)pr["title"],
            $"{Organization}/{Project}/_git/{Uri.EscapeDataString((string)pr["repository"]!["name"]!)}/pullrequest/{id}");
        group["author"] = FirstName((string?)pr["createdBy"]?["displayName"]);
        group["isMine"] = (string?)pr["createdBy"]?["id"] == myId;
        var links = await GetAsync($"{Project}/_apis/git/repositories/{pr["repository"]!["id"]}/pullRequests/{id}/workitems");
        lock (linked) linked[id] = [.. links["value"]!.AsArray().Select(link => int.Parse((string)link!["id"]!))];
    }));

    // Work items of the day and those linked to its PRs: title, and the order in their sprint backlog, the number the
    // team uses at the daily.
    var pbiIds = groups.Values.Where(group => (string)group["type"]! == "pbi").Select(group => (int)group["id"]!)
        .Concat(linked.Values.SelectMany(ids => ids)).Distinct().ToList();
    var items = (await WorkItemsAsync(pbiIds)).ToDictionary(item => (int)item["id"]!);
    var order = await BacklogOrderAsync(items.Values.Select(item => (string?)item["fields"]!["System.IterationPath"]));
    string? Sprint(int id) => items.TryGetValue(id, out var item) ? ((string?)item["fields"]!["System.IterationPath"])?.Split('\\').Last() : null;
    foreach (var group in groups.Values.Where(group => (string)group["type"]! == "pbi").ToList())
    {
        var id = (int)group["id"]!;
        if (items.TryGetValue(id, out var item))
            Events("pbi", id, (string?)item["fields"]!["System.Title"], $"{Organization}/{Project}/_workitems/edit/{id}");
        group["iteration"] = Sprint(id);
        if (order.TryGetValue(id, out var position)) group["backlogOrder"] = position;
    }
    foreach (var (prId, ids) in linked)
        groups[$"pr:{prId}"]["workItems"] = new JsonArray([.. ids.Select(id => new JsonObject
        {
            ["id"] = id,
            ["iteration"] = Sprint(id),
            ["backlogOrder"] = order.TryGetValue(id, out var position) ? position : null,
        })]);

    // A revision of mine that only touched bookkeeping fields leaves an empty group: drop it.
    foreach (var empty in groups.Where(pair => pair.Value["events"]!.AsArray().Count == 0).Select(pair => pair.Key).ToList())
        groups.Remove(empty);
    foreach (var group in groups.Values)
    {
        var sorted = group["events"]!.AsArray().Select(e => e!.DeepClone()).OrderBy(e => (string?)e["time"]).ToList();
        group["events"] = new JsonArray([.. sorted]);
    }
    return new JsonObject
    {
        ["day"] = day.ToString("yyyy-MM-dd"),
        ["groups"] = new JsonArray([.. groups.Values.OrderByDescending(group => (string?)group["events"]!.AsArray().Last()!["time"])]),
    };
}

// The day's activity as plain lines, then claude -p asked for a few bullets I can say at the daily. The prompt goes
// through stdin, so nothing needs quoting; cmd.exe runs claude, which npm installs as a .cmd.
static async Task<string> DailySummaryAsync(JsonObject daily, string language)
{
    var activity = new StringBuilder();
    foreach (var group in daily["groups"]!.AsArray())
    {
        var order = (int?)group!["backlogOrder"] is { } own ? $"order {own} " : "";
        var linked = string.Join(", ", (group["workItems"]?.AsArray() ?? []).Select(w => $"{((int?)w!["backlogOrder"] is { } o ? $"order {o} " : "")}#{w["id"]}"));
        activity.AppendLine((string)group["type"]! == "pr"
            ? $"PR {group["id"]} \"{group["title"]}\" by {((bool?)group["isMine"] == true ? "me" : group["author"])}, linked to {(linked.Length > 0 ? linked : "no work item")}"
            : $"Work item {order}#{group["id"]} \"{group["title"]}\" ({group["iteration"]})");
        foreach (var e in group["events"]!.AsArray())
            activity.AppendLine($"  {((string)e!["time"]!)[11..16]} {e["kind"]}: {e["text"]}");
    }
    var prompt =
        $"You prepare my stand-up. Below is what I did on {daily["day"]}, from Azure DevOps, git and my notes. Write a very " +
        "short summary I can say at the daily: 3 to 6 bullets, one per task, merging a pull request into the work item it is " +
        "linked to. Start each bullet with the backlog order and the id, like \"5 #251726\" (the team names tasks by that " +
        "order), then what I did and where it stands, in a few words. A review of a colleague's PR is one bullet naming the " +
        $"colleague. No title, no preamble, no conclusion. Write in {language}.\n\n{activity}";

    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe",
        "/c claude -p --tools \"\" --strict-mcp-config --no-session-persistence --model sonnet")
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
    };
    using var claude = System.Diagnostics.Process.Start(start)!;
    await claude.StandardInput.WriteAsync(prompt);
    claude.StandardInput.Close();
    var output = claude.StandardOutput.ReadToEndAsync();
    var errors = claude.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await claude.WaitForExitAsync(timeout.Token);
    if (claude.ExitCode != 0) throw new InvalidOperationException($"claude -p exited with {claude.ExitCode}: {(await errors).Trim()}");
    return (await output).Trim();
}

// Work items I changed that day: state, sprint and assignment changes, other edited fields, and my comments.
async Task DailyWorkItemsAsync(DateOnly day, Func<DateTimeOffset?, bool> inDay,
    Func<string, int, string?, string?, JsonArray> events, Action<string, int, DateTimeOffset, string, string> add)
{
    var wiql = new JsonObject
    {
        ["query"] = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{Project}' " +
                    $"AND EVER [System.ChangedBy] = @Me AND [System.ChangedDate] >= '{day:yyyy-MM-dd}'",
    };
    var ids = (await PostAsync($"{Project}/_apis/wit/wiql", wiql))["workItems"]!.AsArray().Select(item => (int)item!["id"]!).ToList();
    var titles = (await WorkItemsAsync(ids)).ToDictionary(item => (int)item["id"]!, item => (string?)item["fields"]!["System.Title"]);
    await Task.WhenAll(ids.Select(async id =>
    {
        var updates = await GetAsync($"{Project}/_apis/wit/workitems/{id}/updates?$top=200");
        foreach (var update in updates["value"]!.AsArray())
        {
            var fields = update!["fields"]?.AsObject();
            var at = (DateTimeOffset?)fields?["System.ChangedDate"]?["newValue"];
            if (fields is null || !inDay(at) || (string?)update["revisedBy"]?["id"] != myId) continue;
            events("pbi", id, titles.GetValueOrDefault(id), $"{Organization}/{Project}/_workitems/edit/{id}");
            if (fields["System.State"] is { } state)
                add("pbi", id, at!.Value, "state", $"{state["oldValue"]} → {state["newValue"]}");
            if (fields["System.IterationPath"] is { } sprint && (int?)update["rev"] != 1)
                add("pbi", id, at!.Value, "sprint", $"{((string?)sprint["oldValue"])?.Split('\\').Last()} → {((string?)sprint["newValue"])?.Split('\\').Last()}");
            if (fields["System.AssignedTo"] is { } assigned)
                add("pbi", id, at!.Value, "assigned", FirstName((string?)assigned["newValue"]?["displayName"]) ?? "-");
            // Fields I edited by hand; dates, "by" fields and board bookkeeping follow a state change on their own.
            var edited = fields.Select(field => field.Key)
                .Where(name => !name.StartsWith("System.") || name is "System.Title" or "System.Description" or "System.Tags")
                .Where(name => !System.Text.RegularExpressions.Regex.IsMatch(name, "(Date|By|Board.*|Kanban.*|Column.*)$"))
                .Select(name => name.Split('.').Last()).ToList();
            if (edited.Count > 0) add("pbi", id, at!.Value, "edit", string.Join(", ", edited));
        }
        var comments = await GetAsync($"{Project}/_apis/wit/workItems/{id}/comments?api-version=7.1-preview.4");
        foreach (var comment in comments["comments"]?.AsArray() ?? [])
        {
            var at = (DateTimeOffset?)comment!["createdDate"];
            if (!inDay(at) || (string?)comment["createdBy"]?["id"] != myId) continue;
            // "Mentioned in !57352" is written by Azure DevOps when a PR comment names the work item, not by me.
            if (((string?)comment["text"] ?? "").StartsWith("Mentioned in ")) continue;
            events("pbi", id, titles.GetValueOrDefault(id), $"{Organization}/{Project}/_workitems/edit/{id}");
            add("pbi", id, at!.Value, "comment", Excerpt((string?)comment["text"]));
        }
    }));
}

// Pull requests I review or wrote, active or closed since that day: my comments and votes, PRs I created or completed.
async Task DailyPullRequestsAsync(DateTimeOffset from, Func<DateTimeOffset?, bool> inDay,
    Func<string, int, string?, string?, JsonArray> events, Action<string, int, DateTimeOffset, string, string> add)
{
    var lists = await Task.WhenAll(new[] { "reviewerId", "creatorId" }.Select(role =>
        GetAsync($"{Project}/_apis/git/pullrequests?searchCriteria.{role}={myId}&searchCriteria.status=all&$top=50")));
    var candidates = lists.SelectMany(list => list["value"]!.AsArray())
        .Where(pr => (string?)pr!["status"] == "active" || (DateTimeOffset?)pr["closedDate"] >= from)
        .GroupBy(pr => (int)pr!["pullRequestId"]!).Select(group => group.First()!);
    await Task.WhenAll(candidates.Select(async pr =>
    {
        var id = (int)pr["pullRequestId"]!;
        var repository = (string)pr["repository"]!["name"]!;
        var url = $"{Organization}/{Project}/_git/{Uri.EscapeDataString(repository)}/pullrequest/{id}";
        void On(DateTimeOffset at, string kind, string text)
        {
            events("pr", id, (string?)pr["title"], url);
            add("pr", id, at, kind, text);
        }
        if ((string?)pr["createdBy"]?["id"] == myId && inDay((DateTimeOffset?)pr["creationDate"]))
            On((DateTimeOffset)pr["creationDate"]!, "created", $"{repository} → {((string?)pr["targetRefName"])?.Replace("refs/heads/", "")}");
        if ((string?)pr["closedBy"]?["id"] == myId && inDay((DateTimeOffset?)pr["closedDate"]))
            On((DateTimeOffset)pr["closedDate"]!, (string?)pr["status"] == "completed" ? "merged" : "abandoned", repository);
        var threads = await GetAsync($"{Project}/_apis/git/repositories/{pr["repository"]!["id"]}/pullRequests/{id}/threads");
        foreach (var thread in threads["value"]!.AsArray())
            foreach (var comment in thread!["comments"]?.AsArray() ?? [])
            {
                var at = (DateTimeOffset?)comment!["publishedDate"];
                if (!inDay(at) || (string?)comment["author"]?["id"] != myId || (bool?)comment["isDeleted"] == true) continue;
                var system = (string?)comment["commentType"] == "system";
                var content = (string?)comment["content"] ?? "";
                if (system && content.Contains("voted")) On(at!.Value, "vote", content[(content.IndexOf("voted") + 6)..].Trim());
                else if (!system) On(at!.Value, (int?)comment["id"] == 1 ? "thread" : "reply", Excerpt(content));
            }
    }));
}

// My commits of that day in the workspace's own repositories (worktrees share them), grouped by the PBI their message
// names ("#251726 ..."); a commit without one is left out.
static void DailyCommits(DateOnly day, Action<string, int, DateTimeOffset, string, string> add)
{
    foreach (var repository in Directory.GetDirectories(Workspace()).Where(folder => Directory.Exists(Path.Combine(folder, ".git"))))
    {
        string Git(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in new[] { "-C", repository }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var git = System.Diagnostics.Process.Start(start)!;
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return output;
        }
        var email = Git("config", "user.email").Trim();
        if (email.Length == 0) continue;
        var log = Git("log", "--all", "--no-merges", $"--author={email}", $"--since={day:yyyy-MM-dd} 00:00",
            $"--until={day.AddDays(1):yyyy-MM-dd} 00:00", "--format=%h%x09%aI%x09%s");
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length < 3) continue;
            var pbi = System.Text.RegularExpressions.Regex.Match(parts[2], @"#(\d{6})");
            if (!pbi.Success) continue;
            add("pbi", int.Parse(pbi.Groups[1].Value), DateTimeOffset.Parse(parts[1]), "commit",
                $"{Path.GetFileName(repository)} {parts[0]} {parts[2]}");
        }
    }
}

// Vault files written that day (PBI working files and reviews, English copies) and the dashboard's Claude sessions. A
// file's day is its frontmatter `updated`, not its modification time, which link and translation passes also change.
static void DailyVaultFiles(Func<DateTimeOffset?, bool> inDay, Action<string, int, DateTimeOffset, string, string> add)
{
    static DateTimeOffset? Updated(string file)
    {
        using var reader = new StreamReader(file);
        if (reader.ReadLine() != "---") return null;
        for (var line = reader.ReadLine(); line is not null and not "---"; line = reader.ReadLine())
            if (line.StartsWith("updated:") && DateTimeOffset.TryParse(line["updated:".Length..].Split('#')[0].Trim().Trim('"', '\''), out var at))
                return at;
        return null;
    }
    var pbiFolders = Directory.Exists(Path.Combine(Vault, "PBIs")) ? Directory.GetDirectories(Path.Combine(Vault, "PBIs"), "pbi-*") : [];
    foreach (var folder in pbiFolders)
    {
        if (!int.TryParse(Path.GetFileName(folder)["pbi-".Length..], out var id)) continue;
        foreach (var file in Directory.GetFiles(folder, "0*.md").Where(file => !file.EndsWith(".fr.md")))
            if (Updated(file) is { } at && inDay(at))
                add("pbi", id, at, "file", Path.GetFileNameWithoutExtension(file));
        foreach (var session in Sessions(id).Where(session => inDay((DateTimeOffset?)session["startedAt"])))
            add("pbi", id, (DateTimeOffset)session["startedAt"]!, "session", (string?)session["kind"] ?? "work");
    }
    if (!Directory.Exists(ReviewDirectory())) return;
    foreach (var file in Directory.GetFiles(ReviewDirectory(), "*.md").Where(file => !file.EndsWith(".fr.md")))
    {
        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"^(\d+)-(review|threads)\.md$");
        if (match.Success && Updated(file) is { } at && inDay(at))
            add("pr", int.Parse(match.Groups[1].Value), at, "report", Path.GetFileNameWithoutExtension(file));
    }
}

static string Excerpt(string? text)
{
    // HTML (work item comments) and markdown (PR comments) to one plain line; an ADO mention is @<guid>.
    var plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]+>", " "));
    plain = System.Text.RegularExpressions.Regex.Replace(plain, @"@<[0-9a-fA-F-]{36}>", "@");
    plain = System.Text.RegularExpressions.Regex.Replace(plain, "[*`#>_]", " ");
    plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
    return plain.Length > 140 ? plain[..140] + "…" : plain;
}

// Adds me to a pull request's reviewers as optional, unless I am one already: re-adding would reset my vote.
// Returns {"reviewer": "added" | "already" | "failed", "detail": ...}. Writing needs the PAT's Code (Read & Write) scope.
async Task<JsonObject> AddMeAsOptionalReviewerAsync(int pullRequestId)
{
    try
    {
        myId ??= (string)(await GetAsync("_apis/connectionData?api-version=7.1-preview"))["authenticatedUser"]!["id"]!;
        var pr = await GetAsync($"{Project}/_apis/git/pullrequests/{pullRequestId}");
        if ((pr["reviewers"]?.AsArray() ?? []).Any(reviewer => (string?)reviewer!["id"] == myId))
            return new JsonObject { ["reviewer"] = "already" };
        var repositoryId = (string)pr["repository"]!["id"]!;
        await SendAsync(new HttpRequestMessage(HttpMethod.Put,
            WithVersion($"{Project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/reviewers/{myId}"))
        {
            Content = new StringContent(new JsonObject { ["vote"] = 0, ["isRequired"] = false }.ToJsonString(), Encoding.UTF8, "application/json"),
        });
        Console.WriteLine($"Added as optional reviewer on PR {pullRequestId}");
        return new JsonObject { ["reviewer"] = "added" };
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Reviewer on PR {pullRequestId}: {exception.Message}");
        return new JsonObject { ["reviewer"] = "failed", ["detail"] = exception.Message };
    }
}

async Task<JsonObject> TodayAsync()
{
    myId ??= (string)(await GetAsync("_apis/connectionData?api-version=7.1-preview"))["authenticatedUser"]!["id"]!;

    var myItemsTask = MyWorkItemsAsync();
    var reviewsTask = ReviewsAsync();
    var sprintTask = CurrentSprintAsync();
    var (myItems, reviews) = (await myItemsTask, await reviewsTask);

    // Backlog order of my PBIs and of the work items linked to the PRs I review, sprint by sprint.
    var cards = myItems.Concat(reviews.SelectMany(pr => pr["workItems"]!.AsArray().Select(item => item!.AsObject()))).ToList();
    var order = await BacklogOrderAsync(cards.Select(card => (string?)card["iterationPath"]));
    foreach (var card in cards)
        if (order.TryGetValue((int)card["id"]!, out var position)) card["backlogOrder"] = position;

    return new JsonObject
    {
        ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
        ["currentSprint"] = await sprintTask,
        ["workItems"] = new JsonArray([.. myItems]),
        ["reviews"] = new JsonArray([.. reviews]),
    };
}

// My open PBIs and bugs, each with the pull requests linked to it (Development section of the work item).
async Task<JsonObject[]> MyWorkItemsAsync()
{
    var wiql = new JsonObject
    {
        ["query"] = $"""
            SELECT [System.Id] FROM WorkItems
            WHERE [System.TeamProject] = '{Project}' AND [System.AssignedTo] = @Me
              AND [System.WorkItemType] IN ('Product Backlog Item', 'Bug')
              AND [System.State] NOT IN ('Done', 'Removed', 'Closed')
            ORDER BY [System.ChangedDate] DESC
            """,
    };
    var ids = (await PostAsync($"{Project}/_apis/wit/wiql", wiql))["workItems"]!.AsArray().Select(item => (int)item!["id"]!).ToList();
    var items = await WorkItemsAsync(ids);

    var cards = await Task.WhenAll(items.Select(async item =>
    {
        var pullRequestIds = (item["relations"]?.AsArray() ?? [])
            .Select(relation => Uri.UnescapeDataString((string?)relation!["url"] ?? ""))
            .Where(url => url.StartsWith("vstfs:///Git/PullRequestId/", StringComparison.OrdinalIgnoreCase))
            .Select(url => int.Parse(url.Split('/').Last()))
            .Distinct();
        var pullRequestsTask = Task.WhenAll(pullRequestIds.Select(PullRequestAsync));
        var historyTask = HistoryAsync((int)item["id"]!);
        var card = WorkItemCard(item);
        card["pullRequests"] = new JsonArray([.. (await pullRequestsTask).OrderByDescending(pr => (int)pr["id"]!)]);
        card["history"] = await historyTask;
        card["sessions"] = new JsonArray([.. Sessions((int)item["id"]!)]);
        return card;
    }));
    return cards;
}

// The "Order" column of the team's sprint backlog: the position of each item among the sprint's PBIs and bugs of the
// team's area paths, all states but Removed (Done included), sorted by Backlog Priority with the unprioritized ones
// last. Items outside the team's areas get no order. A failure leaves the orders out, not the dashboard.
async Task<Dictionary<int, int>> BacklogOrderAsync(IEnumerable<string?> iterationPaths)
{
    var order = new Dictionary<int, int>();
    try
    {
        var teamAreas = await GetAsync($"{Project}/{Uri.EscapeDataString(Team)}/_apis/work/teamsettings/teamfieldvalues");
        var areaClause = string.Join(" OR ", teamAreas["values"]!.AsArray().Select(area =>
            $"[System.AreaPath] {((bool?)area!["includeChildren"] == true ? "UNDER" : "=")} '{WiqlText((string)area["value"]!)}'"));
        foreach (var path in iterationPaths.OfType<string>().Where(path => path.Contains('\\')).Distinct())
        {
            var wiql = new JsonObject
            {
                ["query"] = $"""
                    SELECT [System.Id] FROM WorkItems
                    WHERE [System.TeamProject] = '{Project}' AND [System.IterationPath] = '{WiqlText(path)}'
                      AND [System.WorkItemType] IN ('Product Backlog Item', 'Bug') AND [System.State] <> 'Removed'
                      AND ({areaClause})
                    """,
            };
            var ids = (await PostAsync($"{Project}/_apis/wit/wiql", wiql))["workItems"]!.AsArray().Select(item => (int)item!["id"]!).ToList();
            var priorities = new List<(int Id, double? Priority)>();
            foreach (var chunk in ids.Chunk(200))
            {
                var batch = await GetAsync($"_apis/wit/workitems?ids={string.Join(',', chunk)}&fields=Microsoft.VSTS.Common.BacklogPriority");
                priorities.AddRange(batch["value"]!.AsArray().Select(item =>
                    ((int)item!["id"]!, (double?)item["fields"]?["Microsoft.VSTS.Common.BacklogPriority"])));
            }
            var ranked = priorities.OrderBy(item => item.Priority ?? double.MaxValue).ThenBy(item => item.Id).ToList();
            for (var index = 0; index < ranked.Count; index++) order[ranked[index].Id] = index + 1;
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Backlog order: {exception.Message}");
    }
    return order;
}

static string WiqlText(string text) => text.Replace("'", "''");

// History of a work item from its revisions: creation, state changes, sprint changes and assignments, oldest first,
// each with its date and author. Revisions that change none of these (fields, links, comments) are left out.
async Task<JsonArray> HistoryAsync(int workItemId)
{
    // 200 revisions at most (the API's limit), plenty for a PBI. A failure leaves the history empty, not the dashboard.
    JsonNode updates;
    try
    {
        updates = await GetAsync($"{Project}/_apis/wit/workitems/{workItemId}/updates?$top=200");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"History of {workItemId}: {exception.Message}");
        return [];
    }
    // A list, not JsonArray.Add: Add(JsonObject) binds to the generic Add<T>, which trimming and AOT warn about.
    var events = new List<JsonObject>();
    foreach (var update in updates["value"]!.AsArray())
    {
        var fields = update!["fields"];
        var date = (string?)fields?["System.ChangedDate"]?["newValue"];
        if (date is null) continue;
        var by = FirstName((string?)update["revisedBy"]?["displayName"]);
        JsonObject Event(string type) => new() { ["type"] = type, ["date"] = date, ["by"] = by };

        var state = fields!["System.State"];
        if ((int?)update["rev"] == 1)
        {
            var created = Event("created");
            created["to"] = (string?)state?["newValue"];
            events.Add(created);
        }
        else if (state is not null)
        {
            var changed = Event("state");
            changed["from"] = (string?)state["oldValue"];
            changed["to"] = (string?)state["newValue"];
            events.Add(changed);
        }
        if (fields["System.IterationPath"] is { } iteration && (int?)update["rev"] != 1)
        {
            var sprint = Event("sprint");
            sprint["from"] = ((string?)iteration["oldValue"])?.Split('\\').Last();
            sprint["to"] = ((string?)iteration["newValue"])?.Split('\\').Last();
            events.Add(sprint);
        }
        if (fields["System.AssignedTo"] is { } assigned && (string?)assigned["newValue"]?["displayName"] is { } assignee)
        {
            var assignment = Event("assigned");
            assignment["to"] = FirstName(assignee);
            events.Add(assignment);
        }
    }
    return new JsonArray([.. events]);
}

// The current sprint of the project's default team, for the sprint filter of the page. Null when there is none.
async Task<JsonObject?> CurrentSprintAsync()
{
    var iterations = await GetAsync($"{Project}/_apis/work/teamsettings/iterations?$timeframe=current");
    var current = iterations["value"]!.AsArray().FirstOrDefault();
    if (current is null) return null;
    return new JsonObject
    {
        ["name"] = (string?)current["name"],
        ["path"] = (string?)current["path"],
        ["startDate"] = (string?)current["attributes"]?["startDate"],
        ["finishDate"] = (string?)current["attributes"]?["finishDate"],
    };
}

// Active pull requests where I am a reviewer and not the author, each with its linked work items.
async Task<JsonObject[]> ReviewsAsync()
{
    var list = await GetAsync($"{Project}/_apis/git/pullrequests?searchCriteria.reviewerId={myId}&searchCriteria.status=active&$top=100");
    var ids = list["value"]!.AsArray()
        .Where(pr => (string?)pr!["createdBy"]!["id"] != myId)
        .Select(pr => (int)pr!["pullRequestId"]!);

    return await Task.WhenAll(ids.Select(async id =>
    {
        var pullRequest = await PullRequestAsync(id);
        var links = await GetAsync($"{Project}/_apis/git/repositories/{pullRequest["repositoryId"]}/pullRequests/{id}/workitems");
        var workItemIds = links["value"]!.AsArray().Select(link => int.Parse((string)link!["id"]!)).ToList();
        pullRequest["workItems"] = new JsonArray([.. (await WorkItemsAsync(workItemIds)).Select(WorkItemCard)]);
        return pullRequest;
    }));
}

async Task<List<JsonNode>> WorkItemsAsync(List<int> ids)
{
    if (ids.Count == 0) return [];
    var result = await GetAsync($"_apis/wit/workitems?ids={string.Join(',', ids)}&$expand=relations&errorPolicy=omit");
    return result["value"]!.AsArray().Where(item => item is not null).Select(item => item!).ToList();
}

JsonObject WorkItemCard(JsonNode item)
{
    var fields = item["fields"]!;
    var id = (int)item["id"]!;
    return new JsonObject
    {
        ["id"] = id,
        ["title"] = (string?)fields["System.Title"],
        ["type"] = (string?)fields["System.WorkItemType"],
        ["state"] = (string?)fields["System.State"],
        ["assignedTo"] = FirstName((string?)fields["System.AssignedTo"]?["displayName"]),
        ["iteration"] = ((string?)fields["System.IterationPath"])?.Split('\\').Last(),
        ["iterationPath"] = (string?)fields["System.IterationPath"],
        ["tags"] = (string?)fields["System.Tags"],
        // HTML written in Azure DevOps: the page sanitizes it before showing it.
        ["description"] = (string?)fields["System.Description"],
        ["acceptanceCriteria"] = (string?)fields["Microsoft.VSTS.Common.AcceptanceCriteria"],
        ["changedDate"] = (string?)fields["System.ChangedDate"],
        ["url"] = $"{Organization}/{Project}/_workitems/edit/{id}",
        ["files"] = PbiFiles(id),
        ["lastSession"] = LastSession(id),
    };
}

async Task<JsonObject> PullRequestAsync(int id)
{
    var pr = await GetAsync($"{Project}/_apis/git/pullrequests/{id}");
    var repositoryId = (string)pr["repository"]!["id"]!;
    var repository = (string)pr["repository"]!["name"]!;
    var status = (string)pr["status"]!;

    // Comment threads: all of them and the ones still open (active or pending), the others being resolved (fixed,
    // closed, by design, won't fix). System threads (votes, pushes, policy updates) are not discussions.
    int? openThreads = null, totalThreads = null;
    List<JsonObject> openThreadList = [];
    JsonObject? ci = null;
    DateTimeOffset? myLastAction = null, lastPush = null, authorLastComment = null;
    // Last activity: creation, closing, or the last change of any thread, system ones included (pushes, votes).
    var activity = new List<DateTimeOffset?> { (DateTimeOffset?)pr["creationDate"], (DateTimeOffset?)pr["closedDate"] };
    if (status == "active")
    {
        ci = await BuildPolicyAsync((string)pr["repository"]!["project"]!["id"]!, id);

        var threads = await GetAsync($"{Project}/_apis/git/repositories/{repositoryId}/pullRequests/{id}/threads");
        activity.AddRange(threads["value"]!.AsArray().Select(thread => (DateTimeOffset?)thread!["lastUpdatedDate"]));
        var discussions = threads["value"]!.AsArray().Where(thread =>
            (bool?)thread!["isDeleted"] != true
            && (string?)thread["comments"]?[0]?["commentType"] != "system").ToList();
        totalThreads = discussions.Count;
        var open = discussions.Where(thread => (string?)thread!["status"] is "active" or "pending").ToList();
        openThreads = open.Count;
        openThreadList = [.. open.Select(thread => OpenThread(thread!, id, repository))];

        // Who acted last, for a pull request I review: my last comment or vote, against the last push (RefUpdate
        // system threads) and the author's last comment. The page tells "reviewed, waiting" from "to review again".
        string? Property(JsonNode thread, string name) => (string?)thread["properties"]?[name]?["$value"];
        var authorId = (string?)pr["createdBy"]?["id"];
        var comments = threads["value"]!.AsArray()
            .Where(thread => (bool?)thread!["isDeleted"] != true)
            .SelectMany(thread => thread!["comments"]?.AsArray() ?? [])
            .Where(comment => (bool?)comment!["isDeleted"] != true && (string?)comment["commentType"] != "system")
            .ToList();
        DateTimeOffset? LastCommentBy(string? authorOfComment) => comments
            .Where(comment => (string?)comment!["author"]?["id"] == authorOfComment)
            .Select(comment => (DateTimeOffset?)comment!["publishedDate"]).Max();
        DateTimeOffset? LastThread(Func<JsonNode, bool> match) => threads["value"]!.AsArray()
            .Where(thread => match(thread!)).Select(thread => (DateTimeOffset?)thread!["publishedDate"]).Max();
        var myLastVote = LastThread(thread => Property(thread, "CodeReviewThreadType") == "VoteUpdate"
            && (string?)thread["identities"]?[Property(thread, "CodeReviewVotedByIdentity") ?? ""]?["id"] == myId);
        myLastAction = new[] { LastCommentBy(myId), myLastVote }.Max();
        lastPush = LastThread(thread => Property(thread, "CodeReviewThreadType") == "RefUpdate");
        authorLastComment = authorId is null ? null : LastCommentBy(authorId);
    }

    var reviewers = (pr["reviewers"]?.AsArray() ?? [])
        .Where(reviewer => (bool?)reviewer!["isContainer"] != true)
        .Select(reviewer => new JsonObject
        {
            ["name"] = FirstName((string?)reviewer!["displayName"]),
            ["vote"] = (int)reviewer["vote"]!,
            ["isRequired"] = (bool?)reviewer["isRequired"] ?? false,
            ["isMe"] = (string?)reviewer["id"] == myId,
        });

    return new JsonObject
    {
        ["id"] = id,
        ["title"] = (string?)pr["title"],
        ["description"] = (string?)pr["description"],
        ["repository"] = repository,
        ["repositoryId"] = repositoryId,
        ["status"] = status,
        ["isDraft"] = (bool?)pr["isDraft"] ?? false,
        ["mergeStatus"] = (string?)pr["mergeStatus"],
        ["targetBranch"] = ((string?)pr["targetRefName"])?.Replace("refs/heads/", ""),
        ["author"] = FirstName((string?)pr["createdBy"]?["displayName"]),
        ["isMine"] = (string?)pr["createdBy"]?["id"] == myId,
        ["createdDate"] = (string?)pr["creationDate"],
        ["closedDate"] = (string?)pr["closedDate"],
        ["lastActivity"] = activity.Max()?.ToString("O"),
        ["myLastAction"] = myLastAction?.ToString("O"),
        ["lastPush"] = lastPush?.ToString("O"),
        ["authorLastComment"] = authorLastComment?.ToString("O"),
        ["openThreads"] = openThreads,
        ["totalThreads"] = totalThreads,
        ["threads"] = new JsonArray([.. openThreadList]),
        ["ci"] = ci,
        ["reviewers"] = new JsonArray([.. reviewers]),
        ["url"] = $"{Organization}/{Project}/_git/{Uri.EscapeDataString(repository)}/pullrequest/{id}",
        ["review"] = Report(Path.Combine(ReviewDirectory(), $"{id}-review")),
        ["threadsReport"] = Report(Path.Combine(ReviewDirectory(), $"{id}-threads")),
        ["reviewSession"] = LastReviewSession(id, "review"),
        ["threadsSession"] = LastReviewSession(id, "threads"),
    };
}

// An open comment thread for the page: where it sits (file and line, none for a PR-level thread), its link in Azure
// DevOps and its comments (author, date, markdown content), oldest first.
JsonObject OpenThread(JsonNode thread, int pullRequestId, string repository)
{
    var context = thread["threadContext"];
    var comments = (thread["comments"]?.AsArray() ?? [])
        .Where(comment => (bool?)comment!["isDeleted"] != true && (string?)comment["commentType"] != "system")
        .Select(comment => new JsonObject
        {
            ["author"] = FirstName((string?)comment!["author"]?["displayName"]),
            ["date"] = (string?)comment["publishedDate"],
            ["content"] = (string?)comment["content"],
        });
    return new JsonObject
    {
        ["id"] = (int)thread["id"]!,
        ["file"] = (string?)context?["filePath"],
        ["line"] = (int?)(context?["rightFileStart"] ?? context?["leftFileStart"])?["line"],
        ["url"] = $"{Organization}/{Project}/_git/{Uri.EscapeDataString(repository)}/pullrequest/{pullRequestId}?discussionId={thread["id"]}",
        ["comments"] = new JsonArray([.. comments]),
    };
}

// CI of a pull request = its Build branch policies: failed, running, expired (needs a re-queue) or succeeded, with the
// build to open. Null when the branch has no build policy.
async Task<JsonObject?> BuildPolicyAsync(string projectId, int pullRequestId)
{
    var evaluations = await GetAsync($"{Project}/_apis/policy/evaluations?artifactId=vstfs:///CodeReview/CodeReviewId/{projectId}/{pullRequestId}&api-version=7.1-preview");
    var builds = evaluations["value"]!.AsArray()
        .Where(evaluation => (string?)evaluation!["configuration"]?["type"]?["displayName"] == "Build")
        .Select(evaluation => (
            Status: (string?)evaluation!["status"] switch
            {
                "rejected" or "broken" => "failed",
                "queued" or "running" => "running",
                _ when (bool?)evaluation["context"]?["isExpired"] == true => "expired",
                "approved" => "succeeded",
                var other => other ?? "unknown",
            },
            BuildId: (int?)evaluation["context"]?["buildId"]))
        .ToList();
    if (builds.Count == 0) return null;

    var order = new[] { "failed", "expired", "running", "unknown", "succeeded" };
    var worst = builds.OrderBy(build => Array.IndexOf(order, build.Status) is var index and >= 0 ? index : 3).First();
    return new JsonObject
    {
        ["status"] = worst.Status,
        ["buildUrl"] = worst.BuildId is { } buildId ? $"{Organization}/{Project}/_build/results?buildId={buildId}" : null,
    };
}

// A report written by a Claude session: <path>.md in English, with its French translation <path>.fr.md under "fr"
// when there is one. A report that only exists in French (older ones) comes as the main file.
static JsonObject? Report(string path)
{
    var english = MarkdownFile(path + ".md");
    var french = MarkdownFile(path + ".fr.md");
    if (english is null) return french;
    if (french is not null) english["fr"] = french;
    return english;
}

// A markdown file of the vault sent as raw text, with its path relative to the vault. Null when missing.
static JsonObject? MarkdownFile(string path)
{
    var file = new FileInfo(path);
    if (!file.Exists) return null;
    return new JsonObject
    {
        ["file"] = Path.GetRelativePath(Vault, file.FullName).Replace('\\', '/'),
        ["updated"] = file.LastWriteTime.ToString("O"),
        ["markdown"] = File.ReadAllText(file.FullName),
    };
}

// The working files of a PBI (rule "PBI working files" in the workspace CLAUDE.md), keyed by step, each as a Report
// (English, with its French copy under "fr"). Missing files are left out; the page parses their YAML frontmatter.
static string[] PbiSteps() =>
    ["00-status", "01-summary", "02-analysis", "03-job-done", "04-ai-review", "05-test-instructions", "06-open-questions", "07-pr-description"];

static JsonObject PbiFiles(int workItemId)
{
    var files = new JsonObject();
    foreach (var step in PbiSteps())
        if (Report(Path.Combine(PbiDirectory(workItemId), step)) is { } report) files[step] = report;
    return files;
}

// ------------------------------------------------------------ questions (rule "Questions" in the workspace CLAUDE.md)
// One file per question, <vault>/PBIs/pbi-<id>/questions/q<NN>-<slug>.md, or <vault>/Questions/q-<date>-<slug>.md for
// a question tied to no PBI. Its frontmatter holds its state, who it was asked to, where and when; its body the
// question, then its answers ("### <date> - <author> - <source>" under "## Answers") and optional notes.
static string QuestionsDirectory(int? workItemId) =>
    workItemId is { } id ? Path.Combine(PbiDirectory(id), "questions") : Path.Combine(Vault, "Questions");

static JsonArray Questions()
{
    var files = new List<string>();
    var pbis = Path.Combine(Vault, "PBIs");
    if (Directory.Exists(pbis))
        foreach (var directory in Directory.GetDirectories(pbis, "pbi-*").Select(pbi => Path.Combine(pbi, "questions")).Where(Directory.Exists))
            files.AddRange(Directory.GetFiles(directory, "q*.md"));
    if (Directory.Exists(QuestionsDirectory(null))) files.AddRange(Directory.GetFiles(QuestionsDirectory(null), "q-*.md"));
    return new JsonArray([.. files.Order().Select(Question)]);
}

static JsonObject Question(string path)
{
    var text = File.ReadAllText(path).Replace("\r\n", "\n");
    var end = text.StartsWith("---\n") ? text.IndexOf("\n---", 3) : -1;
    var fields = new Dictionary<string, string>();
    if (end > 0)
        foreach (var line in text[4..end].Split('\n'))
            // A "#" comment needs spaces around it, so the "#<comment id>" of a link stays in the value.
            if (Regex.Match(line, @"^(\w+):\s*(.*?)(\s+#\s.*)?$") is { Success: true } field)
                fields[field.Groups[1].Value] = field.Groups[2].Value.Trim().Trim('"', '\'');

    // The body: its title, then one "## " section each for the question, the answers and the notes.
    string? title = null;
    var sections = new Dictionary<string, List<string>>();
    List<string>? current = null;
    foreach (var line in (end > 0 ? text[(text.IndexOf('\n', end + 1) + 1)..] : text).Split('\n'))
    {
        if (line.StartsWith("## ")) sections[line[3..].Trim()] = current = [];
        else if (current is null && line.StartsWith("# ")) title ??= line[2..].Trim();
        else current?.Add(line);
    }
    string Section(string name) => sections.TryGetValue(name, out var lines) ? string.Join('\n', lines).Trim() : "";

    var answers = new JsonArray();
    foreach (var block in Regex.Split("\n" + Section("Answers"), @"\n### ").Skip(1))
    {
        var lines = block.Split('\n');
        var parts = lines[0].Split(" - ");
        var link = lines.Skip(1).FirstOrDefault(line => line.Trim().Length > 0) is { } first && first.StartsWith("Link:") ? first[5..].Trim() : null;
        // As a JsonNode: the generic Add<T> is not fit for AOT.
        answers.Add((JsonNode)new JsonObject
        {
            ["date"] = parts[0].Trim(),
            ["author"] = parts.Length > 2 ? string.Join(" - ", parts[1..^1]).Trim() : "",
            ["source"] = parts.Length > 1 ? parts[^1].Trim() : "",
            ["link"] = link,
            ["text"] = string.Join('\n', lines.Skip(1).Where(line => link is null || !line.StartsWith("Link:"))).Trim(),
        });
    }

    var file = Path.GetRelativePath(Vault, path).Replace('\\', '/');
    string? Field(string name) => fields.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
    return new JsonObject
    {
        ["file"] = file,
        ["name"] = Path.GetFileNameWithoutExtension(path),
        ["pbi"] = Regex.Match(file, @"^PBIs/pbi-(\d+)/") is { Success: true } pbi ? int.Parse(pbi.Groups[1].Value) : null,
        ["title"] = title ?? Path.GetFileNameWithoutExtension(path),
        ["state"] = Field("state") ?? "draft",
        ["to"] = Field("to"),
        ["channel"] = Field("channel"),
        ["asked"] = Field("asked"),
        ["link"] = Field("link"),
        ["follows"] = Field("follows"),
        ["updated"] = Field("updated") ?? File.GetLastWriteTime(path).ToString("O"),
        ["question"] = Section("Question"),
        ["answers"] = answers,
        ["notes"] = Section("Notes"),
    };
}

// The path of a question file named by the page (relative to the vault): only an existing question file.
static string QuestionPath(string file)
{
    if (!Regex.IsMatch(file, @"^(PBIs/pbi-\d+/questions|Questions)/q[\w-]+\.md$")) throw new ArgumentException("Not a question file.");
    var path = Path.Combine(Vault, file.Replace('/', Path.DirectorySeparatorChar));
    return File.Exists(path) ? path : throw new ArgumentException($"{file} does not exist.");
}

// A date typed in a form (local time, or empty for now), as the vault writes it: ISO 8601 with offset.
static string QuestionDate(string value) =>
    (value.Length == 0 ? DateTimeOffset.Now
        : DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeLocal, out var date) ? date
        : throw new ArgumentException("The date is not valid.")).ToString("yyyy-MM-ddTHH:mm:sszzz");

// Markdown typed in a form must not open a section of the file: headings down to `level` lose their meaning.
static string NoHeading(string markdown, int level) =>
    Regex.Replace(markdown.Replace("\r\n", "\n"), $@"^(#{{1,{level}}}\s)", @"\$1", RegexOptions.Multiline);

// Changes frontmatter fields of a question file (and "updated"), and its body through `body` when given.
static void UpdateQuestion(string path, Dictionary<string, string> fields, Func<string, string>? body = null)
{
    var text = File.ReadAllText(path).Replace("\r\n", "\n");
    var end = text.StartsWith("---\n") ? text.IndexOf("\n---", 3) : -1;
    if (end < 0) throw new ArgumentException($"{Path.GetFileName(path)} has no frontmatter.");
    var head = text[4..end].Split('\n').ToList();
    fields["updated"] = QuestionDate("");
    foreach (var (name, value) in fields)
    {
        var line = $"{name}: {value}".TrimEnd();
        if (head.FindIndex(existing => existing.StartsWith(name + ":")) is var index and >= 0) head[index] = line;
        else head.Add(line);
    }
    File.WriteAllText(path, "---\n" + string.Join('\n', head) + (body is null ? text[end..] : body(text[end..])));
}

// Adds an answer at the end of "## Answers" (answers go oldest first), before the notes when there are some.
static string AddAnswer(string rest, string answer)
{
    var lines = rest.TrimEnd().Split('\n').ToList();
    var start = lines.FindIndex(line => line.Trim() == "## Answers");
    if (start < 0) return string.Join('\n', lines) + "\n\n## Answers\n\n" + answer + "\n";
    var next = lines.FindIndex(start + 1, line => line.StartsWith("## "));
    var before = string.Join('\n', lines[..(next < 0 ? lines.Count : next)]).TrimEnd();
    return before + "\n\n" + answer + "\n" + (next < 0 ? "" : "\n" + string.Join('\n', lines[next..]) + "\n");
}

// A new question file, numbered after the last one of its PBI, and its line in the index: 06-open-questions.md (and
// its French copy) for a PBI, Questions/Questions.md otherwise.
static void CreateQuestion(int? workItemId, string title, string question, string follows, Dictionary<string, string> fields)
{
    var directory = QuestionsDirectory(workItemId);
    Directory.CreateDirectory(directory);
    // Accents go (decomposed, then their marks dropped), the rest becomes hyphens.
    var slug = Regex.Replace(Regex.Replace(title.Normalize(NormalizationForm.FormD).ToLowerInvariant(), @"\p{M}", ""), "[^a-z0-9]+", "-").Trim('-');
    slug = slug.Length > 48 ? slug[..48].TrimEnd('-') : slug.Length > 0 ? slug : "question";
    var number = Directory.GetFiles(directory, "q*.md").Select(file => Regex.Match(Path.GetFileName(file), @"^q(\d+)-"))
        .Where(match => match.Success).Select(match => int.Parse(match.Groups[1].Value)).DefaultIfEmpty(0).Max() + 1;
    var name = workItemId is null ? $"q-{DateTime.Today:yyyy-MM-dd}-{slug}" : $"q{number:00}-{slug}";
    var heading = workItemId is null ? title : $"Q{number} - {title}";
    var path = Path.Combine(directory, name + ".md");
    if (File.Exists(path)) throw new ArgumentException($"{name}.md already exists.");
    if (follows.Length > 0 && !File.Exists(Path.Combine(directory, follows + ".md"))) throw new ArgumentException("The question it follows does not exist.");

    string Field(string field) => $"{field}: {fields.GetValueOrDefault(field)}".TrimEnd();
    var now = QuestionDate("");
    string[] workItem = workItemId is { } id ? [$"Work item: [[PBIs/pbi-{id}/pbi-{id}|PBI {id}]]", ""] : [];
    string[] content =
    [
        "---", $"updated: {now}", "session: dashboard", "sessions: [dashboard]", Field("state"), Field("to"), Field("channel"),
        Field("asked"), Field("link"), $"follows: {follows}".TrimEnd(), "---", "", $"# {heading}", "", .. workItem,
        "## Question", "", NoHeading(question, 2), "", "## Answers", "",
    ];
    File.WriteAllText(path, string.Join('\n', content));

    // The alias of a wikilink ends at "|" or "]".
    var alias = Regex.Replace(Regex.Replace(heading, @"[\[\]|]", ""), @"\s+", " ");
    // The first entry of a list leaves a blank line under its heading.
    string Append(string text, string line) => text + (text.EndsWith("## Questions") ? "\n" : "") + "\n" + line + "\n";
    if (workItemId is not { } pbi)
    {
        var index = Path.Combine(directory, "Questions.md");
        var lines = (File.Exists(index) ? File.ReadAllText(index).Replace("\r\n", "\n").TrimEnd() : "# Questions\n\n## Questions").Split('\n').ToList();
        lines.RemoveAll(line => line.Trim() == "None yet.");
        File.WriteAllText(index, Append(string.Join('\n', lines).TrimEnd(), $"- [[Questions/{name}|{alias}]]"));
        return;
    }
    var entry = $"- [[PBIs/pbi-{pbi}/questions/{name}|{alias}]]";
    var english = Path.Combine(PbiDirectory(pbi), "06-open-questions.md");
    if (!File.Exists(english))
    {
        File.WriteAllText(english, $"---\nupdated: {now}\nsession: dashboard\nsessions: [dashboard]\n---\n\n# {pbi} - open questions\n\n## Questions\n");
        // A new working file gets its line in the hub note of the PBI, after the other working files.
        var hub = Path.Combine(PbiDirectory(pbi), $"pbi-{pbi}.md");
        var hubLines = File.Exists(hub) ? File.ReadAllText(hub).Replace("\r\n", "\n").Split('\n').ToList() : [];
        if (hubLines.FindLastIndex(line => line.StartsWith($"- [[PBIs/pbi-{pbi}/0")) is var last and >= 0)
        {
            hubLines.Insert(last + 1, $"- [[PBIs/pbi-{pbi}/06-open-questions|Open questions]]");
            File.WriteAllText(hub, string.Join('\n', hubLines));
        }
    }
    foreach (var index in new[] { english, Path.ChangeExtension(english, ".fr.md") }.Where(File.Exists))
    {
        var text = Regex.Replace(File.ReadAllText(index).Replace("\r\n", "\n").TrimEnd(), @"(?m)^updated: .*$", $"updated: {now}");
        File.WriteAllText(index, Append(text + (text.Contains("\n## Questions") ? "" : "\n\n## Questions"), entry));
    }
}

// Records a session started from the dashboard in <vault>/PBIs/pbi-<id>/sessions.jsonl and returns its id.
static string RecordSession(int workItemId, string kind)
{
    var sessionId = Guid.NewGuid().ToString();
    Directory.CreateDirectory(PbiDirectory(workItemId));
    File.AppendAllText(SessionsFile(workItemId), new JsonObject
    {
        ["pbi"] = workItemId,
        ["kind"] = kind,
        ["sessionId"] = sessionId,
        ["startedAt"] = DateTimeOffset.Now.ToString("O"),
    }.ToJsonString() + "\n");
    return sessionId;
}

// The last Claude session started from the dashboard for a PBI (one JSON object per line in its sessions.jsonl), the
// one "Resume session" reopens. A session that only wrote a French copy is not one to resume.
static JsonObject? LastSession(int workItemId) => Sessions(workItemId).LastOrDefault(session => (string?)session["kind"] != "translate");

// All the sessions started from the dashboard for a PBI, oldest first.
static List<JsonObject> Sessions(int workItemId)
{
    if (!File.Exists(SessionsFile(workItemId))) return [];
    return File.ReadLines(SessionsFile(workItemId))
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonNode.Parse(line)!.AsObject())
        .Where(session => (int?)session["pbi"] == workItemId)
        .ToList();
}

// Pull request reviews and thread analyses started from the dashboard, in <vault>/Reviews/sessions.jsonl: pr, kind (review or
// threads), sessionId, startedAt, and cwd (relative to the workspace) for older sessions that ran in a checkout,
// since claude --resume only finds a session from the folder it ran in.
static string ReviewSessionsFile() => Path.Combine(ReviewDirectory(), "sessions.jsonl");

static string RecordReviewSession(int pullRequestId, string kind)
{
    var sessionId = Guid.NewGuid().ToString();
    File.AppendAllText(ReviewSessionsFile(), new JsonObject
    {
        ["pr"] = pullRequestId,
        ["kind"] = kind,
        ["sessionId"] = sessionId,
        ["startedAt"] = DateTimeOffset.Now.ToString("O"),
    }.ToJsonString() + "\n");
    return sessionId;
}

static JsonObject? LastReviewSession(int pullRequestId, string kind)
{
    if (!File.Exists(ReviewSessionsFile())) return null;
    return File.ReadLines(ReviewSessionsFile())
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonNode.Parse(line)!.AsObject())
        .LastOrDefault(session => (int?)session["pr"] == pullRequestId && (string?)session["kind"] == kind);
}

// Opens a Claude session in a new Windows Terminal tab, in the workspace or in one of its folders, with the vault as an
// extra working directory (--add-dir) since the session writes its files there. `arguments` is PowerShell text.
static void OpenClaudeTab(string title, string arguments, string? folder = null)
{
    var directory = folder is null ? Workspace() : Path.GetFullPath(Path.Combine(Workspace(), folder));
    if (!directory.StartsWith(Workspace(), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory)) directory = Workspace();
    var terminal = new System.Diagnostics.ProcessStartInfo("wt.exe") { UseShellExecute = false };
    foreach (var argument in new[] { "-w", "0", "new-tab", "--title", title, "-d", directory, "pwsh", "-NoLogo", "-NoExit", "-Command",
                 $"claude --add-dir {PowerShellQuote(Vault)} {arguments}" })
        terminal.ArgumentList.Add(argument);
    System.Diagnostics.Process.Start(terminal);
    Console.WriteLine($"Claude session opened: {title}");
}

// ----------------------------------------------------------------------------------------------- helpers

async Task<JsonNode> GetAsync(string path) => await SendAsync(new HttpRequestMessage(HttpMethod.Get, WithVersion(path)));

async Task<JsonNode> PostAsync(string path, JsonNode body) => await SendAsync(new HttpRequestMessage(HttpMethod.Post, WithVersion(path))
{
    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
});

async Task<JsonNode> SendAsync(HttpRequestMessage request)
{
    using var response = await http.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    // A rejected PAT gets a 203 with the HTML sign-in page instead of a 401.
    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
        throw new InvalidOperationException($"{request.Method} {request.RequestUri!.AbsolutePath}: {(int)response.StatusCode} {(content.TrimStart().StartsWith('<') ? "(HTML page: PAT rejected or expired?)" : content[..Math.Min(content.Length, 300)])}");
    return JsonNode.Parse(content)!;
}

// Prompts of the Claude sessions, always in English. The page language only picks the language Claude answers in and
// adds a French copy of the report: the English report always exists (<name>.md), the French one is <name>.fr.md.
// No ';' in them: Windows Terminal reads it as a command separator.
static string ReviewPrompt(int pullRequestId, string sessionId, string language) =>
    $"Review PR {pullRequestId} on Azure DevOps (organization {OrganizationName}, project {Project}), in the format of the reports " +
    $"already in {ReviewDirectory()}. Do not post any comment and do not vote on the PR, the decision is mine. " +
    ReportInstruction(Path.Combine(ReviewDirectory(), $"{pullRequestId}-review"), sessionId, language);

static string ThreadsPrompt(int pullRequestId, string sessionId, string language) =>
    $"Analyze the unresolved comment threads of PR {pullRequestId} on Azure DevOps (organization {OrganizationName}, project {Project}). " +
    "For each active or pending thread, sum up the request, check in the code whether it is founded, then propose a " +
    "reply or a fix. End with a table: thread, request, verdict, proposed action. Do not post any reply, resolve any " +
    "thread or change any code before I approve. " +
    ReportInstruction(Path.Combine(ReviewDirectory(), $"{pullRequestId}-threads"), sessionId, language);

// Review of any PBI, typed in the page: the work done for it, through its pull requests, against what it asks.
static string PbiReviewPrompt(int workItemId, string sessionId, string language) =>
    $"Review PBI #{workItemId} on Azure DevOps (organization {OrganizationName}, project {Project}): read the PBI, its acceptance " +
    "criteria and its comments, find its linked pull requests, and review their changes against what the PBI asks: " +
    "acceptance criteria covered or not, correctness, tests, and the conventions of the reports already in " +
    $"{ReviewDirectory()}. If the PBI has no pull request, say so and review what can be checked. Do not post any comment, do not " +
    "vote and do not change the PBI, the decision is mine. " +
    PbiFilesInstruction(workItemId, sessionId, language,
        "update 00-status.md, write the review in 04-ai-review.md and add what needs an answer as question files (rule Questions of CLAUDE.md)");

static string PbiPrompt(int workItemId, string sessionId, string language) =>
    $"Start working on PBI #{workItemId} on Azure DevOps (organization {OrganizationName}, project {Project}): read the PBI, its " +
    "acceptance criteria and its comments, sum up what is asked, list the open questions and propose a plan. Do not " +
    "write code before I approve the plan. " +
    PbiFilesInstruction(workItemId, sessionId, language,
        "create 00-status.md first, then write 01-summary.md, 02-analysis.md with the plan, and the questions if " +
        "there are some, one file each with their index 06-open-questions.md (rule Questions of CLAUDE.md). Later steps go to 03-job-done.md, 04-ai-review.md, 05-test-instructions.md and " +
        "07-pr-description.md");

// One step of a PBI done again from its tab: the step is the working file to write, StepTasks says what it takes.
static string StepPrompt(int workItemId, string step, string sessionId, string language) =>
    $"Work on the step {step} of PBI #{workItemId} on Azure DevOps (organization {OrganizationName}, project {Project}): read the " +
    $"PBI and the working files already in {PbiDirectory(workItemId)}, then {StepTasks[step]} " +
    PbiFilesInstruction(workItemId, sessionId, language, $"write {step}.md and update 00-status.md");

// The French copy of a working file or of a report that only exists in English.
static string TranslatePrompt(string directory, string name, string sessionId) =>
    $"Write the French copy of {Path.Combine(directory, name + ".md")} next to it, as {name}.fr.md: same " +
    $"structure, translated to French, with the same frontmatter where this session ({sessionId}) is added. Do not change " +
    "the English file or any other file. Answer me in French.";

// Where a PBI session writes: the "PBI working files" rule of the workspace CLAUDE.md, in <vault>/PBIs/pbi-<id>/.
static string PbiFilesInstruction(int workItemId, string sessionId, string language, string steps) =>
    $"Follow the PBI working files rule of CLAUDE.md in {PbiDirectory(workItemId)} (this session is {sessionId}, name it " +
    $"in the frontmatter of every file you write): {steps}. If these files already exist, read them first and complete " +
    "them without erasing their history. " +
    (language == "fr"
        ? "Write the French copy (.fr.md) of each file next to it. Answer me in French."
        : "Answer me in English.");

// A report starts with the same YAML frontmatter as the PBI working files (rule in the workspace CLAUDE.md).
static string ReportInstruction(string report, string sessionId, string language) =>
    $"Start the report with a YAML frontmatter: updated (ISO 8601 with offset), session ({sessionId}, this session) and " +
    "sessions (every session that wrote it, oldest first). If the report already exists, read it first and complete it. " +
    "Under its title, link the PBI it is about and list the report in that PBI's hub note, as the vault rules of CLAUDE.md say. " +
    (language == "fr"
        ? $"Write the report in English in {report}.md and its French translation, same structure, in {report}.fr.md. Answer me in French."
        : $"Write the report in English in {report}.md. Answer me in English.");

static string Workspace() => Settings.Workspace ?? Path.GetFullPath(Path.Combine(SourceDirectory(), ".."));

static string PbiDirectory(int workItemId) => Path.Combine(Vault, "PBIs", $"pbi-{workItemId}");

static string ReviewDirectory() => Path.Combine(Vault, "Reviews");

static string SessionsFile(int workItemId) => Path.Combine(PbiDirectory(workItemId), "sessions.jsonl");

static string PowerShellQuote(string text) => "'" + text.Replace("'", "''") + "'";

static string WithVersion(string path) => path.Contains("api-version=") ? path : path + (path.Contains('?') ? "&" : "?") + ApiVersion;

static string? FirstName(string? displayName) => displayName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

// Prompt texts that are data: what doing each step of a PBI again takes (the steps a tab of the PBI page can start),
// for StepPrompt. No ';' in them.
static class Prompts
{
    // Where a question can be asked and an answer can come from (an answer can also be my own "decision").
    public static readonly string[] QuestionChannels = ["ado", "teams", "email", "kb", "call", "other"];

    public static readonly Dictionary<string, string> StepTasks = new()
    {
        ["01-summary"] = "sum up what the PBI asks: goal, acceptance criteria, scope and what is out of scope.",
        ["02-analysis"] = "analyze what has to change and how: code areas, contract, options with their trade-offs, and the plan. Do not write code.",
        ["03-job-done"] = "carry out the plan of 02-analysis.md: tell me what you are about to change and wait for my approval before writing code, then record the work done.",
        ["04-ai-review"] = "review the changes of its pull requests against what the PBI asks, in the format of the reports of the vault. Do not post any comment and do not vote.",
        ["05-test-instructions"] = "write how to test it: endpoints with sample payloads, which account, environment and expected results.",
        ["06-open-questions"] = "write the questions for the PO or the team as the Questions rule of CLAUDE.md says: one file per question in its questions folder, as drafts, and their lines in the index 06-open-questions.md. Keep the questions already there with their state and answers. Do not post them.",
        ["07-pr-description"] = "write a pull request description ready to paste in Azure DevOps.",
    };
}

// Settings of this machine, from dashboard.settings.json next to dashboard.cs (not committed; see
// dashboard.settings.example.json): the Azure DevOps organization URL, project and team (whose sprint backlog gives
// the PBIs' order), the vault folder of the PBI working files and reviews, the environment variable holding the PAT,
// and optionally the workspace (the folder of the checkouts, the parent of dashboard\ by default).
static class Settings
{
    public static string Organization { get; private set; } = "";
    public static string OrganizationName => Organization.TrimEnd('/').Split('/').Last();
    public static string Project { get; private set; } = "";
    public static string Team { get; private set; } = "";
    public static string Vault { get; private set; } = "";
    public static string PatVariable { get; private set; } = "AZURE_DEVOPS_PAT";
    public static string? Workspace { get; private set; }

    // Returns what is wrong with the file, or null once the settings are loaded.
    public static string? Load(string path)
    {
        if (!File.Exists(path)) return $"{path} is missing: copy dashboard.settings.example.json to it and fill it in.";
        JsonNode json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(path))!;
        }
        catch (System.Text.Json.JsonException exception)
        {
            return $"{path} is not valid JSON ({exception.Message}). Backslashes in paths are written \\\\.";
        }
        string? Read(string name) => (string?)json[name] is { Length: > 0 } value ? value : null;
        var missing = new[] { "organization", "project", "team", "vault" }.Where(name => Read(name) is null).ToList();
        if (missing.Count > 0) return $"{path}: set {string.Join(", ", missing)}.";
        Organization = Read("organization")!.TrimEnd('/');
        Project = Read("project")!;
        Team = Read("team")!;
        Vault = Read("vault")!;
        PatVariable = Read("patVariable") ?? PatVariable;
        Workspace = Read("workspace") is { } workspace ? Path.GetFullPath(workspace) : null;
        return null;
    }
}
