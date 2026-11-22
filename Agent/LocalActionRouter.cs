namespace SharpAgent.Agent;

static partial class LocalActionRouter
{
    internal sealed record DiagnosticIntent(bool Repair, bool TestsOnly, string? Code);

    internal static DiagnosticIntent? Diagnosis(string text)
    {
        var match = DiagnosticRequest().Match(text.Trim().TrimEnd('.', '!', '?'));
        return !match.Success ? null : new(match.Groups["repair"].Success, match.Groups["tests"].Success,
            match.Groups["code"] is { Success: true } code ? code.Value.ToUpperInvariant() : null);
    }

    internal static string? Match(string text) => text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant() switch
    {
        "git status" or "покажи git status" or "покажи статус git" or "show git status" => "git status",
        "git diff" or "покажи git diff" or "покажи изменения git" or "show git diff" => "git diff",
        "git diff --staged" or "покажи git diff --staged" => "git diff --staged",
        "dotnet build" or "запусти dotnet build" or "run dotnet build" => "dotnet build",
        "dotnet test" or "запусти dotnet test" or "run dotnet test" => "dotnet test",
        "проверь сборку и тесты" or "запусти сборку и тесты" or "build and test" or "проверь проект" => "project verify",
        "запусти тесты проекта" or "run project tests" => "project test",
        "покажи сценарии" or "list local scripts" => "script list",
        "покажи базу знаний" or "export local knowledge" => "knowledge export",
        "какая рабочая папка" or "покажи рабочую папку" or "pwd" => "pwd",
        "который час" or "сколько сейчас времени" or "what time is it" => "time",
        var raw => Named(raw),
    };

    static string? Named(string text)
    {
        if (Regex.Match(text, @"^(?:примени решение|apply solution) (?<id>[0-9a-f]{32})$") is { Success: true } patch)
            return "knowledge apply " + patch.Groups["id"].Value;
        var match = Regex.Match(text, @"^(?<action>запусти сценарий|run local script|повтори проверки|run test suite) (?<id>[a-z0-9_-]{1,64})$");
        return !match.Success ? null : (match.Groups["action"].Value is "запусти сценарий" or "run local script" ? "script run " : "suite run ") +
            match.Groups["id"].Value;
    }

    [GeneratedRegex(@"^(?:(?<repair>исправь (?:ошибк[иу] сборки|сборку и тесты|(?<tests>падающие тесты))|fix (?:the build|(?<tests>failing tests)))|диагностируй (?:проект|сборку|(?<tests>тесты))|найди причину (?:ошибки сборки|(?<tests>падения тестов))|diagnose (?:the project|the build|(?<tests>failing tests)))(?:\s+(?<code>[A-Za-z]{1,10}\d{2,8}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticRequest();
}
