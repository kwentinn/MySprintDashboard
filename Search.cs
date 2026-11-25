// Search: the words typed in the page, looked for in Azure DevOps (the work items and pull requests of the project)
// and in the markdown files of the vault. Every word must be found; case does not matter.
public static class Search
{
    const int MaxResults = 30;

    // The words of a query, lower case; "#1201" and "!4215" count as their number.
    public static string[] SearchWords(string? query) =>
        [.. (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.TrimStart('#', '!').ToLowerInvariant()).Where(word => word.Length > 0).Distinct().Take(8)];

    // {query, words, workItems, pullRequests, vault}. Azure DevOps failing (network, PAT) leaves the vault results, with
    // the reason in "adoError". `closed` also looks into the work items and pull requests that are closed.
    public static async Task<JsonObject> SearchAsync(string? query, bool closed)
    {
        var words = SearchWords(query);
        var result = new JsonObject
        {
            ["query"] = query ?? "",
            ["words"] = new JsonArray([.. words.Select(word => (JsonNode?)JsonValue.Create(word))]),
            ["workItems"] = new JsonArray(),
            ["pullRequests"] = new JsonArray(),
            ["vault"] = new JsonArray(),
        };
        if (words.Length == 0) return result;

        var vault = Task.Run(() => SearchVault(words));
        try
        {
            var workItems = SearchWorkItemsAsync(words, closed);
            var pullRequests = SearchPullRequestsAsync(words, closed);
            result["workItems"] = await workItems;
            result["pullRequests"] = await pullRequests;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Search: {exception.Message}");
            result["adoError"] = exception.Message;
        }
        result["vault"] = await vault;
        return result;
    }

    // Work items of the project whose title, description, acceptance criteria or comments hold every word (or whose id
    // is one of them), last changed first. WIQL looks for words that start with the ones typed ("allergen" finds
    // "allergens"), through the full-text index: a part of a word (CONTAINS on the title) scans the whole project and
    // times out after 30 seconds.
    public static async Task<JsonArray> SearchWorkItemsAsync(string[] words, bool closed)
    {
        var conditions = words.Select(word =>
        {
            var text = WiqlText(word) + "*";
            return $"([System.Title] CONTAINS WORDS '{text}' OR [System.Description] CONTAINS WORDS '{text}'"
                + $" OR [Microsoft.VSTS.Common.AcceptanceCriteria] CONTAINS WORDS '{text}' OR [System.History] CONTAINS WORDS '{text}'"
                + (int.TryParse(word, out var id) ? $" OR [System.Id] = {id}" : "") + ")";
        });
        var wiql = new JsonObject
        {
            ["query"] = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{WiqlText(Project)}' AND {string.Join(" AND ", conditions)}"
                + (closed ? "" : " AND [System.State] NOT IN ('Done', 'Removed', 'Closed')")
                + " ORDER BY [System.ChangedDate] DESC",
        };
        var ids = (await PostAsync($"{Project}/_apis/wit/wiql?$top={MaxResults}", wiql))["workItems"]!.AsArray().Select(item => (int)item!["id"]!).ToList();
        // The item whose id was typed first, then the ones whose title holds the words, each group last changed first.
        var items = (await WorkItemsAsync(ids))
            .OrderBy(item => words.Contains($"{item["id"]}") ? 0 : words.All(word => Holds((string?)item["fields"]?["System.Title"], word)) ? 1 : 2)
            .ThenBy(item => ids.IndexOf((int)item["id"]!));

        return new JsonArray([.. items.Select(item =>
        {
            var fields = item["fields"]!;
            var title = (string?)fields["System.Title"] ?? "";
            var description = PlainText((string?)fields["System.Description"]) + " " + PlainText((string?)fields["Microsoft.VSTS.Common.AcceptanceCriteria"]);
            // Where it was found: the title, else the description, else what is left, the comments.
            var where = words.All(word => Holds(title, word) || word == $"{item["id"]}") ? "title" : words.Any(word => Holds(description, word)) ? "description" : "comments";
            return (JsonNode)new JsonObject
            {
                ["id"] = (int)item["id"]!,
                ["title"] = title,
                ["type"] = (string?)fields["System.WorkItemType"],
                ["state"] = (string?)fields["System.State"],
                ["assignedTo"] = FirstName((string?)fields["System.AssignedTo"]?["displayName"]),
                ["iteration"] = ((string?)fields["System.IterationPath"])?.Split('\\').Last(),
                ["changedDate"] = (string?)fields["System.ChangedDate"],
                ["url"] = $"{Organization}/{Project}/_workitems/edit/{item["id"]}",
                ["where"] = where,
                ["excerpt"] = where == "description" ? Passage(description, words) : null,
            };
        })]);
    }

