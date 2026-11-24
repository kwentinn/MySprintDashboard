using System.Text.Json.Nodes;
using Xunit;
using static VaultStore;

// The markdown file the reader of the page asks for: a file of the vault, and nothing outside it.
[Collection("vault")]
public sealed class VaultFileTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "dashboard-tests-" + Guid.NewGuid().ToString("N"));
    readonly string vault;

    public VaultFileTests()
    {
        vault = Path.Combine(root, "vault");
        Write("vault/PBIs/pbi-1201/03-job-done.md", "# Job done");
        Write("vault/Knowledge/Status codes.md", "# Status codes");
        Write("vault/Knowledge/notes.txt", "not markdown");
        Write("secret.md", "# Outside the vault");
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, new JsonObject
        {
            ["organization"] = "https://dev.azure.com/acme", ["project"] = "Shop", ["team"] = "Shop Team", ["vault"] = vault,
        }.ToJsonString());
        Assert.Null(Settings.Load(settings));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    void Write(string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Theory]
    [InlineData("PBIs/pbi-1201/03-job-done.md")]
    [InlineData("/PBIs/pbi-1201/03-job-done.md")]
    [InlineData("PBIs/pbi-1201/questions/../03-job-done.md")]
    public void A_file_of_the_vault_comes_with_its_path_and_text(string path)
    {
        var file = VaultFile(path)!;

        Assert.Equal("PBIs/pbi-1201/03-job-done.md", (string?)file["file"]);
        Assert.Equal("# Job done", (string?)file["markdown"]);
    }

    [Fact]
    public void A_bare_file_name_is_looked_for_in_the_whole_vault() =>
        Assert.Equal("Knowledge/Status codes.md", (string?)VaultFile("/Status codes.md")!["file"]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../secret.md")]
    [InlineData("PBIs/../../secret.md")]
    [InlineData("secret.md")]
    [InlineData("Knowledge/notes.txt")]
    [InlineData("PBIs/pbi-1201/missing.md")]
    public void Anything_else_is_refused(string? path) => Assert.Null(VaultFile(path));

    [Fact]
    public void An_absolute_path_outside_the_vault_is_refused() => Assert.Null(VaultFile(Path.Combine(root, "secret.md")));
}
