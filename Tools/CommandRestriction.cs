namespace SharpAgent.Tools;

internal enum CommandShell { PowerShell, Cmd }

internal sealed record CommandRestriction(string Message, CommandShell? UnavailableShell = null)
{
    internal const string Prefix = "Ограничение Windows: ";
    private const string NextStep = " Работу с файлами можно продолжить встроенными инструментами.";

    internal static CommandRestriction LaunchDenied(CommandShell shell, int code)
    {
        var name = shell == CommandShell.Cmd ? "cmd" : "PowerShell";
        var reason = code switch
        {
            2 or 3 => $"файл оболочки {name} не найден.",
            5 => $"Windows отказала в запуске {name}.",
            740 => $"для запуска {name} требуется повышение прав.",
            1260 or 4551 => $"политика компьютера запретила запуск {name}.",
            577 => $"Windows не смогла проверить подпись файла {name}.",
            _ => $"не удалось запустить {name}.",
        };
        return new(reason + $" (Код Windows: {code}.)" + NextStep,
            code is 2 or 3 or 577 or 740 or 1260 or 4551 ? shell : null);
    }

    internal static CommandRestriction? FromOutput(CommandShell shell, string stdout, string stderr, string? scriptPath, int? exitCode)
    {
        if (shell == CommandShell.Cmd &&
            (CmdDisabled(stdout) || CmdDisabled(stderr)))
            return new("администратор отключил командную строку cmd." + NextStep, shell);

        var diagnostic = string.Join(" ", stderr.Split('\n').Select(line => line.Trim()));
        if (shell == CommandShell.PowerShell)
        {
            if (Has(diagnostic, "PSSecurityException", "running scripts is disabled", "выполнение сценариев отключено",
                "not digitally signed", "не имеет цифровой подписи", "ScriptContainedMaliciousContent"))
            {
                var headline = string.Concat(stderr.Split('\n').Select(line => line.Trim())
                    .TakeWhile(line => !line.StartsWith('+') && !line.StartsWith("At ", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("строка:", StringComparison.OrdinalIgnoreCase)).Take(8));
                bool wrapper = scriptPath != null && headline.Contains(Path.GetFileName(scriptPath), StringComparison.OrdinalIgnoreCase);
                return new("PowerShell запретил запуск скрипта." + NextStep, wrapper ? shell : null);
            }
            if (Has(diagnostic, "NotSupportedInConstrainedLanguage", "ConstrainedLanguageMode", "CannotCreateTypeConstrainedLanguage",
                "not supported in this language mode", "не поддерживается в этом языковом режиме"))
                return new("команда или скрипт требует возможностей, ограниченных в PowerShell." + NextStep);
        }
        if (exitCode is 1260 or 4551 || Has(diagnostic, "blocked by group policy", "заблокирована групповой политикой",
            "blocked by your system administrator", "заблокировано системным администратором"))
            return new("политика компьютера запретила одно из действий команды." + NextStep);
        return null;
    }

    private static bool CmdDisabled(string text) =>
        Has(text, "command prompt has been disabled by your administrator", "командная строка отключена вашим администратором",
            "командная строка была отключена администратором", "вызов командной строки отключен администратором");

    private static bool Has(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}

internal sealed class CommandRestrictedException(string result, CommandRestriction restriction) : Exception(result)
{
    internal CommandRestriction Restriction { get; } = restriction;
}