    // Pull requests of the project whose title or description hold every word (or whose id is one of them), newest
    // first. Azure DevOps has no text search on pull requests: the last ones are listed and filtered here, so an old
    // closed one may be missed, and comment threads are not looked into.
    public static async Task<JsonArray> SearchPullRequestsAsync(string[] words, bool closed)
    {
        var list = await GetAsync($"{Project}/_apis/git/pullrequests?searchCriteria.status={(closed ? "all" : "active")}&$top={(closed ? 500 : 200)}");
        return new JsonArray([.. list["value"]!.AsArray().Select(pr => pr!)
            .Where(pr => words.All(word => word == $"{pr["pullRequestId"]}" || Holds((string?)pr["title"], word) || Holds((string?)pr["description"], word)))
            .Take(MaxResults)
            .Select(pr =>
            {
                var repository = (string?)pr["repository"]?["name"] ?? "";
                var inTitle = words.All(word => word == $"{pr["pullRequestId"]}" || Holds((string?)pr["title"], word));
                return (JsonNode)new JsonObject
                {
                    ["id"] = (int)pr["pullRequestId"]!,
                    ["title"] = (string?)pr["title"],
                    ["repository"] = repository,
                    ["targetBranch"] = ((string?)pr["targetRefName"])?.Replace("refs/heads/", ""),
                    ["status"] = (string?)pr["status"],
                    ["author"] = FirstName((string?)pr["createdBy"]?["displayName"]),
                    ["isMine"] = MyId is not null && (string?)pr["createdBy"]?["id"] == MyId,
                    ["createdDate"] = (string?)pr["creationDate"],
                    ["closedDate"] = (string?)pr["closedDate"],
                    ["url"] = $"{Organization}/{Project}/_git/{Uri.EscapeDataString(repository)}/pullrequest/{pr["pullRequestId"]}",
                    ["where"] = inTitle ? "title" : "description",
                    ["excerpt"] = inTitle ? null : Passage(PlainText((string?)pr["description"]), words),
                };
            })]);
    }

    // The markdown files of the vault that hold every word, in their text or their path: {file, title, folder, pbi,
    // updated, count, passages} and, for a question, its state and who it was asked to. A file whose title or path
    // holds the words comes first, then the one with the most passages, then the last changed. French copies
    // (.fr.md) and hidden folders (.obsidian) are left out.
    public static JsonArray SearchVault(string[] words)
    {
        var root = Path.GetFullPath(Vault);
        if (words.Length == 0 || !Directory.Exists(root)) return [];
        var found = new List<(bool InTitle, int Count, string Updated, JsonObject File)>();
        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var file = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (file.EndsWith(".fr.md", StringComparison.OrdinalIgnoreCase) || file.Split('/').Any(part => part.StartsWith('.'))) continue;
            string text;
            try { text = File.ReadAllText(path).Replace("\r\n", "\n"); }
            catch (IOException) { continue; }
            if (!words.All(word => Holds(text, word) || Holds(file, word))) continue;

            // The frontmatter is not text to quote, but gives the last change and, for a question, its state.
            var end = text.StartsWith("---\n") ? text.IndexOf("\n---", 3) : -1;
            var head = end > 0 ? text[..end] : "";
            var lines = (end > 0 ? text[(end + 4)..] : text).Split('\n');
            string? Field(string name) => Regex.Match(head, $@"(?m)^{name}:[ \t]*(.*?)(\s+#\s.*)?$") is { Success: true } match && match.Groups[1].Value.Trim() is { Length: > 0 } value ? value : null;
            var title = lines.FirstOrDefault(line => line.StartsWith("# "))?[2..].Trim() ?? Path.GetFileNameWithoutExtension(path);
            var hits = lines.Where(line => !line.StartsWith("# ") && words.Any(word => Holds(line, word))).ToList();
            var updated = Field("updated") ?? File.GetLastWriteTime(path).ToString("O");
            var isQuestion = Regex.IsMatch(file, @"(^|/)[Qq]uestions/q\d*-[\w-]*\.md$");
            found.Add((words.All(word => Holds(title, word) || Holds(file, word)), hits.Count, updated, new JsonObject
            {
                ["file"] = file,
                ["title"] = title,
                ["folder"] = file.Contains('/') ? file.Split('/')[0] : "",
                ["pbi"] = Regex.Match(file, @"^PBIs/pbi-(\d+)/") is { Success: true } pbi ? int.Parse(pbi.Groups[1].Value) : null,
                ["updated"] = updated,
                ["count"] = hits.Count,
                ["passages"] = new JsonArray([.. hits.Take(3).Select(line => (JsonNode?)JsonValue.Create(Passage(PlainText(line), words)))]),
                ["question"] = isQuestion ? new JsonObject { ["state"] = Field("state") ?? "draft", ["to"] = Field("to") } : null,
            }));
        }
        return new JsonArray([.. found.OrderByDescending(hit => hit.InTitle).ThenByDescending(hit => hit.Count)
            .ThenByDescending(hit => hit.Updated, StringComparer.Ordinal).Take(40).Select(hit => (JsonNode)hit.File)]);
    }

    static bool Holds(string? text, string word) => text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);

    // HTML (Azure DevOps) or markdown to one plain line.
    public static string PlainText(string? text)
    {
        var plain = System.Net.WebUtility.HtmlDecode(Regex.Replace(text ?? "", "<[^>]+>", " "));
        plain = Regex.Replace(plain, @"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", "$1");
        plain = Regex.Replace(plain, @"\[([^\]]*)\]\([^)]*\)", "$1");
        // Emphasis and code marks go without leaving a space ("`Totals`." stays "Totals."); "_" stays, names hold it.
        return Regex.Replace(Regex.Replace(plain, "[*`]|^[#>\\s-]+", ""), @"\s+", " ").Trim();
    }

    // About 200 characters of a text around the first word found in it, cut at spaces, with "…" where it was cut.
    public static string Passage(string text, string[] words)
    {
        const int Before = 60, Length = 200;
        if (text.Length <= Length) return text;
        var first = words.Select(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, Math.Min(first - Before, text.Length - Length));
        if (start > 0 && text.IndexOf(' ', start) is var space and >= 0 && space < first) start = space + 1;
        var end = Math.Min(text.Length, start + Length);
        if (end < text.Length && text.LastIndexOf(' ', end) is var last && last > first) end = last;
        return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
    }
}
