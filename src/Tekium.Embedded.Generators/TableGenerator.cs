// Copyright (c) Tekium. Licensed under the MIT License.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Tekium.Embedded.Generators;

/// <summary>
/// For every class marked with [TkTable], generates the code that reads and writes its rows, so that the
/// engine never needs reflection: it is what keeps Native AOT working.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TableGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var tables = context.SyntaxProvider.ForAttributeWithMetadataName(
            TableParser.TableAttribute,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributed, cancellationToken) => TableParser.Parse(attributed, cancellationToken));

        context.RegisterSourceOutput(tables, static (output, table) =>
        {
            foreach (var diagnostic in table.Diagnostics)
            {
                if (Diagnostics.Find(diagnostic.Id) is { } descriptor)
                {
                    output.ReportDiagnostic(Diagnostic.Create(
                        descriptor, diagnostic.ToLocation(), diagnostic.Argument0, diagnostic.Argument1));
                }
            }

            // With errors there is nothing sensible to generate; the diagnostics say why.
            if (table.Diagnostics.Count == 0)
            {
                output.AddSource(table.HintName, SourceText.From(TableEmitter.Emit(table), Encoding.UTF8));
            }
        });
    }
}
