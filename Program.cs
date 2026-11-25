// Morning dashboard: my open PBIs with their pull requests, and the pull requests waiting for my review, with
// my review notes from the vault (Reviews/<pr>-review.md) when there are some.
// .NET program (needs the .NET 10 SDK): this file holds the settings, the client and the routes; Ado.cs talks to
// Azure DevOps, Daily.cs builds a day's activity, VaultStore.cs and VaultQuestions.cs read and write the vault,
// Claude.cs opens the sessions.
//
//   dotnet run                     then open http://localhost:5099 (polls every 10 minutes; ?refresh=60 sets it in seconds)
//
// The organization, project, team, vault and PAT variable come from dashboard.settings.json next to this file (see
// dashboard.settings.example.json); the PAT itself is read from that environment variable and only put in the
// Authorization header. The page is the wwwroot folder next to this file, read on every request so it can be edited without
// a restart. Buttons on the cards open a Claude session (review a pull request, start a PBI) in a new Windows
// Terminal tab.

// `-- --port 5100` runs a second instance next to the usual one.
var portIndex = Array.IndexOf(args, "--port");
var Url = $"http://localhost:{(portIndex >= 0 && portIndex + 1 < args.Length ? int.Parse(args[portIndex + 1]) : 5099)}";

// A published executable with the wwwroot folder next to it is on its own: it reads the page and the settings from its own
// folder and opens the browser when it starts. Otherwise (dotnet run) they come from the folder of this file.
var standalone = File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
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

Http = new HttpClient { BaseAddress = new Uri(Organization + "/") };
Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Url);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

// The page: index.html at "/", and the files it names (css, js) from the same folder, the only one served. The
// browser asks again on each load (no-cache), so an edit shows without a restart.
var web = Path.Combine(home, "wwwroot");
if (!File.Exists(Path.Combine(web, "index.html")))
{
    Console.Error.WriteLine($"{web} is missing: the page (index.html, css, js) must be in that folder.");
    return 1;
}
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(web),
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});
var page = Path.Combine(web, "index.html");
app.MapGet("/", () => Results.Content(File.ReadAllText(page), "text/html; charset=utf-8"));
app.MapGet("/api/today", async () =>
{
    try
    {
        return Results.Content((await TodayAsync()).ToJsonString(), "application/json");
    }
    catch (Exception exception)
    {
        // No reflection-based serializer (the project stays fit for AOT): write the JSON by hand.
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

// The words of ?q= looked for in Azure DevOps and in the vault; ?closed=1 also looks into what is closed in Azure DevOps.
app.MapGet("/api/search", async (HttpRequest request) =>
{
    try
    {
        return Results.Content((await SearchAsync(request.Query["q"], request.Query["closed"] == "1")).ToJsonString(), "application/json");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return Results.Content(new JsonObject { ["detail"] = exception.Message }.ToJsonString(), "application/json", statusCode: 500);
    }
});

// A markdown file of the vault (?path=, relative to the vault), for the reader the page opens on a link between notes.
app.MapGet("/api/vault", (HttpRequest request) => VaultFile(request.Query["path"]) is { } file
    ? Results.Content(file.ToJsonString(), "application/json") : Results.NotFound());

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
                    Line("state") == "waiting" ? Asked() : new() { ["state"] = "draft" }, Line("pbiTitle"));
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
