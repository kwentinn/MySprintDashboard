// The vault: the reports and PBI working files the page shows, and the record of the Claude sessions started from
// the dashboard (sessions.jsonl).
public static class VaultStore
{
    public static string PbiDirectory(int workItemId) => Path.Combine(Vault, "PBIs", $"pbi-{workItemId}");

    public static string ReviewDirectory() => Path.Combine(Vault, "Reviews");

    public static string SessionsFile(int workItemId) => Path.Combine(PbiDirectory(workItemId), "sessions.jsonl");

    // A report written by a Claude session: <path>.md in English, with its French translation <path>.fr.md under "fr"
    // when there is one. A report that only exists in French (older ones) comes as the main file.
    public static JsonObject? Report(string path)
    {
        var english = MarkdownFile(path + ".md");
        var french = MarkdownFile(path + ".fr.md");
        if (english is null) return french;
        if (french is not null) english["fr"] = french;
        return english;
    }

    // A markdown file of the vault sent as raw text, with its path relative to the vault. Null when missing.
    public static JsonObject? MarkdownFile(string path)
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
    public static string[] PbiSteps() =>
        ["00-status", "01-summary", "02-analysis", "03-job-done", "04-ai-review", "05-test-instructions", "06-open-questions", "07-pr-description"];

    public static JsonObject PbiFiles(int workItemId)
    {
        var files = new JsonObject();
        foreach (var step in PbiSteps())
            if (Report(Path.Combine(PbiDirectory(workItemId), step)) is { } report) files[step] = report;
        return files;
    }

    // Records a session started from the dashboard in <vault>/PBIs/pbi-<id>/sessions.jsonl and returns its id.
    public static string RecordSession(int workItemId, string kind)
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
    public static JsonObject? LastSession(int workItemId) => Sessions(workItemId).LastOrDefault(session => (string?)session["kind"] != "translate");

    // All the sessions started from the dashboard for a PBI, oldest first.
    public static List<JsonObject> Sessions(int workItemId)
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
    public static string ReviewSessionsFile() => Path.Combine(ReviewDirectory(), "sessions.jsonl");

    public static string RecordReviewSession(int pullRequestId, string kind)
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

    public static JsonObject? LastReviewSession(int pullRequestId, string kind)
    {
        if (!File.Exists(ReviewSessionsFile())) return null;
        return File.ReadLines(ReviewSessionsFile())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .LastOrDefault(session => (int?)session["pr"] == pullRequestId && (string?)session["kind"] == kind);
    }
}
