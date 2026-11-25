// Questions asked to the PO or the team (rule "Questions" in the workspace CLAUDE.md), read and written in the vault.
// One file per question, <vault>/PBIs/pbi-<id>/questions/q<NN>-<slug>.md, or <vault>/Questions/q-<date>-<slug>.md for
// a question tied to no PBI. Its frontmatter holds its state, who it was asked to, where and when; its body the
// question, then its answers ("### <date> - <author> - <source>" under "## Answers") and optional notes.
public static class VaultQuestions
{
    public static string QuestionsDirectory(int? workItemId) =>
        workItemId is { } id ? Path.Combine(PbiDirectory(id), "questions") : Path.Combine(Vault, "Questions");

    public static JsonArray Questions()
    {
        var files = new List<string>();
        var pbis = Path.Combine(Vault, "PBIs");
        if (Directory.Exists(pbis))
            foreach (var directory in Directory.GetDirectories(pbis, "pbi-*").Select(pbi => Path.Combine(pbi, "questions")).Where(Directory.Exists))
                files.AddRange(QuestionFiles(directory));
        if (Directory.Exists(QuestionsDirectory(null))) files.AddRange(QuestionFiles(QuestionsDirectory(null)));
        // A file that cannot be read or parsed is left out, and said on the console: the others still show.
        var questions = new JsonArray();
        foreach (var file in files.Order())
        {
            try { questions.Add((JsonNode)Question(file)); }
            catch (Exception exception) { Console.Error.WriteLine($"Question {file}: {exception.Message}"); }
        }
        return questions;
    }

    // The question files of a folder: "q<NN>-<slug>.md" or "q-<date>-<slug>.md". Not the index Questions.md (the file
    // system ignores case), and not a "q*.fr.md" a session wrote anyway: a question has no French copy (rule Questions).
    public static IEnumerable<string> QuestionFiles(string directory) =>
        Directory.GetFiles(directory, "q*.md").Where(file => Regex.IsMatch(Path.GetFileName(file), @"^q\d*-[\w-]*\.md$"));

    public static JsonObject Question(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var end = text.StartsWith("---\n") ? text.IndexOf("\n---", 3) : -1;
        var fields = new Dictionary<string, string>();
        if (end > 3)
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
    public static string QuestionPath(string file)
    {
        if (!Regex.IsMatch(file, @"^(PBIs/pbi-\d+/questions|Questions)/q[\w-]+\.md$")) throw new ArgumentException("Not a question file.");
        var path = Path.Combine(Vault, file.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : throw new ArgumentException($"{file} does not exist.");
    }

    // A date typed in a form (local time, or empty for now), as the vault writes it: ISO 8601 with offset.
    public static string QuestionDate(string value) =>
        (value.Length == 0 ? DateTimeOffset.Now
            : DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeLocal, out var date) ? date
            : throw new ArgumentException("The date is not valid.")).ToString("yyyy-MM-ddTHH:mm:sszzz");

    // Markdown typed in a form must not open a section of the file: headings down to `level` lose their meaning.
    public static string NoHeading(string markdown, int level) =>
        Regex.Replace(markdown.Replace("\r\n", "\n"), $@"^(#{{1,{level}}}\s)", @"\$1", RegexOptions.Multiline);

    // Changes frontmatter fields of a question file (and "updated"), and its body through `body` when given.
    public static void UpdateQuestion(string path, Dictionary<string, string> fields, Func<string, string>? body = null)
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
    public static string AddAnswer(string rest, string answer)
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
    public static void CreateQuestion(int? workItemId, string title, string question, string follows, Dictionary<string, string> fields, string workItemTitle = "")
    {
        var directory = QuestionsDirectory(workItemId);
        Directory.CreateDirectory(directory);
        // Accents go (decomposed, then their marks dropped), the rest becomes hyphens.
        var slug = Regex.Replace(Regex.Replace(title.Normalize(NormalizationForm.FormD).ToLowerInvariant(), @"\p{M}", ""), "[^a-z0-9]+", "-").Trim('-');
        slug = slug.Length > 48 ? slug[..48].TrimEnd('-') : slug.Length > 0 ? slug : "question";
        var number = QuestionFiles(directory).Select(file => Regex.Match(Path.GetFileName(file), @"^q(\d+)-"))
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
            var hubLine = $"- [[PBIs/pbi-{pbi}/06-open-questions|Open questions]]";
            if (!File.Exists(hub)) CreateHubNote(pbi, workItemTitle, hubLine, now);
            var hubLines = File.ReadAllText(hub).Replace("\r\n", "\n").Split('\n').ToList();
            if (!hubLines.Contains(hubLine) && hubLines.FindLastIndex(line => line.StartsWith($"- [[PBIs/pbi-{pbi}/0")) is var last and >= 0)
            {
                hubLines.Insert(last + 1, hubLine);
                File.WriteAllText(hub, string.Join('\n', hubLines));
            }
        }
        foreach (var index in new[] { english, Path.ChangeExtension(english, ".fr.md") }.Where(File.Exists))
        {
            var text = Regex.Replace(File.ReadAllText(index).Replace("\r\n", "\n").TrimEnd(), @"(?m)^updated: .*$", $"updated: {now}");
            File.WriteAllText(index, Append(text + (text.Contains("\n## Questions") ? "" : "\n\n## Questions"), entry));
        }
    }

    // A PBI that gets its first file from a question has no folder in the vault yet: the vault rule gives every PBI
    // folder a hub note that links its working files, and one line in PBIs/PBIs.md (under "Worked on").
    static void CreateHubNote(int pbi, string title, string firstLine, string now)
    {
        var name = title.Length > 0 ? $" - {title}" : "";
        File.WriteAllText(Path.Combine(PbiDirectory(pbi), $"pbi-{pbi}.md"),
            $"---\nupdated: {now}\npbi: {pbi}\ntags: [pbi]\n---\n\n# PBI {pbi}{name}\n\nPart of [[PBIs/PBIs|PBIs]]. Working files, in the format of the workspace `CLAUDE.md` (the dashboard reads them):\n\n{firstLine}\n");
        var index = Path.Combine(Vault, "PBIs", "PBIs.md");
        if (!File.Exists(index)) return;
        var lines = File.ReadAllText(index).Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(line => line.Trim() == "## Worked on");
        if (start < 0 || lines.Any(line => line.Contains($"[[PBIs/pbi-{pbi}/pbi-{pbi}|"))) return;
        var next = lines.FindIndex(start + 1, line => line.StartsWith("## "));
        var last = lines.FindLastIndex(next < 0 ? lines.Count - 1 : next - 1, line => line.StartsWith("- "));
        lines.Insert(last > start ? last + 1 : start + 1, $"- [[PBIs/pbi-{pbi}/pbi-{pbi}|{pbi}]]{name}");
        File.WriteAllText(index, string.Join('\n', lines));
    }
}
