using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace UsingAnalyser;

/// <summary>
/// The one description of the canonical layout, shared by the analyser and the code fix so they
/// cannot drift: whatever <see cref="Order"/> produces is exactly what the analyser demands.
/// </summary>
public static class UsingLayout
{
    /// <summary>
    /// Every node in a file that can hold using directives: the file itself, then each namespace,
    /// outermost first. A namespace's usings are its own block and are laid out independently of the
    /// file's - they are in scope in different places, so running them together would be a claim about
    /// the code rather than about its layout.
    /// </summary>
    public static IEnumerable<SyntaxNode> Containers(CompilationUnitSyntax root)
    {
        yield return root;

        foreach (var namespaceDeclaration in Namespaces(root.Members))
        {
            yield return namespaceDeclaration;
        }
    }

    /// <summary>
    /// The non-global using directives of one container, in source order. Global usings are excluded
    /// rather than sorted: the compiler already requires them to come first, they are conventionally a
    /// generated or single-purpose file of their own, and reordering them across that boundary would
    /// be a change in meaning rather than in layout.
    /// </summary>
    public static ImmutableArray<UsingDirectiveSyntax> Relevant(SyntaxNode container) =>
        Usings(container).Where(directive => directive.GlobalKeyword.IsKind(SyntaxKind.None)).ToImmutableArray();

    /// <summary>
    /// The namespace root a container declares, or null when it declares none. This is what stands in
    /// for <c>first_party_prefixes</c> when nothing is configured: a file in <c>Contoso.Billing</c>
    /// is telling you, without being asked, that <c>Contoso</c> is its own code.
    /// </summary>
    public static string? DeclaredRoot(SyntaxNode container)
    {
        var declared = container switch
        {
            // Usings written inside a namespace get this for free: the container is the namespace.
            BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.Name,

            // Otherwise the file's own namespace, which is the first one declared in it. A file
            // holding several says nothing clear about whose code it is, and the first is as good an
            // answer as any - it is also the one a reader would give.
            CompilationUnitSyntax unit => unit.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name,

            _ => null,
        };

        if (declared is null)
        {
            return null;
        }

        var root = Root(Flatten(declared));

        return root.Length == 0 ? null : root;
    }

    /// <summary>The global usings of one container, which a rewrite has to put back untouched.</summary>
    public static ImmutableArray<UsingDirectiveSyntax> Globals(SyntaxNode container) =>
        Usings(container).Where(directive => !directive.GlobalKeyword.IsKind(SyntaxKind.None)).ToImmutableArray();

    /// <summary>
    /// One container with its using list replaced. <see cref="CompilationUnitSyntax"/> and
    /// <see cref="BaseNamespaceDeclarationSyntax"/> both carry usings but share no base type that says
    /// so, which is the only reason this is a switch rather than a call.
    /// </summary>
    public static SyntaxNode WithUsings(SyntaxNode container, SyntaxList<UsingDirectiveSyntax> usings) =>
        container switch
        {
            CompilationUnitSyntax unit => unit.WithUsings(usings),
            BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.WithUsings(usings),
            _ => container,
        };

    private static SyntaxList<UsingDirectiveSyntax> Usings(SyntaxNode container) =>
        container switch
        {
            CompilationUnitSyntax unit => unit.Usings,
            BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.Usings,
            _ => default,
        };

