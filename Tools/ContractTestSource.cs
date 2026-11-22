namespace SharpAgent.Tools;

static partial class ContractTestSource
{
    internal sealed record Contract(string Assembly, string Source);

    internal static Contract Generate(JsonObject spec, ToolContext context)
    {
        var args = new ToolArgs(spec);
        var assembly = Library(args.Required("assembly"), context);
        var type = args.Required("type");
        var method = args.Required("method");
        var formats = args.Array("parameters").Select(Str).OfType<string>().ToArray();
        var returns = args.Required("returns");
        if (!TypeName().IsMatch(type) || !Identifier().IsMatch(method) || formats.Length != args.Array("parameters").Count ||
            formats.Length > 10 || formats.Append(returns).Any(value => !Primitive().IsMatch(value)))
            throw new ToolException("Укажите полное имя типа, публичный статический метод и типы int, long, double, bool или string с необязательным ?.");
        var cases = args.Array("cases");
        if (cases.Count is < 1 or > 40 || cases.Any(item => item is not JsonObject))
            throw new ToolException("Контракт должен содержать от одного до сорока случаев с args и expected или throws.");
        var source = new StringBuilder("""
            #nullable enable
            using System;
            using System.Reflection;
            var target = Assembly.LoadFrom(args[0]).GetType(
            """).Append(Literal(type)).Append(", true)!.GetMethod(").Append(Literal(method))
            .Append(", BindingFlags.Public | BindingFlags.Static, null, new Type[] { ")
            .Append(string.Join(", ", formats.Select(format => $"typeof({(format == "string?" ? "string" : format)})")))
            .AppendLine(" }, null) ?? throw new InvalidOperationException(\"Method not found\");")
            .AppendLine($"if (target.ReturnType != typeof({(returns == "string?" ? "string" : returns)})) throw new InvalidOperationException(\"Return type does not match the contract\");")
            .AppendLine("int failures = 0;")
            .AppendLine("void Check(int number, object?[] input, object? expected, string? exception) {")
            .AppendLine("    try { var actual = target.Invoke(null, input); if (exception == null && Equals(actual, expected)) return; }")
            .AppendLine("    catch (TargetInvocationException ex) { if (exception != null && ex.InnerException?.GetType().FullName == exception) return; }")
            .AppendLine("    failures++; Console.Error.WriteLine($\"Contract case {number} failed\");")
            .AppendLine("}");
        int number = 0;
        foreach (var item in cases.OfType<JsonObject>())
        {
            if (item["args"] is not JsonArray inputs || inputs.Count != formats.Length ||
                (item.ContainsKey("expected") == item.ContainsKey("throws")))
                throw new ToolException("В каждом случае нужны все входы и ровно одно поле expected или throws.");
            var exception = Str(item["throws"]);
            if (item.ContainsKey("throws") && (exception == null || !TypeName().IsMatch(exception)))
                throw new ToolException("Для throws нужно полное имя типа исключения.");
            source.Append("Check(").Append(++number).Append(", new object?[] { ")
                .Append(string.Join(", ", inputs.Select((input, i) => Value(input, formats[i])))).Append(" }, ")
                .Append(exception == null ? Value(item["expected"], returns) : "null").Append(", ")
                .Append(exception == null ? "null" : Literal(exception)).AppendLine(");");
        }
        source.AppendLine($"Console.WriteLine($\"Passed {{{number} - failures}}/{number}\");").AppendLine("return failures == 0 ? 0 : 1;");
        return new(assembly, source.ToString());
    }

    internal static string Library(string value, ToolContext context)
    {
        var path = PathHelper.FinalPath(context.Resolve(value));
        context.GuardSecrets(path);
        if (!PathHelper.IsInside(path, context.WorkingDirectory) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ToolException("Нужна существующая сборка DLL внутри рабочего проекта.");
        return path;
    }

    static string Literal(string value) => JsonSerializer.Serialize(value);

    static string Value(JsonNode? value, string format)
    {
        if (value == null && (format == "string" || format.EndsWith('?'))) return "null";
        if (value is JsonValue node)
            switch (format.TrimEnd('?'))
            {
                case "int" when node.TryGetValue<int>(out var i): return i.ToString(CultureInfo.InvariantCulture);
                case "long" when node.TryGetValue<long>(out var l): return l.ToString(CultureInfo.InvariantCulture) + "L";
                case "double" when node.TryGetValue<double>(out var d) && double.IsFinite(d): return d.ToString("R", CultureInfo.InvariantCulture) + "D";
                case "bool" when node.TryGetValue<bool>(out var b): return b ? "true" : "false";
                case "string" when node.TryGetValue<string>(out var s) && s.Length <= 2000: return Literal(s);
            }
        throw new ToolException("Значение в контракте не соответствует объявленному типу " + format + ".");
    }

    [GeneratedRegex(@"^[A-Za-z_]\w*(?:[.+][A-Za-z_]\w*)*$")]
    private static partial Regex TypeName();
    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex Identifier();
    [GeneratedRegex(@"^(?:int|long|double|bool|string)\??$")]
    private static partial Regex Primitive();
}
