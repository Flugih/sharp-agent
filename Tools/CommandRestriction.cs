namespace SharpAgent.Tools;

internal enum CommandShell { PowerShell, Cmd }

internal sealed record CommandRestriction(string Message, CommandShell? UnavailableShell = null)
{
    internal const string Prefix = "Ограничение Windows: ";
    const string NextStep = " Работу с файлами можно продолжить встроенными инструментами.";
    static readonly string[] CmdDisabled = ["command prompt has been disabled by your administrator",
        "командная строка отключена вашим администратором", "командная строка была отключена администратором",
        "вызов командной строки отключен администратором"];

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
        return new(reason + $" (Код Windows: {code}.)" + NextStep, code is 2 or 3 or 577 or 740 or 1260 or 4551 ? shell : null);
    }

    internal static CommandRestriction? FromOutput(CommandShell shell, string stdout, string stderr, string? scriptPath, int? exitCode)
    {
        if (shell == CommandShell.Cmd && (Has(stdout, CmdDisabled) || Has(stderr, CmdDisabled)))
            return new("администратор отключил командную строку cmd." + NextStep, shell);
        var lines = stderr.Split('\n').Select(line => line.Trim());
        var diagnostic = string.Join(" ", lines);
        if (shell == CommandShell.PowerShell)
        {
            if (Has(diagnostic, "PSSecurityException", "running scripts is disabled", "выполнение сценариев отключено",
                "not digitally signed", "не имеет цифровой подписи", "ScriptContainedMaliciousContent"))
            {
                var headline = string.Concat(lines.TakeWhile(line => !line.StartsWith('+') &&
                    !line.StartsWith("At ", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("строка:", StringComparison.OrdinalIgnoreCase)).Take(8));
                bool wrapper = scriptPath != null && headline.Contains(Path.GetFileName(scriptPath), StringComparison.OrdinalIgnoreCase);
                return new("PowerShell запретил запуск скрипта." + NextStep, wrapper ? shell : null);
            }
            if (Has(diagnostic, "NotSupportedInConstrainedLanguage", "ConstrainedLanguageMode", "CannotCreateTypeConstrainedLanguage",
                "not supported in this language mode", "не поддерживается в этом языковом режиме"))
                return new("команда или скрипт требует возможностей, ограниченных в PowerShell." + NextStep);
        }
        return exitCode is 1260 or 4551 || Has(diagnostic, "blocked by group policy", "заблокирована групповой политикой",
            "blocked by your system administrator", "заблокировано системным администратором")
            ? new("политика компьютера запретила одно из действий команды." + NextStep) : null;
    }

    internal static string? RipgrepLaunchFailure(string stderr, CommandRestriction? restriction)
    {
        if (restriction?.UnavailableShell != null) return null;
        if (Has(stderr, "not a valid Win32 application", "не является приложением Win32"))
            return "Встроенный rg.exe несовместим с этой Windows.";
        var diagnostic = string.Join(" ", stderr.Split('\n').TakeWhile(line => !line.TrimStart().StartsWith('+')));
        if (restriction != null && Has(diagnostic, "blocked by group policy", "заблокирована групповой политикой",
                "blocked by your system administrator", "заблокировано системным администратором"))
            return "Политика Windows ограничила запуск rg.exe.";
        if (!Has(diagnostic, "rg.exe") || !Has(diagnostic, "failed to run", "could not be started", "не удалось запустить",
                "не удалось выполнить", "заблокирован", "blocked", "restricted"))
            return null;
        return restriction == null ? "Windows не смогла запустить rg.exe." :
            "Политика Windows ограничила запуск rg.exe.";
    }

    static bool Has(string text, params string[] values) => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}

internal sealed class CommandRestrictedException(string result, CommandRestriction restriction) : Exception(result)
{
    internal CommandRestriction Restriction => restriction;
}
