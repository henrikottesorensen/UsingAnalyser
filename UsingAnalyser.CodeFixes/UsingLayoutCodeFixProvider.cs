using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace UsingAnalyser;

/// <summary>
/// Rewrites a file's using block into the canonical layout. This is the half that makes the rule
/// adoptable: turning it on in an existing repository is otherwise a few hundred files of hand
/// editing, which nobody does.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UsingLayoutCodeFixProvider))]
[Shared]
public sealed class UsingLayoutCodeFixProvider : CodeFixProvider
{
    private const string Title = "Group and sort using directives";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(UsingLayoutAnalyzer.OrderDiagnosticId, UsingLayoutAnalyzer.SeparationDiagnosticId);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            var span = diagnostic.Location.SourceSpan;

            context.RegisterCodeFix(
                CodeAction.Create(Title, cancellationToken => FixAsync(context.Document, span, cancellationToken), equivalenceKey: Title),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    private static async Task<Document> FixAsync(Document document, TextSpan span, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
        {
            return document;
        }

        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return document;
        }

        // The reported directive says which block to rewrite. A file can hold several - one per
        // namespace, plus the file's own - and each is laid out independently of the others.
        if (root.FindNode(span).FirstAncestorOrSelf<UsingDirectiveSyntax>()?.Parent is not { } container)
        {
            return document;
        }

        var options = UsingLayoutOptions.Read(
            document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree));

        // The same inference the analyser makes, or the fix would lay the block out differently from
        // the way the diagnostic described it.
        return document.WithSyntaxRoot(
            root.ReplaceNode(container, Rewrite(container, options.WithInferredFirstParty(UsingLayout.DeclaredRoot(container)))));
    }

    private static SyntaxNode Rewrite(SyntaxNode container, UsingLayoutOptions options)
    {
        var usings = UsingLayout.Relevant(container);
        if (usings.Length < 2 || usings.Any(directive => directive.ContainsDirectives))
        {
            return container;
        }

        var ordered = UsingLayout.Order(usings, options);
        var newline = DetectLineEnding(usings);

        // Whatever sits above the first directive and ends in a blank line is a file header, not that
        // directive's comment, so it stays at the top instead of travelling with the line it happened
        // to precede. A comment with no blank line under it is the opposite case and does travel.
        var firstLeading = usings[0].GetLeadingTrivia();
        var headerLength = HeaderLength(firstLeading);
        var header = SyntaxFactory.TriviaList(firstLeading.Take(headerLength));

        var rebuilt = new List<UsingDirectiveSyntax>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var directive = ordered[index];
            var leading = directive == usings[0]
                ? SyntaxFactory.TriviaList(firstLeading.Skip(headerLength))
                : OwnLeadingTrivia(directive.GetLeadingTrivia());

            if (index == 0)
            {
                leading = header.AddRange(leading);
            }
            else if (UsingLayout.NeedsSeparation(ordered[index - 1], directive, options))
            {
                leading = leading.Insert(0, newline);
            }

            rebuilt.Add(directive
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(NormalizeTrailingTrivia(directive.GetTrailingTrivia(), newline)));
        }

        var globals = UsingLayout.Globals(container);

        return UsingLayout.WithUsings(container, SyntaxFactory.List(globals.Concat(rebuilt)));
    }

    /// <summary>
    /// How many trivia at the front of <paramref name="list"/> form a file header: everything up to
    /// and including the last blank line.
    /// </summary>
    private static int HeaderLength(SyntaxTriviaList list)
    {
        var header = 0;
        var lineIsBlank = true;

        for (var index = 0; index < list.Count; index++)
        {
            if (list[index].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                if (lineIsBlank)
                {
                    header = index + 1;
                }

                lineIsBlank = true;
            }
            else if (!list[index].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                lineIsBlank = false;
            }
        }

        return header;
    }

    /// <summary>
    /// A directive's leading trivia with the blank lines above it dropped, so that separation becomes
    /// something this fix decides rather than something it inherits. Indentation is not a blank line
    /// and survives, which is what keeps usings written inside a namespace lined up.
    /// </summary>
    private static SyntaxTriviaList OwnLeadingTrivia(SyntaxTriviaList list)
    {
        var start = 0;

        while (start < list.Count)
        {
            if (list[start].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                start++;
                continue;
            }

            if (list[start].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                var lookahead = start;
                while (lookahead < list.Count && list[lookahead].IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    lookahead++;
                }

                if (lookahead < list.Count && list[lookahead].IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    start = lookahead + 1;
                    continue;
                }
            }

            break;
        }

        return SyntaxFactory.TriviaList(list.Skip(start));
    }

    /// <summary>
    /// A directive's trailing trivia cut back to the end of its own line, keeping any end-of-line
    /// comment. Blank lines below it belong to the next directive's separation, which this fix is
    /// about to decide for itself.
    /// </summary>
    private static SyntaxTriviaList NormalizeTrailingTrivia(SyntaxTriviaList list, SyntaxTrivia newline)
    {
        var kept = new List<SyntaxTrivia>(list.Count);

        foreach (var trivia in list)
        {
            kept.Add(trivia);
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                return SyntaxFactory.TriviaList(kept);
            }
        }

        kept.Add(newline);

        return SyntaxFactory.TriviaList(kept);
    }

    /// <summary>
    /// The file's own line ending, taken from the first one present rather than assumed, so a fix
    /// never turns a one-line change into a whole-file diff on the other platform.
    /// </summary>
    private static SyntaxTrivia DetectLineEnding(ImmutableArray<UsingDirectiveSyntax> usings)
    {
        foreach (var directive in usings)
        {
            foreach (var trivia in directive.GetTrailingTrivia())
            {
                if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    return trivia;
                }
            }
        }

        return SyntaxFactory.CarriageReturnLineFeed;
    }
}