    /// <summary>
    /// The namespaces declared in <paramref name="members"/>, and those nested inside them. Walking
    /// members rather than every descendant matters: a namespace can only be declared at file level or
    /// inside another namespace, so descending into method bodies would cost a full tree walk per file
    /// to find nothing.
    /// </summary>
    private static IEnumerable<BaseNamespaceDeclarationSyntax> Namespaces(SyntaxList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            if (member is not BaseNamespaceDeclarationSyntax namespaceDeclaration)
            {
                continue;
            }

            yield return namespaceDeclaration;

            foreach (var nested in Namespaces(namespaceDeclaration.Members))
            {
                yield return nested;
            }
        }
    }

    /// <summary>The canonical order for <paramref name="usings"/>, which is a stable sort of them.</summary>
    public static ImmutableArray<UsingDirectiveSyntax> Order(
        ImmutableArray<UsingDirectiveSyntax> usings,
        UsingLayoutOptions options) =>
        usings
            .OrderBy(directive => Key(directive, options), UsingKeyComparer.Instance)
            .ToImmutableArray();

    /// <summary>
    /// Whether a blank line belongs between two adjacent directives. Ordering is not configurable and
    /// separation is, so two directives can sit in different blocks and still run together.
    /// </summary>
    public static bool NeedsSeparation(
        UsingDirectiveSyntax first,
        UsingDirectiveSyntax second,
        UsingLayoutOptions options)
    {
        var left = Key(first, options);
        var right = Key(second, options);

        // The trailing static and alias blocks are not part of the toggled scheme: they exist to keep
        // SA1216 and SA1209 satisfied, and running them into the block above would undo that.
        if (left.Kind != right.Kind)
        {
            return true;
        }

        // A boundary between blocks belongs to the block toggles, and only to them. Root separation
        // never overrules them, so asking for roots to be split does not quietly re-separate a block
        // boundary you had switched off.
        if (left.Group != right.Group)
        {
            return options.Separates(left.Group, right.Group);
        }

        // Inside one block, roots. Regular usings only: an alias sorts under its alias, which rarely
        // has a dot in it, so every alias would become a root of its own and every one of them would
        // get a blank line above it.
        return options.SeparateRoots
            && left.Kind == UsingKind.Regular
            && !string.Equals(Root(left.Name), Root(right.Name), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A namespace's first segment, which is what "Microsoft things together, Evilcorp things
    /// together" means. Ordering never has to change for this: the sort is alphabetical by segment,
    /// so a root's namespaces are already contiguous.
    /// </summary>
    private static string Root(string name)
    {
        var dot = name.IndexOf('.');

        return dot < 0 ? name : name.Substring(0, dot);
    }

    /// <summary>
    /// Whether a blank line currently sits between two adjacent directives. The newline that ends
    /// the first directive is one of the two we are counting, so a blank line means two or more.
    /// Counting stops at a comment because a comment introduces the directive below it, so blank
    /// lines above the comment are the separation and blank lines below it are the comment's own
    /// spacing - only the former is ours to judge.
    /// </summary>
    public static bool HasSeparation(UsingDirectiveSyntax first, UsingDirectiveSyntax second)
    {
        var newlines = first.GetTrailingTrivia().Count(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));

        foreach (var trivia in second.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                newlines++;
            }
            else if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                break;
            }
        }

        return newlines >= 2;
    }

    /// <summary>The sort key: which block a directive lands in, and where it sits inside that block.</summary>
    private static UsingKey Key(UsingDirectiveSyntax directive, UsingLayoutOptions options)
    {
        if (directive.Alias is not null)
        {
            // SA1211 orders aliases by the alias, not by what it points at, and an alias pointing at a
            // tuple or an array has no namespace to order by in the first place.
            return new UsingKey(UsingKind.Alias, UsingGroup.System, Flatten(directive.Alias.Name));
        }

        var name = Flatten(directive.NamespaceOrType);

        if (!directive.StaticKeyword.IsKind(SyntaxKind.None))
        {
            return new UsingKey(UsingKind.Static, UsingGroup.System, name);
        }

        return new UsingKey(UsingKind.Regular, Classify(name, options.FirstPartyPrefixes), name);
    }

    /// <summary>
    /// Which block a namespace belongs to. Ordinal throughout, because namespaces are case sensitive
    /// and a prefix that matches only under a loose comparison would be silently wrong rather than
    /// helpfully lenient.
    /// </summary>
    private static UsingGroup Classify(string name, ImmutableArray<string> firstPartyPrefixes)
    {
        if (IsUnder(name, "System"))
        {
            return UsingGroup.System;
        }

        foreach (var prefix in firstPartyPrefixes)
        {
            if (IsUnder(name, prefix))
            {
                return UsingGroup.FirstParty;
            }
        }

        return UsingGroup.ThirdParty;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is <paramref name="root"/> or sits beneath it. The explicit dot
    /// check is what stops a "System" root from swallowing "SystemsManager".
    /// </summary>
    private static bool IsUnder(string name, string root) =>
        string.Equals(name, root, StringComparison.Ordinal)
        || (name.Length > root.Length
            && name[root.Length] == '.'
            && name.StartsWith(root, StringComparison.Ordinal));

    /// <summary>
    /// A syntax node's text with every space removed, so that <c>using Foo . Bar;</c> sorts as
    /// <c>Foo.Bar</c> rather than as its own oddity.
    /// </summary>
    private static string Flatten(SyntaxNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        var text = node.ToString();
        if (!text.Any(char.IsWhiteSpace))
        {
            return text;
        }

        var flattened = new StringBuilder(text.Length);
        foreach (var character in text.Where(character => !char.IsWhiteSpace(character)))
        {
            flattened.Append(character);
        }

        return flattened.ToString();
    }

    /// <summary>Where a directive sorts: its block, and its place within it.</summary>
    private readonly struct UsingKey
    {
        public UsingKey(UsingKind kind, UsingGroup group, string name)
        {
            Kind = kind;
            Group = group;
            Name = name;
        }

        public UsingKind Kind { get; }

        public UsingGroup Group { get; }

        public string Name { get; }
    }

    /// <summary>
    /// Orders by block, then by name a dotted part at a time. Comparing whole strings would sort
    /// <c>Foo.Bar</c> after <c>FooBar</c> in some places and before it in others, because the dot
    /// sorts below letters but above digits; comparing part by part just asks the question the
    /// reader is asking.
    /// </summary>
    private sealed class UsingKeyComparer : IComparer<UsingKey>
    {
        public static readonly UsingKeyComparer Instance = new();

        public int Compare(UsingKey x, UsingKey y)
        {
            var kind = x.Kind.CompareTo(y.Kind);
            if (kind != 0)
            {
                return kind;
            }

            var group = x.Group.CompareTo(y.Group);
            if (group != 0)
            {
                return group;
            }

            var left = x.Name.Split('.');
            var right = y.Name.Split('.');

            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                // Case-insensitively first, because that is what "alphabetically" means to whoever
                // reads the file, and what SA1210 means too. Ordinal on its own sorts CSharp above
                // CodeActions - 'S' is below 'o' - so this rule would contradict the one it asks you
                // to keep switched on.
                var part = string.Compare(left[index], right[index], StringComparison.OrdinalIgnoreCase);
                if (part != 0)
                {
                    return part;
                }

                // Ordinal only to break an exact tie. Two segments differing solely in case are a
                // real, if unlikely, pair of namespaces, and they need some order rather than
                // whichever the sort happened to see first.
                part = string.CompareOrdinal(left[index], right[index]);
                if (part != 0)
                {
                    return part;
                }
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}

/// <summary>
/// The top-level partition, which exists to stay out of StyleCop's way: SA1216 wants every
/// <c>using static</c> after every plain using, and SA1209 wants every alias after those.
/// </summary>
public enum UsingKind
{
    /// <summary>A plain <c>using Some.Namespace;</c>, the only kind that gets grouped.</summary>
    Regular,

    /// <summary>A <c>using static</c>, which trails every plain using as one block of its own.</summary>
    Static,

    /// <summary>An alias, which trails everything else as one block of its own.</summary>
    Alias,
}

/// <summary>The three blank-line-separated blocks, in the order they appear in a file.</summary>
public enum UsingGroup
{
    /// <summary><c>System</c> and everything beneath it.</summary>
    System,

    /// <summary>Anything that is neither System nor a configured first-party root.</summary>
    ThirdParty,

    /// <summary>A namespace beneath one of the roots named by <c>using_first_party_prefixes</c>.</summary>
    FirstParty,
}
