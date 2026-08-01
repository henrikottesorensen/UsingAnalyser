using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

using UsingAnalyser;

namespace UsingAnalyser.Test;

/// <summary>
/// The layout the analyser enforces, stated as before-and-after pairs. Every case that reports also
/// states its fixed form, so the harness proves the fix converges - it re-runs the analyser on the
/// output and fails if anything is still reported.
/// </summary>
public class UsingLayoutAnalyzerTests
{
    [Fact]
    public async Task CanonicalLayoutIsAccepted()
    {
        await VerifyAsync("""
            using System;
            using System.Blagh;

            using Gizmo.Widget;
            using ThirdParty.Library.Thingy;

            using SolutionPrefix.Host;
            using SolutionPrefix.Model;

            internal class C;
            """);
    }

    [Fact]
    public async Task FirstPartyIsSortedLastEvenThoughItSortsFirstAlphabetically()
    {
        // The case StyleCop cannot reach: SA1210 wants Gizmo after SolutionPrefix, because G sorts
        // after S only when you stop caring where a namespace comes from.
        await VerifyAsync(
            """
            {|UA1000:using SolutionPrefix.Host;|}
            using System;
            using Gizmo.Widget;

            internal class C;
            """,
            """
            using System;

            using Gizmo.Widget;

            using SolutionPrefix.Host;

            internal class C;
            """);
    }

    [Fact]
    public async Task MissingBlankLineBetweenBlocksIsReported()
    {
        await VerifyAsync(
            """
            using System;
            {|UA1001:using Gizmo.Widget;|}

            internal class C;
            """,
            """
            using System;

            using Gizmo.Widget;

            internal class C;
            """);
    }

    [Fact]
    public async Task BlankLineInsideOneBlockIsReported()
    {
        await VerifyAsync(
            """
            using System;

            {|UA1001:using System.Text;|}

            internal class C;
            """,
            """
            using System;
            using System.Text;

            internal class C;
            """);
    }

    [Fact]
    public async Task WithNoConfiguredPrefixesEverythingButSystemIsThirdParty()
    {
        await VerifyAsync(
            """
            using System;

            using Gizmo.Widget;

            {|UA1001:using SolutionPrefix.Host;|}

            internal class C;
            """,
            """
            using System;

            using Gizmo.Widget;
            using SolutionPrefix.Host;

            internal class C;
            """,
            prefixes: null);
    }

    [Fact]
    public async Task AFileHeaderStaysAtTheTopButACommentTravelsWithItsDirective()
    {
        await VerifyAsync(
            """
            // Copyright someone.

            {|UA1000:using Gizmo.Widget;|}
            // Needed for the thing.
            using System;

            internal class C;
            """,
            """
            // Copyright someone.

            // Needed for the thing.
            using System;

            using Gizmo.Widget;

            internal class C;
            """);
    }

    [Fact]
    public async Task StaticAndAliasUsingsTrailInBlocksOfTheirOwn()
    {
        await VerifyAsync(
            """
            {|UA1000:using static System.Math;|}
            using Zed = System.Text.StringBuilder;
            using System;
            using SolutionPrefix.Host;

            internal class C;
            """,
            """
            using System;

            using SolutionPrefix.Host;

            using static System.Math;

            using Zed = System.Text.StringBuilder;

            internal class C;
            """);
    }

    [Fact]
    public async Task GlobalUsingsAreLeftWhereTheyAre()
    {
        await VerifyAsync(
            """
            global using System.Text;

            {|UA1000:using SolutionPrefix.Host;|}
            using System;

            internal class C;
            """,
            """
            global using System.Text;

            using System;

            using SolutionPrefix.Host;

            internal class C;
            """);
    }

    [Fact]
    public async Task UsingsUnderAConditionalAreLeftAlone()
    {
        // Reordering across an #if would change which usings the compiler sees, so layout gives way.
        await VerifyAsync("""
            using SolutionPrefix.Host;
            #if DEBUG
            using System;
            #endif

            internal class C;
            """);
    }

    [Fact]
    public async Task ASingleUsingIsNeverReported()
    {
        await VerifyAsync("""
            using SolutionPrefix.Host;

            internal class C;
            """);
    }

    [Fact]
    public async Task WithSeparateSystemOffSystemRunsIntoThirdParty()
    {
        await VerifyAsync(
            """
            using System;

            {|UA1001:using Gizmo.Widget;|}

            using SolutionPrefix.Host;

            internal class C;
            """,
            """
            using System;
            using Gizmo.Widget;

            using SolutionPrefix.Host;

            internal class C;
            """,
            separateSystem: false);
    }

    [Fact]
    public async Task WithSeparateFirstPartyOffFirstPartyRunsIntoThirdParty()
    {
        await VerifyAsync(
            """
            using System;

            using Gizmo.Widget;

            {|UA1001:using SolutionPrefix.Host;|}

            internal class C;
            """,
            """
            using System;

            using Gizmo.Widget;
            using SolutionPrefix.Host;

            internal class C;
            """,
            separateFirstParty: false);
    }

    [Fact]
    public async Task WithBothOffOrderIsStillEnforcedAsOneRun()
    {
        // The point of splitting ordering from separation: turning the blank lines off does not turn
        // the scheme off, it just stops it being visible.
        await VerifyAsync(
            """
            {|UA1000:using SolutionPrefix.Host;|}
            using System;
            using Gizmo.Widget;

            internal class C;
            """,
            """
            using System;
            using Gizmo.Widget;
            using SolutionPrefix.Host;

            internal class C;
            """,
            separateSystem: false,
            separateFirstParty: false);
    }

