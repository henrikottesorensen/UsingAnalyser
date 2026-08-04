using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace UsingAnalyser;

/// <summary>
/// Enforces a using layout that neither the built-in options nor StyleCop can express: System, then
/// third party, then the solution's own namespaces, as three alphabetical blocks separated by a
/// blank line. <c>dotnet_separate_import_directive_groups</c> gets close but groups by first-level
/// namespace, which splits third party into one block per vendor and cannot tell a vendor from you;
/// StyleCop's SA1208/SA1210 know only "System first, then alphabetical" and have no notion of a
/// blank-line block at all.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UsingLayoutAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The directives are in the wrong order.</summary>
    public const string OrderDiagnosticId = "UA1000";

    /// <summary>The directives are in the right order but the blank lines between them are not.</summary>
    public const string SeparationDiagnosticId = "UA1001";

    private static readonly DiagnosticDescriptor OrderRule = new(
        OrderDiagnosticId,
        title: "Using directives are out of order",
        messageFormat: "'{0}' should appear after '{1}'",
        category: "Ordering",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: $"Set {UsingLayoutOptions.FirstPartyPrefixesKey} in .editorconfig to the namespace roots that belong to this solution.");

    private static readonly DiagnosticDescriptor SeparationRule = new(
        SeparationDiagnosticId,
        title: "Using blocks are not separated by a blank line",
        messageFormat: "'{0}' {1} be preceded by a blank line",
        category: "Ordering",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: $"Set {UsingLayoutOptions.FirstPartyPrefixesKey} in .editorconfig to the namespace roots that belong to this solution.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(OrderRule, SeparationRule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        // A syntax tree action, not a semantic one: classification is a question about the text of a
        // namespace, so the analyser never needs a compilation and never waits for one.
        context.RegisterSyntaxTreeAction(Analyze);
    }

    private static void Analyze(SyntaxTreeAnalysisContext context)
    {
        if (context.Tree.GetRoot(context.CancellationToken) is not CompilationUnitSyntax root)
        {
            return;
        }

        var options = UsingLayoutOptions.Read(context.Options.AnalyzerConfigOptionsProvider.GetOptions(context.Tree));

        // Usings written inside a namespace are a block in their own right - that is the placement
        // StyleCop's SA1200 asks for by default, so it is the shape a stranger is most likely to
        // arrive with.
        foreach (var container in UsingLayout.Containers(root))
        {
            AnalyzeContainer(context, container, options);
        }
    }

    private static void AnalyzeContainer(SyntaxTreeAnalysisContext context, SyntaxNode container, UsingLayoutOptions fileOptions)
    {
        // With no prefixes configured, the container's own namespace says which code is the
        // solution's. Per container rather than per file, so usings inside a namespace are judged
        // against that namespace.
        var options = fileOptions.WithInferredFirstParty(UsingLayout.DeclaredRoot(container));

        var usings = UsingLayout.Relevant(container);
        if (usings.Length < 2)
        {
            return;
        }

        // A #if around a using makes its position meaningful rather than cosmetic, and moving it
        // across the boundary would change which usings the compiler sees. Layout is not worth
        // that, so a block that conditions its usings is left alone entirely.
        if (usings.Any(directive => directive.ContainsDirectives))
        {
            return;
        }

        var ordered = UsingLayout.Order(usings, options);

        // At most one diagnostic per block, because the fix rewrites the block in one edit. Per
        // block rather than per file, so that a file with usings in two containers reports both and
        // Fix All settles it in a single pass.
        for (var index = 0; index < usings.Length; index++)
        {
            if (usings[index] != ordered[index])
            {
                // Reported on the directive sitting in the wrong place, naming the one that belongs
                // there. Everything above this point already matches, so the directive that belongs
                // here is further down the file, which makes "should appear after" true every time.
                context.ReportDiagnostic(Diagnostic.Create(
                    OrderRule,
                    usings[index].GetLocation(),
                    Describe(usings[index]),
                    Describe(ordered[index])));
                return;
            }
        }

        for (var index = 1; index < usings.Length; index++)
        {
            var wanted = UsingLayout.NeedsSeparation(usings[index - 1], usings[index], options);
            if (wanted != UsingLayout.HasSeparation(usings[index - 1], usings[index]))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    SeparationRule,
                    usings[index].GetLocation(),
                    Describe(usings[index]),
                    wanted ? "should" : "should not"));
                return;
            }
        }
    }

    /// <summary>What to call a directive in a message: what the reader sees after <c>using</c>.</summary>
    private static string Describe(UsingDirectiveSyntax directive) =>
        directive.Alias?.Name.ToString() ?? directive.NamespaceOrType?.ToString() ?? directive.ToString();
}
