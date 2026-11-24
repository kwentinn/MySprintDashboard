// Daily: what I did on a given day
// Events of that day, grouped by work item or pull request, from four sources: work item revisions and comments by me
// (ADO), my comments, votes, PRs created or completed (ADO), my commits in the workspace checkouts (git), and the vault
// files and Claude sessions of the dashboard. Every event is {time, kind, text}; a group is {type, id, title, url, events}.
public static class Daily
{
    public static DateOnly PreviousWorkingDay(DateOnly today) => today.DayOfWeek switch
    {
        DayOfWeek.Monday => today.AddDays(-3),
        DayOfWeek.Sunday => today.AddDays(-2),
        _ => today.AddDays(-1),
    };

    public static async Task<JsonObject> DailyAsync(DateOnly day)
    {
        MyId ??= (string)(await GetAsync("_apis/connectionData?api-version=7.1-preview"))["authenticatedUser"]!["id"]!;
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
            group["isMine"] = (string?)pr["createdBy"]?["id"] == MyId;
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
    public static async Task<string> DailySummaryAsync(JsonObject daily, string language)
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
    public static async Task DailyWorkItemsAsync(DateOnly day, Func<DateTimeOffset?, bool> inDay,
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
                if (fields is null || !inDay(at) || (string?)update["revisedBy"]?["id"] != MyId) continue;
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
                if (!inDay(at) || (string?)comment["createdBy"]?["id"] != MyId) continue;
                // "Mentioned in !57352" is written by Azure DevOps when a PR comment names the work item, not by me.
                if (((string?)comment["text"] ?? "").StartsWith("Mentioned in ")) continue;
                events("pbi", id, titles.GetValueOrDefault(id), $"{Organization}/{Project}/_workitems/edit/{id}");
                add("pbi", id, at!.Value, "comment", Excerpt((string?)comment["text"]));
            }
        }));
    }

    // Pull requests I review or wrote, active or closed since that day: my comments and votes, PRs I created or completed.
    public static async Task DailyPullRequestsAsync(DateTimeOffset from, Func<DateTimeOffset?, bool> inDay,
        Func<string, int, string?, string?, JsonArray> events, Action<string, int, DateTimeOffset, string, string> add)
    {
        var lists = await Task.WhenAll(new[] { "reviewerId", "creatorId" }.Select(role =>
            GetAsync($"{Project}/_apis/git/pullrequests?searchCriteria.{role}={MyId}&searchCriteria.status=all&$top=50")));
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
            if ((string?)pr["createdBy"]?["id"] == MyId && inDay((DateTimeOffset?)pr["creationDate"]))
                On((DateTimeOffset)pr["creationDate"]!, "created", $"{repository} → {((string?)pr["targetRefName"])?.Replace("refs/heads/", "")}");
            if ((string?)pr["closedBy"]?["id"] == MyId && inDay((DateTimeOffset?)pr["closedDate"]))
                On((DateTimeOffset)pr["closedDate"]!, (string?)pr["status"] == "completed" ? "merged" : "abandoned", repository);
            var threads = await GetAsync($"{Project}/_apis/git/repositories/{pr["repository"]!["id"]}/pullRequests/{id}/threads");
            foreach (var thread in threads["value"]!.AsArray())
                foreach (var comment in thread!["comments"]?.AsArray() ?? [])
                {
                    var at = (DateTimeOffset?)comment!["publishedDate"];
                    if (!inDay(at) || (string?)comment["author"]?["id"] != MyId || (bool?)comment["isDeleted"] == true) continue;
                    var system = (string?)comment["commentType"] == "system";
                    var content = (string?)comment["content"] ?? "";
                    if (system && content.Contains("voted")) On(at!.Value, "vote", content[(content.IndexOf("voted") + 6)..].Trim());
                    else if (!system) On(at!.Value, (int?)comment["id"] == 1 ? "thread" : "reply", Excerpt(content));
                }
        }));
    }

    // My commits of that day in the workspace's own repositories (worktrees share them), grouped by the PBI their message
    // names ("#251726 ..."); a commit without one is left out.
    public static void DailyCommits(DateOnly day, Action<string, int, DateTimeOffset, string, string> add)
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
    public static void DailyVaultFiles(Func<DateTimeOffset?, bool> inDay, Action<string, int, DateTimeOffset, string, string> add)
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

    public static string Excerpt(string? text)
    {
        // HTML (work item comments) and markdown (PR comments) to one plain line; an ADO mention is @<guid>.
        var plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]+>", " "));
        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"@<[0-9a-fA-F-]{36}>", "@");
        plain = System.Text.RegularExpressions.Regex.Replace(plain, "[*`#>_]", " ");
        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
        return plain.Length > 140 ? plain[..140] + "…" : plain;
    }
}
