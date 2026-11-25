using System.Text.Json.Nodes;
using Xunit;
using static Search;

// The search in the vault's markdown files, and the pieces of text it shows. The search in Azure DevOps has no test.
[Collection("vault")]
public sealed class SearchTests : IDisposable
{
    readonly string vault = Path.Combine(Path.GetTempPath(), "dashboard-tests-" + Guid.NewGuid().ToString("N"));

    public SearchTests()
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

    void Write(string relativePath, string content)
    {
        var path = Path.Combine(vault, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    static string[] Files(JsonArray results) => [.. results.Select(result => (string)result!["file"]!)];

    [Fact]
    public void SearchWords_are_lower_case_distinct_and_without_the_sign_of_a_number() =>
        Assert.Equal(["invoice", "pdf", "1201", "4215"], SearchWords("  Invoice PDF invoice #1201\t!4215 "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  # ! ")]
    public void SearchWords_of_nothing_is_empty(string? query) => Assert.Empty(SearchWords(query));

    [Fact]
    public void SearchVault_finds_the_files_that_hold_every_word()
    {
        Write("Knowledge/PDF rendering.md", "---\nupdated: 2026-11-18T10:00:00+01:00\nsession: abc\n---\n\n# PDF rendering\n\nAn **invoice** PDF embeds a subset of the font.\n\nNothing here.\n\nTotals of the invoice come from `OrderTotals`.\n");
        Write("PBIs/pbi-1201/03-job-done.md", "# Job done\n\nInvoiceRenderer builds the PDF.\n");
        Write("PBIs/pbi-1201/01-summary.md", "# Summary\n\nOnly the invoice, no file format.\n");
        Write("PBIs/pbi-1201/03-job-done.fr.md", "# Travail fait\n\nInvoice PDF.\n");
        Write(".obsidian/workspace.md", "invoice pdf");

        var results = SearchVault(["invoice", "pdf"]);

        // A French copy and Obsidian's own folder are left out; the file whose title holds a word... comes after the
        // one whose title and path hold them all.
        Assert.Equal(["Knowledge/PDF rendering.md", "PBIs/pbi-1201/03-job-done.md"], Files(results));
        var first = results[0]!;
        Assert.Equal("PDF rendering", (string?)first["title"]);
        Assert.Equal("Knowledge", (string?)first["folder"]);
        Assert.Null(first["pbi"]);
        Assert.Equal("2026-11-18T10:00:00+01:00", (string?)first["updated"]);
        // The lines that hold a word, as plain text; the title and the frontmatter are not passages.
        Assert.Equal(2, (int?)first["count"]);
        Assert.Equal(["An invoice PDF embeds a subset of the font.", "Totals of the invoice come from OrderTotals."],
            first["passages"]!.AsArray().Select(passage => (string)passage!));
        Assert.Equal(1201, (int?)results[1]!["pbi"]);
        Assert.Null(results[1]!["question"]);
    }

    [Fact]
    public void SearchVault_orders_by_title_then_passages_then_last_change()
    {
        Write("History/a.md", "---\nupdated: 2026-11-01\n---\n\n# Something else\n\ninvoice\n");
        Write("History/b.md", "---\nupdated: 2026-11-20\n---\n\n# Something else\n\ninvoice\n");
        Write("History/c.md", "---\nupdated: 2026-10-01\n---\n\n# Something else\n\ninvoice one\n\ninvoice two\n");
        Write("History/d.md", "---\nupdated: 2026-09-01\n---\n\n# The invoice\n\nNo other line.\n");

        Assert.Equal(["History/d.md", "History/c.md", "History/b.md", "History/a.md"], Files(SearchVault(["invoice"])));
    }

    [Fact]
    public void SearchVault_says_the_state_of_a_question_and_finds_a_file_by_its_path()
    {
        Write("PBIs/pbi-1201/questions/q03-refunded-order.md", "---\nupdated: 2026-11-17\nstate: waiting          # draft | waiting\nto: Eve\n---\n\n# Q3 - Should a refunded order keep its invoice?\n\n## Question\n\nOptions.\n");
        Write("Questions/q-2026-11-24-numbering.md", "---\nupdated: 2026-11-24\n---\n\n# Who owns the numbering?\n");

        var question = Assert.Single(SearchVault(["refunded", "q03"]))!;
        Assert.Equal("waiting", (string?)question["question"]!["state"]);
        Assert.Equal("Eve", (string?)question["question"]!["to"]);
        Assert.Equal(1201, (int?)question["pbi"]);

        var draft = Assert.Single(SearchVault(["numbering"]))!;
        Assert.Equal("draft", (string?)draft["question"]!["state"]);
        Assert.Null(draft["question"]!["to"]);
    }

    [Fact]
    public void SearchVault_does_not_look_into_the_frontmatter()
    {
        Write("Knowledge/PDF rendering.md", "---\nupdated: 2026-11-18\nsession: abc\ntags: [invoice]\n---\n\n# PDF rendering\n\nFonts.\n");
        Write("Knowledge/Sessions.md", "# Sessions\n\nA session writes its id in the frontmatter.\n");

        Assert.Equal(["Knowledge/Sessions.md"], Files(SearchVault(["session"])));
        Assert.Empty(SearchVault(["invoice"]));
    }

    [Fact]
    public void SearchVault_finds_nothing_without_words_or_matches()
    {
        Write("Knowledge/PDF rendering.md", "# PDF rendering\n");

        Assert.Empty(SearchVault([]));
        Assert.Empty(SearchVault(["absent"]));
    }

    [Fact]
    public void WiqlTerms_cut_the_words_at_punctuation_and_keep_short_ones_whole() =>
        Assert.Equal(["rows*", "create*", "v2", "public*", "mourad*", "248949*"], WiqlTerms(["rows[0]", "(create)", "/v2/public", "mourad's", "a", "248949", "rows"]));

    [Fact]
    public void WorkItemsFilter_leaves_closed_items_out_except_the_one_whose_id_was_typed()
    {
        const string fields = "[System.Title] CONTAINS WORDS '{0}' OR [System.Description] CONTAINS WORDS '{0}' OR [Microsoft.VSTS.Common.AcceptanceCriteria] CONTAINS WORDS '{0}' OR [System.History] CONTAINS WORDS '{0}'";
        const string open = " AND [System.State] NOT IN ('Done', 'Removed', 'Closed')";
        var invoice = "(" + string.Format(fields, "invoice*") + ")";
        var number = "(" + string.Format(fields, "1201*") + ")";

        Assert.Equal(invoice + open, WorkItemsFilter(["invoice"], closed: false));
        Assert.Equal(invoice, WorkItemsFilter(["invoice"], closed: true));
        // A typed number is looked for as text, under the state filter, and as an id, outside it.
        Assert.Equal($"(({invoice} AND {number}{open}) OR [System.Id] IN (1201))", WorkItemsFilter(["invoice", "1201"], closed: false));
        // Nothing the index can look for: a lone character, or only punctuation.
        Assert.Null(WorkItemsFilter(["a", "[]"], closed: false));
        Assert.Equal("[System.Id] IN (7)", WorkItemsFilter(["7"], closed: false));
    }

    [Fact]
    public void PlainText_drops_html_and_markdown()
    {
        Assert.Equal("Keep the invoice & the PDF", PlainText("<div>Keep the <b>invoice</b> &amp; the&nbsp;PDF</div>"));
        Assert.Equal("see Status and the audit, code", PlainText("- see [[PBIs/pbi-1/00-status|Status]] and [the audit](../audit.md), `code`"));
        Assert.Equal("", PlainText(null));
    }

    [Fact]
    public void Passage_keeps_a_short_text_and_cuts_a_long_one_around_the_word()
    {
        Assert.Equal("A short text about an invoice.", Passage("A short text about an invoice.", ["invoice"]));

        var text = string.Join(' ', Enumerable.Repeat("before", 40)) + " the INVOICE itself " + string.Join(' ', Enumerable.Repeat("after", 60));
        var passage = Passage(text, ["invoice"]);

        Assert.StartsWith("…before", passage);
        Assert.EndsWith("after…", passage);
        Assert.Contains("the INVOICE itself", passage);
        Assert.InRange(passage.Length, 150, 202);

        // A word near the start: nothing was cut before it.
        Assert.StartsWith("invoice first", Passage("invoice first " + string.Join(' ', Enumerable.Repeat("after", 60)), ["invoice"]));
    }
}
