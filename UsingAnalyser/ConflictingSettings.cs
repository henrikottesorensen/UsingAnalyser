using System.Collections.Generic;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace UsingAnalyser;

/// <summary>
/// Settings that make something else rewrite the using block. That is the one thing this layout
/// cannot survive: a rule with an opinion and no fix only warns, and you can switch it off or live
/// with it, but a second fix turns disagreement into motion. Two of them undo each other on every
/// pass, and the file never settles.
/// </summary>
/// <remarks>
/// This exists because neither of the two below is visible in a build log. The organize-imports stage
/// reports no diagnostic at all, and SA1210 reports something far milder than what it does - so both
/// were found by a consumer losing a day to them rather than by anybody reading the configuration.
/// UA1002 is that day, written down.
/// </remarks>
public static class ConflictingSettings
{
    /// <summary>Sorts System first. Harmless as a rule, and arms the imports stage by existing.</summary>
    public const string SortSystemDirectivesFirstKey = "dotnet_sort_system_directives_first";

    /// <summary>Blank lines by first-level namespace. Arms the imports stage by existing.</summary>
    public const string SeparateImportDirectiveGroupsKey = "dotnet_separate_import_directive_groups";

    /// <summary>StyleCop's alphabetical sort, whose fix and UA1000's undo one another.</summary>
    public const string StyleCopOrderingRule = "SA1210";

    /// <summary>
    /// How SA1210's severity is written, for the message. Not a key that can be read back: the compiler
    /// takes <c>dotnet_diagnostic.*.severity</c> out of the configuration and turns it into the
    /// compilation's severity map, so it never reaches <see cref="AnalyzerConfigOptions"/> at all - ask
    /// <see cref="Microsoft.CodeAnalysis.SyntaxTreeOptionsProvider"/> instead.
    /// </summary>
    public const string StyleCopOrderingSeverityKey = "dotnet_diagnostic.SA1210.severity";

    // Kept to one clause with no full stop, because these land in a diagnostic message: RS1032 wants a
    // single sentence there, and the reasoning belongs in the rule's description instead.
    private const string ImportsRemedy =
        "remove the key, since its presence at any value, including false, turns on the organize-imports "
        + "stage of 'dotnet format style' and leaves 'dotnet format --verify-no-changes' failing permanently";

    /// <summary>What to do about SA1210 being turned up, for the message.</summary>
    public const string StyleCopRemedy =
        "set it to none, since SA1210 has a fix of its own and under 'dotnet format' the two rewrite the "
        + "using block in turn, changing the file on every run";

    /// <summary>
    /// Every conflicting setting in force for <paramref name="options"/>, as a key and what to do about
    /// it. Reports what is configured rather than what is effective, because that is all a file's
    /// configuration can say: whether another analyser package is installed at all, and what its own
    /// defaults are, is not something this one can see.
    /// </summary>
    public static IEnumerable<(string Key, string Remedy)> InForce(AnalyzerConfigOptions options)
    {
        // Presence is the whole test. There is no value that switches the imports stage back off, which
        // is exactly the trap - "= false" reads like turning it off and is the same as turning it on.
        if (options.TryGetValue(SortSystemDirectivesFirstKey, out _))
        {
            yield return (SortSystemDirectivesFirstKey, ImportsRemedy);
        }

        if (options.TryGetValue(SeparateImportDirectiveGroupsKey, out _))
        {
            yield return (SeparateImportDirectiveGroupsKey, ImportsRemedy);
        }
    }

    /// <summary>
    /// Whether a configured severity is one <c>dotnet format</c> applies a fix for. Its threshold is
    /// <c>warn</c> unless told otherwise, so below that SA1210 has no fix in play and there is nothing
    /// for the two to fight over - reporting then would be crying wolf.
    /// </summary>
    /// <remarks>
    /// <see cref="ReportDiagnostic.Default"/> is the interesting one, and it is deliberately not
    /// reported. It means nobody has configured SA1210 either way, so what happens is StyleCop's own
    /// default - and no analyser can read another package's defaults. Unset is unknown here, not safe,
    /// which is why UA1002 is a check on configuration rather than a clean bill of health.
    /// </remarks>
    public static bool IsActedOn(ReportDiagnostic severity) =>
        severity is ReportDiagnostic.Warn or ReportDiagnostic.Error;
}