    [Fact]
    public async Task ABoundaryThatSkipsThirdPartyIsSeparatedIfEitherToggleAsksForIt()
    {
        // No third-party usings at all, so the one boundary in the file crosses both toggles. Only
        // separate_first_party is on, and that is enough.
        await VerifyAsync(
            """
            using System;

            using SolutionPrefix.Host;

            internal class C;
            """,
            separateSystem: false);
    }

    [Fact]
    public async Task ABoundaryThatSkipsThirdPartyRunsTogetherWhenNeitherToggleAsksForIt()
    {
        await VerifyAsync(
            """
            using System;
            using SolutionPrefix.Host;

            internal class C;
            """,
            separateSystem: false,
            separateFirstParty: false);
    }

    [Fact]
    public async Task StaticAndAliasBlocksStaySeparatedWhateverTheTogglesSay()
    {
        // They are not part of the toggled scheme: they are there to keep SA1216 and SA1209 happy.
        await VerifyAsync(
            """
            using System;
            using SolutionPrefix.Host;

            using static System.Math;

            using Zed = System.Text.StringBuilder;

            internal class C;
            """,
            separateSystem: false,
            separateFirstParty: false);
    }

    [Fact]
    public async Task UsingsInsideANamespaceAreLaidOutToo()
    {
        // The placement SA1200 asks for by default. Before containers, this reported nothing at all.
        await VerifyAsync(
            """
            namespace Sample
            {
                {|UA1000:using SolutionPrefix.Host;|}
                using System;
                using Gizmo.Widget;

                internal class C;
            }
            """,
            """
            namespace Sample
            {
                using System;

                using Gizmo.Widget;

                using SolutionPrefix.Host;

                internal class C;
            }
            """);
    }

    [Fact]
    public async Task IndentationSurvivesTheRewrite()
    {
        await VerifyAsync(
            """
            namespace Outer
            {
                namespace Inner
                {
                    using System;
                    {|UA1001:using Gizmo.Widget;|}

                    internal class C;
                }
            }
            """,
            """
            namespace Outer
            {
                namespace Inner
                {
                    using System;

                    using Gizmo.Widget;

                    internal class C;
                }
            }
            """);
    }

    [Fact]
    public async Task EachContainerIsItsOwnBlock()
    {
        // Two independent blocks in one file, so both report and Fix All settles them in one pass.
        await VerifyAsync(
            """
            {|UA1000:using SolutionPrefix.Host;|}
            using System;

            namespace Sample
            {
                {|UA1000:using Gizmo.Widget;|}
                using System.Text;

                internal class C;
            }
            """,
            """
            using System;

            using SolutionPrefix.Host;

            namespace Sample
            {
                using System.Text;

                using Gizmo.Widget;

                internal class C;
            }
            """);
    }

    [Fact]
    public async Task UsingsAfterAFileScopedNamespaceAreLaidOutToo()
    {
        await VerifyAsync(
            """
            namespace Sample;

            {|UA1000:using SolutionPrefix.Host;|}
            using System;

            internal class C;
            """,
            """
            namespace Sample;

            using System;

            using SolutionPrefix.Host;

            internal class C;
            """);
    }

    [Fact]
    public async Task SegmentsSortAlphabeticallyWhateverTheirCase()
    {
        // Ordinal on its own puts CSharp above CodeActions, because 'S' sorts below 'o'. SA1210 does
        // not agree, and neither does anyone reading the file - so neither do we.
        await VerifyAsync(
            """
            {|UA1000:using Gizmo.CSharp;|}
            using Gizmo.CodeActions;
            using Gizmo.CodeFixes;

            internal class C;
            """,
            """
            using Gizmo.CodeActions;
            using Gizmo.CodeFixes;
            using Gizmo.CSharp;

            internal class C;
            """);
    }

    [Fact]
    public async Task SegmentsDifferingOnlyInCaseStillHaveAStableOrder()
    {
        // Nothing here is alphabetical any more, so ordinal breaks the tie and the answer is at least
        // the same one every time.
        await VerifyAsync(
            """
            {|UA1000:using Gizmo.Widget;|}
            using Gizmo.WIDGET;

            internal class C;
            """,
            """
            using Gizmo.WIDGET;
            using Gizmo.Widget;

            internal class C;
            """);
    }

    /// <summary>
    /// Runs the analyser and, when <paramref name="fixedSource"/> differs, the code fix. Compiler
    /// diagnostics are off because these namespaces do not exist and do not need to: the analyser is
    /// a syntax tree action and never asks what a name binds to. Each setting is written only when
    /// the case names it, so unset really is unset rather than a default written out longhand.
    /// </summary>
    private static async Task VerifyAsync(
        string source,
        string? fixedSource = null,
        string? prefixes = "SolutionPrefix",
        bool? separateSystem = null,
        bool? separateFirstParty = null)
    {
        var settings = new List<string> { "root = true", "[*.cs]" };

        if (prefixes is not null)
        {
            settings.Add($"{UsingLayoutOptions.FirstPartyPrefixesKey} = {prefixes}");
        }

        if (separateSystem is not null)
        {
            settings.Add($"{UsingLayoutOptions.SeparateSystemKey} = {(separateSystem.Value ? "true" : "false")}");
        }

        if (separateFirstParty is not null)
        {
            settings.Add($"{UsingLayoutOptions.SeparateFirstPartyKey} = {(separateFirstParty.Value ? "true" : "false")}");
        }

        var editorConfig = string.Join("\n", settings) + "\n";

        var test = new CSharpCodeFixTest<UsingLayoutAnalyzer, UsingLayoutCodeFixProvider, DefaultVerifier>
        {
            TestCode = source,
            FixedCode = fixedSource ?? source,
            CompilerDiagnostics = CompilerDiagnostics.None,
        };

        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", editorConfig));

        await test.RunAsync();
    }
}
