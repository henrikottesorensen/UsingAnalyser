using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
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

    [Fact]
    public async Task RootsRunTogetherUnlessAskedToSeparate()
    {
        // The default, and what every 0.1.0 consumer already has on disk.
        await VerifyAsync("""
            using System;

            using Evilcorp.Thingamabob;
            using Microsoft.Extensions.Options;
            using Microsoft.Extensions.Primitives;

            internal class C;
            """);
    }

    [Fact]
    public async Task WithSeparateRootsOnEachRootIsItsOwnRun()
    {
        await VerifyAsync(
            """
            using System;

            using Evilcorp.Thingamabob;
            {|UA1001:using Microsoft.Extensions.Options;|}
            using Microsoft.Extensions.Primitives;

            internal class C;
            """,
            """
            using System;

            using Evilcorp.Thingamabob;

            using Microsoft.Extensions.Options;
            using Microsoft.Extensions.Primitives;

            internal class C;
            """,
            separateRoots: true);
    }

    [Fact]
    public async Task SeparateRootsSplitsTheFirstPartyBlockToo()
    {
        // Two configured roots, so the block has two of them and the toggle applies there as well.
        await VerifyAsync(
            """
            using System;

            using Contoso.Internal.Bits;
            {|UA1001:using SolutionPrefix.Host;|}

            internal class C;
            """,
            """
            using System;

            using Contoso.Internal.Bits;

            using SolutionPrefix.Host;

            internal class C;
            """,
            prefixes: "SolutionPrefix, Contoso",
            separateRoots: true);
    }

    [Fact]
    public async Task SeparateRootsDoesNotReopenABlockBoundaryYouClosed()
    {
        // separate_system is off, so System runs into third party even though their roots differ.
        // A block boundary is the block toggles' business, and this one does not overrule them.
        await VerifyAsync(
            """
            using System;
            using Evilcorp.Thingamabob;

            using Microsoft.Extensions.Options;

            internal class C;
            """,
            separateSystem: false,
            separateRoots: true);
    }

    [Fact]
    public async Task SeparateRootsLeavesAliasesAlone()
    {
        // An alias sorts under its alias, which has no dots, so treating each as a root would put a
        // blank line above every one of them.
        await VerifyAsync(
            """
            using System;

            using Alpha = System.Text.StringBuilder;
            using Zed = System.Collections.ArrayList;

            internal class C;
            """,
            separateRoots: true);
    }

    [Fact]
    public async Task WithNothingConfiguredTheFileSaysWhatIsFirstParty()
    {
        // No first_party_prefixes anywhere. The namespace the file declares is the answer.
        await VerifyAsync(
            """
            {|UA1000:using SolutionPrefix.Model;|}
            using System;
            using Gizmo.Widget;

            namespace SolutionPrefix.Host;

            internal class C;
            """,
            """
            using System;

            using Gizmo.Widget;

            using SolutionPrefix.Model;

            namespace SolutionPrefix.Host;

            internal class C;
            """,
            prefixes: null);
    }

    [Fact]
    public async Task ConfigurationBeatsWhatTheFileDeclares()
    {
        // The file is Contoso, the configuration says SolutionPrefix. A solution spanning several
        // roots has to be able to say so, and one file cannot know about the others.
        await VerifyAsync(
            """
            using System;

            using Contoso.Vendor;

            using SolutionPrefix.Model;

            namespace Contoso.App;

            internal class C;
            """,
            prefixes: "SolutionPrefix");
    }

    [Fact]
    public async Task UsingsInsideANamespaceInferFromThatNamespace()
    {
        await VerifyAsync(
            """
            namespace SolutionPrefix.Host
            {
                {|UA1000:using SolutionPrefix.Model;|}
                using System;
                using Gizmo.Widget;

                internal class C;
            }
            """,
            """
            namespace SolutionPrefix.Host
            {
                using System;

                using Gizmo.Widget;

                using SolutionPrefix.Model;

                internal class C;
            }
            """,
            prefixes: null);
    }

    [Fact]
    public async Task AFileDeclaringNoNamespaceHasNothingToInferFrom()
    {
        // Top-level statements and the like. Everything that is not System is third party, which is
        // the behaviour that used to apply everywhere when nothing was configured.
        await VerifyAsync(
            """
            using System;

            using Gizmo.Widget;
            using SolutionPrefix.Model;

            internal class C;
            """,
            prefixes: null);
    }

    /// <summary>
    /// Runs the analyser and, when <paramref name="fixedSource"/> differs, the code fix. Compiler
    /// diagnostics are off because these namespaces do not exist and do not need to: the analyser is
    /// a syntax tree action and never asks what a name binds to. Each setting is written only when
    /// the case names it, so unset really is unset rather than a default written out longhand.
    /// </summary>
    [Fact]
    public async Task CleanConfigurationReportsNoConflict()
    {
        await VerifyConfigurationAsync("");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task SortSystemDirectivesFirstIsReportedAtAnyValue(string value)
    {
        // The whole point of the rule. "= false" reads like switching the thing off and is identical to
        // switching it on, because dotnet format only asks whether the key is there.
        await VerifyConfigurationAsync(
            $"{ConflictingSettings.SortSystemDirectivesFirstKey} = {value}",
            ConflictingSettings.SortSystemDirectivesFirstKey);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task SeparateImportDirectiveGroupsIsReportedAtAnyValue(string value)
    {
        await VerifyConfigurationAsync(
            $"{ConflictingSettings.SeparateImportDirectiveGroupsKey} = {value}",
            ConflictingSettings.SeparateImportDirectiveGroupsKey);
    }

    [Fact]
    public async Task BothImportKeysAreReportedSeparately()
    {
        // Two keys, two things to delete. One report naming only the first would leave the second in
        // place and the build still broken after doing as it asked.
        await VerifyConfigurationAsync(
            $"""
            {ConflictingSettings.SortSystemDirectivesFirstKey} = true
            {ConflictingSettings.SeparateImportDirectiveGroupsKey} = true
            """,
            ConflictingSettings.SortSystemDirectivesFirstKey,
            ConflictingSettings.SeparateImportDirectiveGroupsKey);
    }

    [Theory]
    [InlineData("warning")]
    [InlineData("error")]
    [InlineData("Warning")]
    public async Task StyleCopOrderingIsReportedWhenTurnedUp(string severity)
    {
        await VerifyConfigurationAsync(
            $"{ConflictingSettings.StyleCopOrderingSeverityKey} = {severity}",
            ConflictingSettings.StyleCopOrderingSeverityKey);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("silent")]
    [InlineData("suggestion")]
    public async Task StyleCopOrderingIsLeftAloneBelowWarning(string severity)
    {
        // dotnet format fixes at warn and above by default, so below that SA1210 has no fix in play and
        // there is nothing for the two to fight over. Reporting here would be crying wolf.
        await VerifyConfigurationAsync($"{ConflictingSettings.StyleCopOrderingSeverityKey} = {severity}");
    }

    [Fact]
    public async Task StyleCopOrderingIsReportedWhenSetInAGlobalConfig()
    {
        // The case the .editorconfig tests do not reach, and the one that matters most: a package that
        // ships rule severities ships them as a .globalconfig, which belongs to no file. Reading only
        // the per-tree severities found nothing here, and every consumer configured that way - which is
        // all of them, since that is how a rules package is delivered - would have gone unwarned.
        await VerifyGlobalConfigurationAsync(
            $"{ConflictingSettings.StyleCopOrderingSeverityKey} = warning",
            ConflictingSettings.StyleCopOrderingSeverityKey);
    }

    [Fact]
    public async Task ImportKeysAreReportedWhenSetInAGlobalConfig()
    {
        await VerifyGlobalConfigurationAsync(
            $"{ConflictingSettings.SortSystemDirectivesFirstKey} = false",
            ConflictingSettings.SortSystemDirectivesFirstKey);
    }

    [Fact]
    public async Task StyleCopOrderingLeftUnsetIsNotReported()
    {
        // The blind spot, stated as a test so that nobody mistakes silence for a clean bill of health.
        // SA1210's own default is StyleCop's business, and no analyser can read another package's
        // defaults - so an unset key is unknown here, not safe.
        await VerifyConfigurationAsync("");
    }

    /// <summary>
    /// Runs the canonical layout against <paramref name="settings"/>, which are written into
    /// .editorconfig verbatim, and asserts exactly which UA1002 reports come back.
    /// </summary>
    /// <remarks>
    /// The layout here is already correct, so UA1000 and UA1001 have nothing to say and anything
    /// reported is the configuration check. The expected diagnostics are given rather than marked up
    /// in the source because UA1002 has no location: an .editorconfig key has no span in a .cs file.
    /// </remarks>
    /// <summary>The settings written into .editorconfig, under <c>[*.cs]</c>.</summary>
    private static Task VerifyConfigurationAsync(string settings, params string[] expectedKeys) =>
        VerifyConfigurationCoreAsync(settings, globalSettings: null, expectedKeys);

    /// <summary>The settings written into a .globalconfig, which belongs to no file.</summary>
    private static Task VerifyGlobalConfigurationAsync(string globalSettings, params string[] expectedKeys) =>
        VerifyConfigurationCoreAsync(settings: "", globalSettings, expectedKeys);

    private static async Task VerifyConfigurationCoreAsync(
        string settings, string? globalSettings, string[] expectedKeys)
    {
        const string Source = """
            using System;

            using Gizmo.Widget;

            using SolutionPrefix.Host;

            internal class C;
            """;

        // The same harness the layout cases use. UA1002 is not in the fixer's FixableDiagnosticIds, so
        // nothing is offered for it and the fixed source is the source unchanged - which is itself worth
        // asserting: a configuration mistake is not something a code fix can put right.
        var test = new CSharpCodeFixTest<UsingLayoutAnalyzer, UsingLayoutCodeFixProvider, DefaultVerifier>
        {
            TestCode = Source,
            FixedCode = Source,
            CompilerDiagnostics = CompilerDiagnostics.None,
        };

        test.TestState.AnalyzerConfigFiles.Add((
            "/.editorconfig",
            $"root = true\n[*.cs]\n{UsingLayoutOptions.FirstPartyPrefixesKey} = SolutionPrefix\n{settings}\n"));

        // A second file with is_global, which is how a rules package delivers severities: it applies to
        // every file and belongs to none, and the analyser has to ask a different question to see it.
        if (globalSettings is not null)
        {
            test.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", $"is_global = true\n{globalSettings}\n"));
        }

        foreach (var key in expectedKeys)
        {
            test.ExpectedDiagnostics.Add(
                new DiagnosticResult(UsingLayoutAnalyzer.ConflictDiagnosticId, DiagnosticSeverity.Warning)
                    .WithArguments(key, RemedyFor(key)));
        }

        await test.RunAsync();
    }

    /// <summary>
    /// The remedy text UA1002 pairs with a key. Reproduced here rather than read off the analyser, so
    /// that a change to what the message tells people has to be made deliberately in two places.
    /// </summary>
    private static string RemedyFor(string key) => key switch
    {
        ConflictingSettings.StyleCopOrderingSeverityKey =>
            "set it to none, since SA1210 has a fix of its own and under 'dotnet format' the two rewrite the "
            + "using block in turn, changing the file on every run",
        _ =>
            "remove the key, since its presence at any value, including false, turns on the organize-imports "
            + "stage of 'dotnet format style' and leaves 'dotnet format --verify-no-changes' failing permanently",
    };

    private static async Task VerifyAsync(
        string source,
        string? fixedSource = null,
        string? prefixes = "SolutionPrefix",
        bool? separateSystem = null,
        bool? separateFirstParty = null,
        bool? separateRoots = null)
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

        if (separateRoots is not null)
        {
            settings.Add($"{UsingLayoutOptions.SeparateRootsKey} = {(separateRoots.Value ? "true" : "false")}");
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
