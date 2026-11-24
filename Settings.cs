// Settings of this machine, from dashboard.settings.json next to the project (not committed; see
// dashboard.settings.example.json): the Azure DevOps organization URL, project and team (whose sprint backlog gives
// the PBIs' order), the vault folder of the PBI working files and reviews, the environment variable holding the PAT,
// and optionally the workspace (the folder of the checkouts, the parent of dashboard\ by default).
public static class Settings
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
