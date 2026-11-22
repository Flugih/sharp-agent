using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(args[0], name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
IndexFiles(args[1]);
[MethodImpl(MethodImplOptions.NoInlining)]
static void IndexFiles(string directory)
{
    var files = JsonSerializer.Deserialize<Input[]>(File.ReadAllText(Path.Combine(directory, "index.json")));
    var trees = files.ToDictionary(file => file.File, file =>
    {
        var path = Path.Combine(directory, file.File);
        return CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
    });
    var groups = files.GroupBy(file => file.Project).ToDictionary(group => group.Key, group => group.ToArray());
    var built = new Dictionary<string, CSharpCompilation>();
    var building = new HashSet<string>();
    var runtime = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    CSharpCompilation Build(string project)
    {
        if (built.TryGetValue(project, out var ready)) return ready;
        building.Add(project);
        var group = groups[project];
        var refs = new List<MetadataReference> { runtime };
        foreach (var reference in group[0].References)
            if (groups.ContainsKey(reference) && !building.Contains(reference)) refs.Add(Build(reference).ToMetadataReference());
        building.Remove(project);
        return built[project] = CSharpCompilation.Create(project, group.Select(file => trees[file.File]), refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
    string Identity(ISymbol symbol)
    {
        var declaration = symbol?.OriginalDefinition.GetDocumentationCommentId();
        var assembly = symbol?.ContainingAssembly?.Name;
        return declaration != null && assembly != null && groups.ContainsKey(assembly) ? assembly + "|" + declaration : null;
    }
    foreach (var file in files.Where(file => file.Emit))
    {
        var tree = trees[file.File];
        var text = tree.GetText().ToString();
        var semantic = Build(file.Project).GetSemanticModel(tree);
        var symbols = new List<object>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var identifier = node switch
            {
                MethodDeclarationSyntax m => m.Identifier,
                BaseTypeDeclarationSyntax t => t.Identifier,
                BaseMethodDeclarationSyntax b => b.ChildTokens().FirstOrDefault(k => k.IsKind(SyntaxKind.IdentifierToken)),
                LocalFunctionStatementSyntax l => l.Identifier,
                PropertyDeclarationSyntax p => p.Identifier,
                DelegateDeclarationSyntax d => d.Identifier,
                _ => default,
            };
            if (identifier.RawKind == 0 || identifier.IsMissing || symbols.Count == 800) continue;
            var body = node.ChildNodes().FirstOrDefault(c => c is BlockSyntax or ArrowExpressionClauseSyntax or AccessorListSyntax);
            int end = node is TypeDeclarationSyntax { OpenBraceToken: { IsMissing: false, RawKind: not 0 } brace } ? brace.SpanStart
                : body?.SpanStart ?? node.Span.End;
            var span = node.GetLocation().GetLineSpan();
            int start = span.StartLinePosition.Line;
            var relations = node.DescendantNodes(descendIntoChildren: c => c == node || c is not MemberDeclarationSyntax)
                .OfType<SimpleNameSyntax>().Take(160).Select(name => (name.Identifier.ValueText, Symbol: semantic.GetSymbolInfo(name).Symbol)).ToArray();
            symbols.Add(new
            {
                Name = identifier.ValueText,
                Start = start + 1,
                End = (node is BaseTypeDeclarationSyntax ? start : span.EndLinePosition.Line) + 1,
                Signature = string.Join(" ", text.Substring(node.SpanStart, Math.Min(end - node.SpanStart, 320))
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)),
                Id = Identity(semantic.GetDeclaredSymbol(node)),
                References = relations.Select(r => Identity(r.Symbol)).Where(id => id != null).Distinct().Take(40),
                Test = node is MethodDeclarationSyntax method && method.AttributeLists.SelectMany(list => list.Attributes)
                    .Any(attribute => IsTest(attribute.Name.ToString())),
                Dependencies = relations.Select(r => r.Symbol?.Name ?? r.ValueText)
                    .Where(name => name != identifier.ValueText).Distinct().Take(40).ToArray(),
            });
        }
        File.WriteAllText(Path.ChangeExtension(tree.FilePath, ".json"), JsonSerializer.Serialize(new
        {
            Symbols = symbols,
            Imports = tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.Name?.ToString())
                .Where(name => name != null).Distinct().Take(40),
            Diagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(15)
                .Select(d => (d.Location.GetLineSpan().StartLinePosition.Line + 1) + ": " + d.Id + " " + d.GetMessage()),
        }));
    }
}

static bool IsTest(string attribute)
{
    var name = attribute.Split('.').Last();
    return name.EndsWith("Attribute", StringComparison.Ordinal) ? IsTest(name[..^9]) :
        name is "Fact" or "Theory" or "Test" or "TestCase" or "TestMethod" or "DataTestMethod";
}

sealed record Input(string File, string Project, string[] References, bool Emit);
