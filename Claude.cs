// Claude sessions: the Windows Terminal tab that runs one, and the prompts it starts with.
public static class Claude
{
    // Opens a Claude session in a new Windows Terminal tab, in the workspace or in one of its folders, with the vault as an
    // extra working directory (--add-dir) since the session writes its files there. `arguments` is PowerShell text.
    public static void OpenClaudeTab(string title, string arguments, string? folder = null)
    {
        var directory = folder is null ? Workspace() : Path.GetFullPath(Path.Combine(Workspace(), folder));
        if (!directory.StartsWith(Workspace(), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory)) directory = Workspace();
        var terminal = new System.Diagnostics.ProcessStartInfo("wt.exe") { UseShellExecute = false };
        foreach (var argument in new[] { "-w", "0", "new-tab", "--title", title, "-d", directory, "pwsh", "-NoLogo", "-NoExit", "-Command",
                     $"claude --add-dir {PowerShellQuote(Vault)} {arguments}" })
            terminal.ArgumentList.Add(argument);
        System.Diagnostics.Process.Start(terminal);
        Console.WriteLine($"Claude session opened: {title}");
    }

    // Prompts of the Claude sessions, always in English. The page language only picks the language Claude answers in and
    // adds a French copy of the report: the English report always exists (<name>.md), the French one is <name>.fr.md.
    // No ';' in them: Windows Terminal reads it as a command separator.
    public static string ReviewPrompt(int pullRequestId, string sessionId, string language) =>
        $"Review PR {pullRequestId} on Azure DevOps (organization {OrganizationName}, project {Project}), in the format of the reports " +
        $"already in {ReviewDirectory()}. Do not post any comment and do not vote on the PR, the decision is mine. " +
        ReportInstruction(Path.Combine(ReviewDirectory(), $"{pullRequestId}-review"), sessionId, language);

    public static string ThreadsPrompt(int pullRequestId, string sessionId, string language) =>
        $"Analyze the unresolved comment threads of PR {pullRequestId} on Azure DevOps (organization {OrganizationName}, project {Project}). " +
        "For each active or pending thread, sum up the request, check in the code whether it is founded, then propose a " +
        "reply or a fix. End with a table: thread, request, verdict, proposed action. Do not post any reply, resolve any " +
        "thread or change any code before I approve. " +
        ReportInstruction(Path.Combine(ReviewDirectory(), $"{pullRequestId}-threads"), sessionId, language);

    // Review of any PBI, typed in the page: the work done for it, through its pull requests, against what it asks.
    public static string PbiReviewPrompt(int workItemId, string sessionId, string language) =>
        $"Review PBI #{workItemId} on Azure DevOps (organization {OrganizationName}, project {Project}): read the PBI, its acceptance " +
        "criteria and its comments, find its linked pull requests, and review their changes against what the PBI asks: " +
        "acceptance criteria covered or not, correctness, tests, and the conventions of the reports already in " +
        $"{ReviewDirectory()}. If the PBI has no pull request, say so and review what can be checked. Do not post any comment, do not " +
        "vote and do not change the PBI, the decision is mine. " +
        PbiFilesInstruction(workItemId, sessionId, language,
            "update 00-status.md, write the review in 04-ai-review.md and add what needs an answer as question files (rule Questions of CLAUDE.md)");

    public static string PbiPrompt(int workItemId, string sessionId, string language) =>
        $"Start working on PBI #{workItemId} on Azure DevOps (organization {OrganizationName}, project {Project}): read the PBI, its " +
        "acceptance criteria and its comments, sum up what is asked, list the open questions and propose a plan. Do not " +
        "write code before I approve the plan. " +
        PbiFilesInstruction(workItemId, sessionId, language,
            "create 00-status.md first, then write 01-summary.md, 02-analysis.md with the plan, and the questions if " +
            "there are some, one file each with their index 06-open-questions.md (rule Questions of CLAUDE.md). Later steps go to 03-job-done.md, 04-ai-review.md, 05-test-instructions.md and " +
            "07-pr-description.md");

