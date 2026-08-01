using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis.Diagnostics;

namespace UsingAnalyser;

/// <summary>
/// What one file's .editorconfig asks for. Every key carries the <c>usinglayout.</c> prefix so that
/// nothing here can collide with a built-in option, a StyleCop setting, or another analyser's.
/// </summary>
public sealed class UsingLayoutOptions
{
    /// <summary>The namespace roots that count as first party, comma separated.</summary>
    public const string FirstPartyPrefixesKey = "usinglayout.first_party_prefixes";

    /// <summary>Whether a blank line goes between System and what follows it.</summary>
    public const string SeparateSystemKey = "usinglayout.separate_system";

    /// <summary>Whether a blank line goes between third party and first party.</summary>
    public const string SeparateFirstPartyKey = "usinglayout.separate_first_party";

    private UsingLayoutOptions(ImmutableArray<string> firstPartyPrefixes, bool separateSystem, bool separateFirstParty)
    {
        FirstPartyPrefixes = firstPartyPrefixes;
        SeparateSystem = separateSystem;
        SeparateFirstParty = separateFirstParty;
    }

    /// <summary>What every consumer gets before configuring anything: the full three-block scheme.</summary>
    public static UsingLayoutOptions Default { get; } =
        new(ImmutableArray<string>.Empty, separateSystem: true, separateFirstParty: true);

    /// <summary>
    /// The roots naming first-party namespaces. Empty is a legitimate configuration rather than an
    /// error: the scheme collapses to System-then-everything-else, which is still stricter than
    /// anything the language gives you for free.
    /// </summary>
    public ImmutableArray<string> FirstPartyPrefixes { get; }

    /// <summary>Whether the System block stands apart from what follows it.</summary>
    public bool SeparateSystem { get; }

    /// <summary>Whether the first-party block stands apart from what precedes it.</summary>
    public bool SeparateFirstParty { get; }

    /// <summary>Reads the options that apply to one file.</summary>
    public static UsingLayoutOptions Read(AnalyzerConfigOptions options) =>
        new(
            ReadPrefixes(options),
            ReadFlag(options, SeparateSystemKey),
            ReadFlag(options, SeparateFirstPartyKey));

    /// <summary>
    /// Whether a blank line belongs at the boundary between two blocks. Crossing both boundaries at
    /// once - System straight to first party, in a file that happens to use nothing third party -
    /// takes a blank line if either of the boundaries it skips over asks for one, so that turning on
    /// one toggle keeps its block set apart no matter which other blocks the file contains.
    /// </summary>
    public bool Separates(UsingGroup earlier, UsingGroup later)
    {
        var from = earlier < later ? earlier : later;
        var to = earlier < later ? later : earlier;
        var separated = false;

        for (var boundary = (int)from; boundary < (int)to; boundary++)
        {
            separated |= boundary == (int)UsingGroup.System ? SeparateSystem : SeparateFirstParty;
        }

        return separated;
    }

    private static ImmutableArray<string> ReadPrefixes(AnalyzerConfigOptions options)
    {
        if (!options.TryGetValue(FirstPartyPrefixesKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return ImmutableArray<string>.Empty;
        }

        return raw
            .Split(',')
            .Select(prefix => prefix.Trim())
            .Where(prefix => prefix.Length > 0)
            .ToImmutableArray();
    }

    /// <summary>
    /// A flag, defaulting to on. Anything unparseable defaults the same way rather than reporting:
    /// a typo in a layout setting should not be the thing that fails a build.
    /// </summary>
    private static bool ReadFlag(AnalyzerConfigOptions options, string key) =>
        !options.TryGetValue(key, out var raw) || !bool.TryParse(raw.Trim(), out var value) || value;
}
