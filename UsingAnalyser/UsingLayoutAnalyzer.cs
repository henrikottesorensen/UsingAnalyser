using System;
using System.Collections.Generic;
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

    /// <summary>Something else is configured to rewrite the using block, and will fight this one.</summary>
    public const string ConflictDiagnosticId = "UA1002";

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

    private static readonly DiagnosticDescriptor ConflictRule = new(
        ConflictDiagnosticId,
        title: "A configured setting will fight this using layout",
        messageFormat: "'{0}' will fight the layout UA1000 enforces - {1}",
        category: "Ordering",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Something else is configured to rewrite the using block, and two fixes that disagree do "
            + "not settle - they undo each other on every pass. A rule with an opinion and no fix only warns, "
            + "which is survivable; a second fix is what turns disagreement into motion. Neither case this "
            + "reports is visible in a build log: the organize-imports stage of 'dotnet format style' emits no "
            + "diagnostic at all, and SA1210 emits one far milder than what it does under 'dotnet format'. "
            + "Reported once per project rather than per file, because it is one setting that is wrong rather "
            + "than one mistake per file that happens to contain a using directive.",
        // A compilation-end diagnostic, because it is reported for the project rather than for a file.
        // The tag is not decoration: it is how the IDE knows to hold the report until a full analysis,
        // instead of dropping it when it looks at one file on its own.
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(OrderRule, SeparationRule, ConflictRule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        // A syntax tree action, not a semantic one: classification is a question about the text of a
        // namespace, so the analyser never needs a compilation and never waits for one.
        context.RegisterSyntaxTreeAction(Analyze);

        // UA1002 is about the project's configuration rather than any one file, so it is reported once
        // for the compilation instead of once per file. A misconfigured repository would otherwise
        // answer with one warning per source file, which buries the single thing to change.
        context.RegisterCompilationAction(AnalyzeConfiguration);
    }

    /// <summary>
    /// Reports the settings that will rewrite the using block behind this analyser's back. Once each,
    /// with no location: an .editorconfig key has no span in any source file, and pointing at whichever
    /// using directive happened to be first would be inventing one.
    /// </summary>
    private static void AnalyzeConfiguration(CompilationAnalysisContext context)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);

        // Per tree, because .editorconfig applies per path and a setting can be confined to one
        // directory - a project-wide read would miss that. The set is what keeps it to one report:
        // whichever file first brings a setting into force is the one that gets to say so.
        var severities = context.Compilation.Options.SyntaxTreeOptionsProvider;

        // Once, for the whole compilation, because a .globalconfig severity belongs to no tree. Asking
        // only the per-tree map missed this entirely: an .editorconfig writes SA1210 under [*.cs] and a
        // .globalconfig writes it globally, and a package that ships its rules as a globalconfig - which
        // is the usual way to ship them - lands only here.
        if (severities is not null
            && severities.TryGetGlobalDiagnosticValue(
                ConflictingSettings.StyleCopOrderingRule, context.CancellationToken, out var configured)
            && ConflictingSettings.IsActedOn(configured))
        {
            Report(
                context,
                reported,
                ConflictingSettings.StyleCopOrderingSeverityKey,
                ConflictingSettings.StyleCopRemedy);
        }

        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var options = context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree);

            foreach (var (key, remedy) in ConflictingSettings.InForce(options))
            {
                Report(context, reported, key, remedy);
            }

            // SA1210's severity cannot be read off the options above: the compiler takes
            // dotnet_diagnostic.*.severity out of the configuration and turns it into this map, so it
            // never arrives as an ordinary key. Asking the map is also the better question, since it
            // answers what is in force rather than what one file happened to write down.
            if (severities is not null
                && severities.TryGetDiagnosticValue(
                    tree, ConflictingSettings.StyleCopOrderingRule, context.CancellationToken, out var severity)
                && ConflictingSettings.IsActedOn(severity))
            {
                Report(
                    context,
                    reported,
                    ConflictingSettings.StyleCopOrderingSeverityKey,
                    ConflictingSettings.StyleCopRemedy);
            }
        }
    }

    /// <summary>Reports a setting the first time it is seen, and says nothing about it again.</summary>
    private static void Report(
        CompilationAnalysisContext context, HashSet<string> reported, string key, string remedy)
    {
        if (reported.Add(key))
        {
            context.ReportDiagnostic(Diagnostic.Create(ConflictRule, Location.None, key, remedy));
        }
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