    // One step of a PBI done again from its tab: the step is the working file to write, StepTasks says what it takes.
    public static string StepPrompt(int workItemId, string step, string sessionId, string language) =>
        $"Work on the step {step} of PBI #{workItemId} on Azure DevOps (organization {OrganizationName}, project {Project}): read the " +
        $"PBI and the working files already in {PbiDirectory(workItemId)}, then {StepTasks[step]} " +
        PbiFilesInstruction(workItemId, sessionId, language, $"write {step}.md and update 00-status.md");

    // The French copy of a working file or of a report that only exists in English.
    public static string TranslatePrompt(string directory, string name, string sessionId) =>
        $"Write the French copy of {Path.Combine(directory, name + ".md")} next to it, as {name}.fr.md: same " +
        $"structure, translated to French, with the same frontmatter where this session ({sessionId}) is added. Do not change " +
        "the English file or any other file. Answer me in French.";

    // Where a PBI session writes: the "PBI working files" rule of the workspace CLAUDE.md, in <vault>/PBIs/pbi-<id>/.
    public static string PbiFilesInstruction(int workItemId, string sessionId, string language, string steps) =>
        $"Follow the PBI working files rule of CLAUDE.md in {PbiDirectory(workItemId)} (this session is {sessionId}, name it " +
        $"in the frontmatter of every file you write): {steps}. If these files already exist, read them first and complete " +
        "them without erasing their history. " +
        (language == "fr"
            ? "Write the French copy (.fr.md) of each working file next to it (a question file has none, only the index 06-open-questions.md). Answer me in French."
            : "Answer me in English.");

    // A report starts with the same YAML frontmatter as the PBI working files (rule in the workspace CLAUDE.md).
    public static string ReportInstruction(string report, string sessionId, string language) =>
        $"Start the report with a YAML frontmatter: updated (ISO 8601 with offset), session ({sessionId}, this session) and " +
        "sessions (every session that wrote it, oldest first). If the report already exists, read it first and complete it. " +
        "Under its title, link the PBI it is about and list the report in that PBI's hub note, as the vault rules of CLAUDE.md say. " +
        (language == "fr"
            ? $"Write the report in English in {report}.md and its French translation, same structure, in {report}.fr.md. Answer me in French."
            : $"Write the report in English in {report}.md. Answer me in English.");

    public static string Workspace() => Settings.Workspace ?? Path.GetFullPath(Path.Combine(SourceDirectory(), ".."));

    public static string PowerShellQuote(string text) => "'" + text.Replace("'", "''") + "'";

    public static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

// Prompt texts that are data: what doing each step of a PBI again takes (the steps a tab of the PBI page can start),
// for StepPrompt. No ';' in them.
public static class Prompts
{
    // Where a question can be asked and an answer can come from (an answer can also be my own "decision").
    public static readonly string[] QuestionChannels = ["ado", "teams", "email", "kb", "call", "other"];

    public static readonly Dictionary<string, string> StepTasks = new()
    {
        ["01-summary"] = "sum up what the PBI asks: goal, acceptance criteria, scope and what is out of scope.",
        ["02-analysis"] = "analyze what has to change and how: code areas, contract, options with their trade-offs, and the plan. Do not write code.",
        ["03-job-done"] = "carry out the plan of 02-analysis.md: tell me what you are about to change and wait for my approval before writing code, then record the work done.",
        ["04-ai-review"] = "review the changes of its pull requests against what the PBI asks, in the format of the reports of the vault. Do not post any comment and do not vote.",
        ["05-test-instructions"] = "write how to test it: endpoints with sample payloads, which account, environment and expected results.",
        ["06-open-questions"] = "write the questions for the PO or the team as the Questions rule of CLAUDE.md says: one file per question in its questions folder, as drafts, and their lines in the index 06-open-questions.md. Keep the questions already there with their state and answers. Do not post them.",
        ["07-pr-description"] = "write a pull request description ready to paste in Azure DevOps.",
    };
}
