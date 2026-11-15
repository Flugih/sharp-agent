using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class CodeIndexWorker
{
    private static void Main(string[] args)
    {
        var roslyn = args[0];
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(roslyn, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        IndexFiles(args[1]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IndexFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.source"))
        {
            var text = File.ReadAllText(path);
            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetRoot();
            var symbols = new List<object>();
            foreach (var node in root.DescendantNodes())
            {
                var identifier = node switch
                {
                    BaseTypeDeclarationSyntax type => type.Identifier,
                    BaseMethodDeclarationSyntax method => method.ChildTokens().FirstOrDefault(token => token.IsKind(SyntaxKind.IdentifierToken)),
                    LocalFunctionStatementSyntax local => local.Identifier,
                    PropertyDeclarationSyntax property => property.Identifier,
                    DelegateDeclarationSyntax declaration => declaration.Identifier,
                    _ => default,
                };
                if (node is MethodDeclarationSyntax namedMethod) identifier = namedMethod.Identifier;
                if (identifier.RawKind == 0 || identifier.IsMissing || symbols.Count == 800) continue;
                var body = node.ChildNodes().FirstOrDefault(child =>
                    child is BlockSyntax or ArrowExpressionClauseSyntax or AccessorListSyntax);
                int end = node is TypeDeclarationSyntax typeDeclaration && !typeDeclaration.OpenBraceToken.IsMissing &&
                    typeDeclaration.OpenBraceToken.RawKind != 0 ? typeDeclaration.OpenBraceToken.SpanStart :
                    body == null ? node.Span.End : body.SpanStart;
                int start = node.GetLocation().GetLineSpan().StartLinePosition.Line;
                int last = node.GetLocation().GetLineSpan().EndLinePosition.Line;
                var signature = string.Join(" ", text.Substring(node.SpanStart, Math.Min(end - node.SpanStart, 320))
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                symbols.Add(new
                {
                    Name = identifier.ValueText,
                    Start = start + 1,
                    End = (node is BaseTypeDeclarationSyntax ? start : last) + 1,
                    Signature = signature,
                    Dependencies = node.DescendantNodes(descendIntoChildren: child => child == node || child is not MemberDeclarationSyntax)
                        .OfType<IdentifierNameSyntax>().Select(reference => reference.Identifier.ValueText).Distinct().Take(40).ToArray(),
                });
            }
            var diagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(15)
                .Select(d => (d.Location.GetLineSpan().StartLinePosition.Line + 1) + ": " + d.Id + " " + d.GetMessage());
            File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
            {
                Symbols = symbols,
                Diagnostics = diagnostics,
            }));
        }
    }
}
