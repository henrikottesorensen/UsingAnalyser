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
public class UsingGroupAnalyzerTests
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

    /// <summary>
    /// Runs the analyser and, when <paramref name="fixedSource"/> differs, the code fix. Compiler
    /// diagnostics are off because these namespaces do not exist and do not need to: the analyser is
    /// a syntax tree action and never asks what a name binds to.
    /// </summary>
    private static async Task VerifyAsync(string source, string? fixedSource = null, string? prefixes = "SolutionPrefix")
    {
        var editorConfig = prefixes is null
            ? "root = true\n[*.cs]\n"
            : $"root = true\n[*.cs]\n{UsingLayout.FirstPartyPrefixesKey} = {prefixes}\n";

        var test = new CSharpCodeFixTest<UsingGroupAnalyzer, UsingGroupCodeFixProvider, DefaultVerifier>
        {
            TestCode = source,
            FixedCode = fixedSource ?? source,
            CompilerDiagnostics = CompilerDiagnostics.None,
        };

        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", editorConfig));

        await test.RunAsync();
    }
}
