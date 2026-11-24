// Azure DevOps: my open PBIs with their pull requests, the pull requests waiting for my review, and the calls behind
// them. Everything goes through GetAsync / PostAsync, with the PAT of the client.
public static class Ado
{
    const string ApiVersion = "api-version=7.1";
    // The client, set once by Program: the organization as base address, the PAT in its Authorization header.
    public static HttpClient Http { get; set; } = null!;
    // My user id, read once from connectionData.
    public static string? MyId { get; set; }

    // Adds me to a pull request's reviewers as optional, unless I am one already: re-adding would reset my vote.
    // Returns {"reviewer": "added" | "already" | "failed", "detail": ...}. Writing needs the PAT's Code (Read & Write) scope.
    public static async Task<JsonObject> AddMeAsOptionalReviewerAsync(int pullRequestId)
    {
        try
        {
            MyId ??= (string)(await GetAsync("_apis/connectionData?api-version=7.1-preview"))["authenticatedUser"]!["id"]!;
            var pr = await GetAsync($"{Project}/_apis/git/pullrequests/{pullRequestId}");
            if ((pr["reviewers"]?.AsArray() ?? []).Any(reviewer => (string?)reviewer!["id"] == MyId))
                return new JsonObject { ["reviewer"] = "already" };
            var repositoryId = (string)pr["repository"]!["id"]!;
            await SendAsync(new HttpRequestMessage(HttpMethod.Put,
                WithVersion($"{Project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/reviewers/{MyId}"))
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

    public static async Task<JsonObject> TodayAsync()
    {
        MyId ??= (string)(await GetAsync("_apis/connectionData?api-version=7.1-preview"))["authenticatedUser"]!["id"]!;

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
    public static async Task<JsonObject[]> MyWorkItemsAsync()
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
    public static async Task<Dictionary<int, int>> BacklogOrderAsync(IEnumerable<string?> iterationPaths)
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

    public static string WiqlText(string text) => text.Replace("'", "''");

    // History of a work item from its revisions: creation, state changes, sprint changes and assignments, oldest first,
    // each with its date and author. Revisions that change none of these (fields, links, comments) are left out.
    public static async Task<JsonArray> HistoryAsync(int workItemId)
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
    public static async Task<JsonObject?> CurrentSprintAsync()
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
    public static async Task<JsonObject[]> ReviewsAsync()
    {
        var list = await GetAsync($"{Project}/_apis/git/pullrequests?searchCriteria.reviewerId={MyId}&searchCriteria.status=active&$top=100");
        var ids = list["value"]!.AsArray()
            .Where(pr => (string?)pr!["createdBy"]!["id"] != MyId)
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

    public static async Task<List<JsonNode>> WorkItemsAsync(List<int> ids)
    {
        if (ids.Count == 0) return [];
        var result = await GetAsync($"_apis/wit/workitems?ids={string.Join(',', ids)}&$expand=relations&errorPolicy=omit");
        return result["value"]!.AsArray().Where(item => item is not null).Select(item => item!).ToList();
    }

    public static JsonObject WorkItemCard(JsonNode item)
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

    public static async Task<JsonObject> PullRequestAsync(int id)
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
                && (string?)thread["identities"]?[Property(thread, "CodeReviewVotedByIdentity") ?? ""]?["id"] == MyId);
            myLastAction = new[] { LastCommentBy(MyId), myLastVote }.Max();
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
                ["isMe"] = (string?)reviewer["id"] == MyId,
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
            ["isMine"] = (string?)pr["createdBy"]?["id"] == MyId,
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
    public static JsonObject OpenThread(JsonNode thread, int pullRequestId, string repository)
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
    public static async Task<JsonObject?> BuildPolicyAsync(string projectId, int pullRequestId)
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

    public static async Task<JsonNode> GetAsync(string path) => await SendAsync(new HttpRequestMessage(HttpMethod.Get, WithVersion(path)));

    public static async Task<JsonNode> PostAsync(string path, JsonNode body) => await SendAsync(new HttpRequestMessage(HttpMethod.Post, WithVersion(path))
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    });

    public static async Task<JsonNode> SendAsync(HttpRequestMessage request)
    {
        using var response = await Http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        // A rejected PAT gets a 203 with the HTML sign-in page instead of a 401.
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidOperationException($"{request.Method} {request.RequestUri!.AbsolutePath}: {(int)response.StatusCode} {(content.TrimStart().StartsWith('<') ? "(HTML page: PAT rejected or expired?)" : content[..Math.Min(content.Length, 300)])}");
        return JsonNode.Parse(content)!;
    }

    public static string WithVersion(string path) => path.Contains("api-version=") ? path : path + (path.Contains('?') ? "&" : "?") + ApiVersion;

    public static string? FirstName(string? displayName) => displayName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
}
