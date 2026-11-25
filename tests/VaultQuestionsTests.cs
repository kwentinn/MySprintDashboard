using System.Text.Json.Nodes;
using Xunit;
using static VaultQuestions;

// The question files of the vault: what the dashboard reads from them and what its forms write. Each test gets an
// empty vault in a temporary folder; the settings are static, so the test classes share a collection and run one after the other.
[Collection("vault")]
public sealed class VaultQuestionsTests : IDisposable
{
    readonly string vault = Path.Combine(Path.GetTempPath(), "dashboard-tests-" + Guid.NewGuid().ToString("N"));

    public VaultQuestionsTests()
    {
        Directory.CreateDirectory(vault);
        var settings = Path.Combine(vault, "settings.json");
        File.WriteAllText(settings, new JsonObject
        {
            ["organization"] = "https://dev.azure.com/acme", ["project"] = "Shop", ["team"] = "Shop Team", ["vault"] = vault,
        }.ToJsonString());
        Assert.Null(Settings.Load(settings));
    }

    public void Dispose() => Directory.Delete(vault, recursive: true);

    string Write(string relativePath, string content)
    {
        var path = Path.Combine(vault, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    string Read(string relativePath) => File.ReadAllText(Path.Combine(vault, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    const string Asked = """
        ---
        updated: 2026-11-17T12:50:00+01:00
        session: abc
        sessions: [abc]
        state: waiting          # draft | waiting | resolved | dropped
        to: Eve, Bob
        channel: ado
        asked: 2026-11-16T16:28:00+01:00
        link: https://dev.azure.com/acme/Shop/_workitems/edit/1201#21829996
        follows: q01-invoice-language
        ---

        # Q2 - Should a refunded order keep its invoice?

        Work item: [[PBIs/pbi-1201/pbi-1201|PBI 1201]]

        ## Question

        Options: (a) keep it, (b) hide it.

        ### Context

        Legal may care.

        ## Answers

        ### 2026-11-17T09:12:00+01:00 - Eve - teams
        Link: https://teams.example/l/message/1

        I need to check with legal.

        ### 2026-11-18 - Bob - Legal - email

        Keep it.

        ## Notes

        Applied in the PDF renderer.
        """;

    [Fact]
    public void Question_reads_the_frontmatter_the_sections_and_the_answers()
    {
        var question = Question(Write("PBIs/pbi-1201/questions/q02-refunded-order.md", Asked));

        Assert.Equal("PBIs/pbi-1201/questions/q02-refunded-order.md", (string?)question["file"]);
        Assert.Equal("q02-refunded-order", (string?)question["name"]);
        Assert.Equal(1201, (int?)question["pbi"]);
        Assert.Equal("Q2 - Should a refunded order keep its invoice?", (string?)question["title"]);
        // The comment after the state goes, the "#<comment id>" of the link stays.
        Assert.Equal("waiting", (string?)question["state"]);
        Assert.Equal("https://dev.azure.com/acme/Shop/_workitems/edit/1201#21829996", (string?)question["link"]);
        Assert.Equal("Eve, Bob", (string?)question["to"]);
        Assert.Equal("ado", (string?)question["channel"]);
        Assert.Equal("q01-invoice-language", (string?)question["follows"]);
        // A "###" heading inside the question is part of its text.
        Assert.Equal("Options: (a) keep it, (b) hide it.\n\n### Context\n\nLegal may care.", (string?)question["question"]);
        Assert.Equal("Applied in the PDF renderer.", (string?)question["notes"]);

        var answers = question["answers"]!.AsArray();
        Assert.Equal(2, answers.Count);
        Assert.Equal("2026-11-17T09:12:00+01:00", (string?)answers[0]!["date"]);
        Assert.Equal("Eve", (string?)answers[0]!["author"]);
        Assert.Equal("teams", (string?)answers[0]!["source"]);
        Assert.Equal("https://teams.example/l/message/1", (string?)answers[0]!["link"]);
        Assert.Equal("I need to check with legal.", (string?)answers[0]!["text"]);
        // An author may hold " - ": the date is the first part, the source the last.
        Assert.Equal("Bob - Legal", (string?)answers[1]!["author"]);
        Assert.Equal("email", (string?)answers[1]!["source"]);
        Assert.Null(answers[1]!["link"]);
        Assert.Equal("Keep it.", (string?)answers[1]!["text"]);
    }

    [Fact]
    public void Question_without_fields_is_a_draft_with_no_answer()
    {
        var question = Question(Write("Questions/q-2026-11-24-who-owns-numbering.md", "---\nupdated: 2026-11-24\nstate:\nto:\n---\n\n# Who owns the numbering?\n\n## Question\n\nText.\n\n## Answers\n"));

        Assert.Null(question["pbi"]);
        Assert.Equal("draft", (string?)question["state"]);
        Assert.Null(question["to"]);
        Assert.Empty(question["answers"]!.AsArray());
        Assert.Equal("Text.", (string?)question["question"]);
    }

    [Fact]
    public void Questions_lists_the_question_files_only()
    {
        Write("PBIs/pbi-1201/questions/q01-invoice-language.md", Asked);
        Write("PBIs/pbi-1201/questions/q02-refunded-order.md", Asked);
        Write("PBIs/pbi-1201/questions/q02-refunded-order.fr.md", Asked);   // a French copy is not a question
        Write("PBIs/pbi-1201/06-open-questions.md", "# index");
        Write("PBIs/pbi-1187/01-summary.md", "# no questions folder");
        Write("Questions/Questions.md", "# index");                            // nor is the index, whatever the case of its name
        Write("Questions/q-2026-11-24-who-owns-numbering.md", Asked);

        var files = Questions().Select(question => (string)question!["file"]!).ToArray();

        Assert.Equal(
        [
            "PBIs/pbi-1201/questions/q01-invoice-language.md",
            "PBIs/pbi-1201/questions/q02-refunded-order.md",
            "Questions/q-2026-11-24-who-owns-numbering.md",
        ], files);
    }

    [Fact]
    public void Questions_is_empty_in_an_empty_vault() => Assert.Empty(Questions());

    [Fact]
    public void CreateQuestion_numbers_after_the_last_one_and_adds_it_to_the_indexes()
    {
        Write("PBIs/pbi-1201/questions/q01-invoice-language.md", Asked);
        Write("PBIs/pbi-1201/questions/q04-older.md", Asked);
        Write("PBIs/pbi-1201/06-open-questions.md", "---\nupdated: 2026-11-01\nsession: abc\n---\n\n# 1201 - open questions\n\nIntro.\n\n## Questions\n\n- [[PBIs/pbi-1201/questions/q01-invoice-language|Q1 - Language]]\n");
        Write("PBIs/pbi-1201/06-open-questions.fr.md", "---\nupdated: 2026-11-01\n---\n\n# 1201 - questions ouvertes\n\n## Questions\n\n- [[PBIs/pbi-1201/questions/q01-invoice-language|Q1 - Language]]\n");

        CreateQuestion(1201, "Numéro d'avoir [légal] | à imprimer ?", "Text.\n## Not a section\nEnd.", "q01-invoice-language",
            new() { ["state"] = "waiting", ["to"] = "Eve", ["channel"] = "teams", ["asked"] = "2026-11-24T11:40:00+01:00", ["link"] = "https://teams.example/1" });

        // Accents and punctuation leave the file name; the number follows the highest one.
        var question = Question(Path.Combine(vault, "PBIs", "pbi-1201", "questions", "q05-numero-d-avoir-legal-a-imprimer.md"));
        Assert.Equal("Q5 - Numéro d'avoir [légal] | à imprimer ?", (string?)question["title"]);
        Assert.Equal("waiting", (string?)question["state"]);
        Assert.Equal("Eve", (string?)question["to"]);
        Assert.Equal("teams", (string?)question["channel"]);
        Assert.Equal("2026-11-24T11:40:00+01:00", (string?)question["asked"]);
        Assert.Equal("https://teams.example/1", (string?)question["link"]);
        Assert.Equal("q01-invoice-language", (string?)question["follows"]);
        // A heading typed in the text cannot open a section of the file.
        Assert.Equal("Text.\n\\## Not a section\nEnd.", (string?)question["question"]);
        Assert.Empty(question["answers"]!.AsArray());

        var file = Read("PBIs/pbi-1201/questions/q05-numero-d-avoir-legal-a-imprimer.md");
        Assert.Contains("session: dashboard\nsessions: [dashboard]\n", file);
        Assert.Contains("\nWork item: [[PBIs/pbi-1201/pbi-1201|PBI 1201]]\n", file);

        // Both indexes get the line (its alias without the characters that end a wikilink) and a new "updated".
        const string line = "- [[PBIs/pbi-1201/questions/q05-numero-d-avoir-legal-a-imprimer|Q5 - Numéro d'avoir légal à imprimer ?]]";
        foreach (var index in new[] { "PBIs/pbi-1201/06-open-questions.md", "PBIs/pbi-1201/06-open-questions.fr.md" })
        {
            var text = Read(index);
            Assert.EndsWith("|Q1 - Language]]\n" + line + "\n", text);
            Assert.DoesNotContain("updated: 2026-11-01", text);
        }
        Assert.Contains("\nIntro.\n", Read("PBIs/pbi-1201/06-open-questions.md"));
    }

    [Fact]
    public void CreateQuestion_creates_the_index_of_a_pbi_and_its_line_in_the_hub_note()
    {
        Write("PBIs/pbi-1187/pbi-1187.md", "# PBI 1187\n\n- [[PBIs/pbi-1187/00-status|Status]]\n- [[PBIs/pbi-1187/01-summary|Summary]]\n\nSee also:\n\n- [[History/x|x]]\n");

        CreateQuestion(1187, "CSV or XLSX?", "", "", new() { ["state"] = "draft" });

        var question = Assert.Single(Questions())!;
        Assert.Equal("PBIs/pbi-1187/questions/q01-csv-or-xlsx.md", (string?)question["file"]);
        Assert.Equal("draft", (string?)question["state"]);
        Assert.Null(question["to"]);
        Assert.EndsWith("# 1187 - open questions\n\n## Questions\n\n- [[PBIs/pbi-1187/questions/q01-csv-or-xlsx|Q1 - CSV or XLSX?]]\n", Read("PBIs/pbi-1187/06-open-questions.md"));
        // After the other working files, before "See also".
        Assert.Contains("|Summary]]\n- [[PBIs/pbi-1187/06-open-questions|Open questions]]\n\nSee also:", Read("PBIs/pbi-1187/pbi-1187.md"));
    }

    [Fact]
    public void CreateQuestion_on_a_pbi_without_a_folder_gives_it_its_hub_note_and_its_line_in_the_index_of_pbis()
    {
        Write("PBIs/PBIs.md", "# PBIs\n\n## Worked on\n\n- [[PBIs/pbi-1187/pbi-1187|1187]] - Export\n\n## Reviewed\n\n- [[PBIs/pbi-1100/pbi-1100|1100]] - Other\n");

        CreateQuestion(1300, "First question", "", "", new() { ["state"] = "draft" }, "Credit notes");
        CreateQuestion(1300, "Second question", "", "", new() { ["state"] = "draft" }, "Credit notes");

        var hub = Read("PBIs/pbi-1300/pbi-1300.md");
        Assert.Contains("\npbi: 1300\n", hub);
        Assert.EndsWith("# PBI 1300 - Credit notes\n\nPart of [[PBIs/PBIs|PBIs]]. Working files, in the format of the workspace `CLAUDE.md` (the dashboard reads them):\n\n- [[PBIs/pbi-1300/06-open-questions|Open questions]]\n", hub);
        // One line, at the end of "Worked on", whatever the number of questions.
        Assert.Equal("# PBIs\n\n## Worked on\n\n- [[PBIs/pbi-1187/pbi-1187|1187]] - Export\n- [[PBIs/pbi-1300/pbi-1300|1300]] - Credit notes\n\n## Reviewed\n\n- [[PBIs/pbi-1100/pbi-1100|1100]] - Other\n", Read("PBIs/PBIs.md"));
        Assert.Equal(2, Questions().Count);
    }

    [Fact]
    public void Questions_shows_a_question_with_an_empty_frontmatter_and_skips_what_is_not_a_file()
    {
        Write("PBIs/pbi-1201/questions/q01-invoice-language.md", Asked);
        Write("PBIs/pbi-1201/questions/q02-empty-frontmatter.md", "---\n---\n\n# Q2 - Empty frontmatter\n\n## Question\n\nText.\n");
        Directory.CreateDirectory(Path.Combine(vault, "PBIs", "pbi-1201", "questions", "q03-a-folder.md"));

        var questions = Questions();

        Assert.Equal(["PBIs/pbi-1201/questions/q01-invoice-language.md", "PBIs/pbi-1201/questions/q02-empty-frontmatter.md"],
            questions.Select(question => (string)question!["file"]!));
        Assert.Equal("draft", (string?)questions[1]!["state"]);
        Assert.Equal("Text.", (string?)questions[1]!["question"]);
    }

    [Fact]
    public void CreateQuestion_without_a_pbi_goes_to_the_questions_folder()
    {
        Write("Questions/Questions.md", "# Questions\n\nIntro.\n\n## Questions\n\nNone yet.\n");

        CreateQuestion(null, "Who owns the numbering?", "Text.", "", new() { ["state"] = "draft" });

        var name = $"q-{DateTime.Today:yyyy-MM-dd}-who-owns-the-numbering";
        var question = Assert.Single(Questions())!;
        Assert.Equal($"Questions/{name}.md", (string?)question["file"]);
        Assert.Equal("Who owns the numbering?", (string?)question["title"]);
        Assert.DoesNotContain("Work item:", Read($"Questions/{name}.md"));
        Assert.Equal($"# Questions\n\nIntro.\n\n## Questions\n\n- [[Questions/{name}|Who owns the numbering?]]\n", Read("Questions/Questions.md"));
    }

    [Fact]
    public void CreateQuestion_refuses_a_missing_parent_and_a_second_file_of_the_same_name()
    {
        Assert.Throws<ArgumentException>(() => CreateQuestion(1201, "Follow-up", "", "q09-missing", new() { ["state"] = "draft" }));
        Assert.Empty(Questions());

        CreateQuestion(null, "Same title", "", "", new() { ["state"] = "draft" });
        Assert.Throws<ArgumentException>(() => CreateQuestion(null, "Same title", "", "", new() { ["state"] = "draft" }));
        Assert.Single(Questions());
    }

    [Fact]
    public void UpdateQuestion_changes_the_fields_it_is_given_and_nothing_else()
    {
        var path = Write("PBIs/pbi-1201/questions/q02-refunded-order.md", Asked);

        UpdateQuestion(path, new() { ["state"] = "dropped", ["channel"] = "" });

        var question = Question(path);
        Assert.Equal("dropped", (string?)question["state"]);
        Assert.Null(question["channel"]);
        Assert.Equal("Eve, Bob", (string?)question["to"]);
        Assert.NotEqual("2026-11-17T12:50:00+01:00", (string?)question["updated"]);
        Assert.Equal(2, question["answers"]!.AsArray().Count);
        // The body and the session lines are as they were.
        var text = File.ReadAllText(path);
        Assert.Contains("session: abc\nsessions: [abc]\n", text);
        Assert.EndsWith(Asked.ReplaceLineEndings("\n")[Asked.ReplaceLineEndings("\n").IndexOf("\n---\n\n# Q2")..], text);
    }

    [Fact]
    public void UpdateQuestion_refuses_a_file_without_frontmatter()
    {
        var path = Write("Questions/q-2026-11-24-bare.md", "# Bare\n\n## Question\n");

        Assert.Throws<ArgumentException>(() => UpdateQuestion(path, new() { ["state"] = "dropped" }));
    }

    [Fact]
    public void An_answer_goes_after_the_others_and_before_the_notes()
    {
        var path = Write("PBIs/pbi-1201/questions/q02-refunded-order.md", Asked);

        UpdateQuestion(path, new() { ["state"] = "resolved" }, rest => AddAnswer(rest, "### 2026-11-24T10:00:00+01:00 - Quentin - decision\n\nOption (a)."));

        var question = Question(path);
        Assert.Equal("resolved", (string?)question["state"]);
        var answers = question["answers"]!.AsArray();
        Assert.Equal(["Eve", "Bob - Legal", "Quentin"], answers.Select(answer => (string?)answer!["author"]));
        Assert.Equal("decision", (string?)answers[2]!["source"]);
        Assert.Equal("Option (a).", (string?)answers[2]!["text"]);
        Assert.Equal("Applied in the PDF renderer.", (string?)question["notes"]);
        Assert.EndsWith("Keep it.\n\n### 2026-11-24T10:00:00+01:00 - Quentin - decision\n\nOption (a).\n\n## Notes\n\nApplied in the PDF renderer.\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("\n---\n\n# T\n\n## Question\n\nQ.\n\n## Answers\n", "\n---\n\n# T\n\n## Question\n\nQ.\n\n## Answers\n\n### A\n\nText.\n")]
    [InlineData("\n---\n\n# T\n\n## Question\n\nQ.\n", "\n---\n\n# T\n\n## Question\n\nQ.\n\n## Answers\n\n### A\n\nText.\n")]
    public void AddAnswer_fills_an_empty_section_and_creates_a_missing_one(string rest, string expected) =>
        Assert.Equal(expected, AddAnswer(rest, "### A\n\nText."));

    [Theory]
    [InlineData("../../secret.md")]
    [InlineData("PBIs/pbi-1201/01-summary.md")]
    [InlineData("PBIs/pbi-1201/questions/../01-summary.md")]
    [InlineData("PBIs/pbi-1201/questions/q02-refunded-order.fr.md")]
    [InlineData("Questions/Questions.md")]
    [InlineData("PBIs/pbi-1201/questions/q09-missing.md")]
    public void QuestionPath_refuses_what_is_not_an_existing_question_file(string file)
    {
        Write("PBIs/pbi-1201/01-summary.md", "x");
        Write("PBIs/pbi-1201/questions/q02-refunded-order.fr.md", "x");
        Write("Questions/Questions.md", "x");

        Assert.Throws<ArgumentException>(() => QuestionPath(file));
    }

    [Fact]
    public void QuestionPath_gives_the_path_of_a_question_file()
    {
        var path = Write("PBIs/pbi-1201/questions/q02-refunded-order.md", Asked);

        Assert.Equal(path, QuestionPath("PBIs/pbi-1201/questions/q02-refunded-order.md"));
    }

    [Fact]
    public void QuestionDate_writes_a_local_time_with_its_offset()
    {
        var local = new DateTime(2026, 11, 24, 11, 40, 0, DateTimeKind.Local);

        Assert.Equal(new DateTimeOffset(local).ToString("yyyy-MM-ddTHH:mm:sszzz"), QuestionDate("2026-11-24T11:40"));
        Assert.Equal("2026-11-24T11:40:00+02:00", QuestionDate("2026-11-24T11:40:00+02:00"));
        Assert.True(DateTimeOffset.Now - DateTimeOffset.Parse(QuestionDate("")) < TimeSpan.FromMinutes(1));
        Assert.Throws<ArgumentException>(() => QuestionDate("next week"));
    }

    [Fact]
    public void NoHeading_keeps_headings_from_opening_a_section()
    {
        Assert.Equal("a\n\\# One\n\\## Two\n### Three\n#notaheading", NoHeading("a\r\n# One\n## Two\n### Three\n#notaheading", 2));
        Assert.Equal("\\### Three", NoHeading("### Three", 3));
    }
}
