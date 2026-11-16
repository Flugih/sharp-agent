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
    foreach (var path in Directory.EnumerateFiles(directory, "*.source"))
    {
        var text = File.ReadAllText(path);
        var tree = CSharpSyntaxTree.ParseText(text);
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
            symbols.Add(new
            {
                Name = identifier.ValueText,
                Start = start + 1,
                End = (node is BaseTypeDeclarationSyntax ? start : span.EndLinePosition.Line) + 1,
                Signature = string.Join(" ", text.Substring(node.SpanStart, Math.Min(end - node.SpanStart, 320))
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)),
                Dependencies = node.DescendantNodes(descendIntoChildren: c => c == node || c is not MemberDeclarationSyntax)
                    .OfType<IdentifierNameSyntax>().Select(r => r.Identifier.ValueText).Distinct().Take(40).ToArray(),
            });
        }
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            Symbols = symbols,
            Diagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(15)
                .Select(d => (d.Location.GetLineSpan().StartLinePosition.Line + 1) + ": " + d.Id + " " + d.GetMessage()),
        }));
    }
}
