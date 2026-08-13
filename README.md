# UsingAnalyser

Enforces a using layout that neither the built-in options nor StyleCop can express:

```csharp
using System;
using System.Blagh;

using Gizmo.Widget;
using ThirdParty.Library.Thingy;

using SolutionPrefix.Host;
using SolutionPrefix.Model;
```

System first, then third party, then the solution's own namespaces. Three blocks, one blank line
between them, each block sorted alphabetically.

Every using block in a file is laid out independently: the file's own directives, and any written
inside a namespace - the placement StyleCop's SA1200 asks for by default. Nested namespaces each get
a block of their own, since usings in different scopes are in force in different places.

## Why this needs an analyser

- `dotnet_separate_import_directive_groups` does insert blank lines, but a "group" is the first-level
  namespace. It splits third party into one block per vendor, and has no way to tell a vendor from
  you.
- `dotnet_sort_system_directives_first` gets System to the top and stops there.
- StyleCop's SA1208 and SA1210 know "System first, then alphabetical" and have no notion of a
  blank-line block at all.

Neither of the `dotnet_*` options is a diagnostic. They configure the editor's sort-usings action and
`dotnet format`, so nothing enforces them during a build.

## Configuration

Four keys, in `.editorconfig`. All of them carry the `usinglayout.` prefix, so nothing here can
collide with a built-in option, a StyleCop setting, or another analyser's.

```ini
[*.cs]
usinglayout.first_party_prefixes = SolutionPrefix
usinglayout.separate_system = true
usinglayout.separate_first_party = true
usinglayout.separate_roots = false
```

**`first_party_prefixes` is optional.** Left unset, each file is judged against the namespace it
declares: a file in `Contoso.Billing` has already said that `Contoso` is its own code, and there is
no reason to make you repeat it. Usings written inside a namespace are judged against that namespace.

Set it when inference is not enough, and it always wins when set:

- **A solution spanning several roots.** `Contoso.Platform, Fabrikam` - one file cannot know about
  the others, so a file in `Contoso` would otherwise treat `Fabrikam` as a vendor.
- **Files that deliberately declare someone else's namespace.** An extension method placed in
  `Microsoft.Extensions.DependencyInjection` for discoverability would otherwise make every
  `Microsoft.*` first party.
- **Files with no namespace at all**, such as top-level statements. Nothing to infer from, so
  everything that is not System is a vendor.

Matching is ordinal, because namespaces are case sensitive, and a root only matches at a dot
boundary, so `System` never swallows `SystemsManager`.

`separate_system` and `separate_first_party` decide the boundaries between blocks. Both default to
`true`, and they work independently:

| separate_system | separate_first_party | Result                                              |
|-----------------|----------------------|-----------------------------------------------------|
| `true`          | `true`               | Three blocks (the default).                          |
| `false`         | `true`               | System runs into third party; first party set apart. |
| `true`          | `false`              | System set apart; third party runs into first party. |
| `false`         | `false`              | One run, still ordered.                              |

Turning both off does not turn the scheme off - it stops the scheme being *visible*. UA1000 still
sorts System, then third party, then first party; there is just nothing between the blocks.

`separate_roots` is the fourth, and it works one level down: inside a block, it puts a blank line
wherever the first namespace segment changes, so vendors stand apart from each other.

```csharp
using Evilcorp.Thingamabob;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using NSubstitute;
```

It defaults to `false`, unlike the other three. Turning it on relayouts every file with more than one
vendor in a block, and that is not a thing to do to somebody who upgraded a patch version.

It applies to every block, not only third party. The System block never splits in practice, because
`System`, `System.Text` and `System.Threading.Tasks` all have the same first segment - only the
*root* changes a block, not the segments under it. The first-party block does split when
`first_party_prefixes` names roots that differ:

```ini
usinglayout.first_party_prefixes = Contoso, Fabrikam
```

```csharp
using Contoso.Thing;

using Fabrikam.Other;
```

It never overrules the block toggles. A boundary *between* blocks is theirs alone, so switching
`separate_system` off keeps System running into third party even though their roots differ - asking
for roots to be split does not quietly reopen a boundary you closed.

The trailing `using static` and alias blocks are exempt, and stay exempt: splitting them is reported
as an error rather than merely not required. They exist to keep SA1216 and SA1209 satisfied, and an
alias sorts under its alias, which rarely contains a dot - so every alias would otherwise become a
root of its own and collect a blank line above it.

Ordering never changes for any of this. The sort is alphabetical segment by segment, so a root's
namespaces are already contiguous - `separate_roots` only decides whether a blank line falls between
them.

Two things the toggles do not reach. A file with no third-party usings has one boundary that crosses
both toggles at once, and it takes a blank line if *either* toggle asks for one, so a block you asked
to set apart stays set apart regardless of what else the file happens to import. And the trailing
`using static` and alias blocks are always separated: they exist to keep SA1216 and SA1209 satisfied,
and running them into the block above would undo that.

An unparseable value falls back to the default rather than reporting. A typo in a layout setting
should not be the thing that fails a build.

## Rules

| Rule   | Says                                                             |
|--------|------------------------------------------------------------------|
| UA1000 | The directives are in the wrong order.                            |
| UA1001 | The order is right but the blank lines between blocks are not.    |
| UA1002 | Something else is configured to rewrite the using block.          |

All three are warnings by default. UA1000 and UA1001 are fixable, and at most one is reported per
file - the fix rewrites the whole block in a single edit, so a report per misplaced line would be one
problem described N times.

UA1002 is the odd one. It is about the project's configuration rather than any file's contents, so it
is reported once per project with no location, and it is not fixable - a code fix edits source, and
what is wrong here is an `.editorconfig`. It fires on:

- `dotnet_sort_system_directives_first`, present at any value
- `dotnet_separate_import_directive_groups`, present at any value
- `dotnet_diagnostic.SA1210.severity` set to `warning` or `error`

The first two are checked for *presence*, because that is the actual trigger - `= false` reads like
switching the thing off and does exactly what `= true` does. The third is checked against the
compilation's severity map rather than read as a key, since the compiler takes
`dotnet_diagnostic.*.severity` out of the configuration before an analyser ever sees it.

**Silence from UA1002 is not a clean bill of health**, and the gap is worth knowing. If SA1210 is left
unset, what happens is StyleCop's own default, and no analyser can read another package's defaults -
so unset is *unknown* here rather than safe. Below `warning` it stays quiet on purpose: `dotnet
format` fixes at `warn` and above unless told otherwise, so a suggestion puts no second fix in play
and reporting it would be crying wolf.

## Installing

As a project reference, which is the quickest way to try it:

```xml
<ProjectReference Include="../UsingAnalyser/UsingAnalyser/UsingAnalyser.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
<ProjectReference Include="../UsingAnalyser/UsingAnalyser.CodeFixes/UsingAnalyser.CodeFixes.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Or as a package, `dotnet pack UsingAnalyser.CodeFixes` (package id `UsingLayoutAnalyser`), which puts
both assemblies where Roslyn looks for them.

## Turning it on in an existing repository

`dotnet format` applies the fix in bulk:

```bash
dotnet format analyzers YourSolution.slnx --diagnostics UA1000 UA1001 --severity warn
```

Two settings, and the canonical layout then builds clean:

```ini
csharp_using_directive_placement = outside_namespace:error
dotnet_diagnostic.SA1210.severity = none
```

And two settings that must be **absent**, not set to anything:

```ini
dotnet_sort_system_directives_first
dotnet_separate_import_directive_groups
```

`dotnet format style` runs an organize-imports pass, reported as `error IMPORTS: Fix imports
ordering`, that switches on if either key is present - at *any* value, including `false`. It then
sorts flat-alphabetically after System, which puts first party above the vendors. It is not a
diagnostic: it has no severity, and setting `dotnet_diagnostic.IDE0055.severity = none` does not
reach it.

The result is a loop rather than a wrong layout. Each run reorders in the style stage, UA1000 puts it
back in the analyzers stage, and the file on disk never changes - so `dotnet format` reports nothing
to do while `dotnet format --verify-no-changes` exits 2 forever, and CI stays red with no way to
satisfy it.

## Which other rules this touches

Measured rather than reasoned about, against StyleCop.Analyzers 1.2.0.556 on a canonical file
including statics and aliases, with `dotnet_analyzer_diagnostic.severity = warning` - every StyleCop,
CA and IDE rule at once, rather than one category. With the three below settled, that file reports
nothing at all.

Measured against `dotnet format` as well as against the build, because the two do not agree. A rule
can be silent as a diagnostic and still be enforced by a formatting stage that reports nothing, and a
rule that reports only a warning at build can rewrite your files on every `dotnet format` run. Both
happened here, and neither was visible from a build log.

**Conflicts. These three must be settled, or the layout will not build clean.**

| Rule | What happens | Settle it with |
|------|--------------|----------------|
| `SA1210` | Sorts the whole list alphabetically, so it wants third party and first party interleaved - precisely the split this scheme creates. Every laid-out file becomes a warning, and under `TreatWarningsAsErrors` a broken build. Worse under `dotnet format`: SA1210 has a fixer too, so the two rewrite the block in turn and the file **oscillates**, changing on every run forever. | `dotnet_diagnostic.SA1210.severity = none`. UA1000 takes over sorting entirely. |
| `IDE0055` | Fires *only* when `dotnet_separate_import_directive_groups = true`, because that option wants blank lines by first-level namespace. Under `EnforceCodeStyleInBuild` it is a build warning, not merely the editor regrouping behind you. | Remove the key. Setting it to `false` silences IDE0055 but arms `IMPORTS` below, which is worse. |
| `IMPORTS` | Not a rule but a stage of `dotnet format style`, armed by the mere presence of `dotnet_sort_system_directives_first` or `dotnet_separate_import_directive_groups` at any value. It sorts flat-alphabetically after System, contradicting the first-party block, and no severity setting reaches it. `dotnet format` then makes no change while `--verify-no-changes` exits 2 permanently. | Remove both keys. They configure a sorter that UA1000 has replaced, so there is nothing left for them to do. |

The two are one problem wearing different hats, and it is worth stating as a rule: **anything that
rewrites the using block will fight this one.** Not anything that disagrees about order - a rule with
an opinion and no fixer only warns, and you can switch it off or live with it. A *fixer* is what
turns disagreement into motion. `IMPORTS` is a formatter and `SA1210` is an analyser fixer, and they
were found by two different accidents rather than by looking, because neither is visible in a build
log: one reports nothing, and the other reports something far milder than what it does.

The shapes differ in how they fail, and the second is the one that costs you:

```
SA1210 = warning, three consecutive dotnet format runs

  run 1 -> md5 95946a80c174365c31e180916cbb7029
  run 2 -> md5 aeb36bcea02ebeffb2a5de3076ca4687
  run 3 -> md5 95946a80c174365c31e180916cbb7029
```

```diff
  using System;
  using System.Collections.Generic;
-
- using Evilcorp.Widgets;
-
  using Contoso.Billing.Model;
+ using Evilcorp.Widgets;
```

`IMPORTS` leaves a correct file on disk that `--verify-no-changes` refuses to pass, which is
maddening but at least stable. `SA1210` produces a genuine diff every single time anyone runs
`dotnet format`, which means spurious commits and merge conflicts between people who ran it at
different moments.

Both are what UA1002 reports, so this table is a description of a rule rather than a list of things
to remember. Neither had to be remembered by anybody after they cost a consumer a day each.

**A trap that is not this analyser's doing.** `SA1200` fires on every using under StyleCop's defaults,
because it wants them *inside* the namespace. Declaring
`csharp_using_directive_placement = outside_namespace` silences it - StyleCop honours that option.
Either placement works here, so this only decides which shape you are enforcing, not whether the
analyser applies. `inside_namespace` was checked under `dotnet format` too, since `IDE0065` has a
fixer and moves the directives: it moves them, UA1000 lays them out in their new scope, and the two
settle rather than take turns.

**Compatible, verified silent on the canonical layout:** `SA1208` (System first), `SA1209` (aliases
last), `SA1211` (aliases alphabetical), `SA1216` and `SA1217` (`using static` placement and order),
and `SA1516` - including with `stylecop.layout.allowConsecutiveUsings = false`. `IDE0005` is
orthogonal: it removes usings nothing needs, which is a separate question from where the rest go.

Silent is the weaker claim, so these were run through `dotnet format` as well - every rule at warning
with only `SA1210` switched off. Several of them have fixers, and none of those fixers disturbs the
layout: the file is byte-identical after three consecutive runs and `--verify-no-changes` exits 0.

`dotnet_sort_system_directives_first = true` used to be listed here, and as a diagnostic it is indeed
silent - UA1000 already sorts System first, so there is nothing for it to report. That was measured
against the build alone, which missed that the key also arms the `IMPORTS` stage above. Silent is not
the same as harmless.

One thing removing these keys does cost, measured rather than waved away: with
`dotnet_separate_import_directive_groups = true`, SA1516 reports a missing blank line between using
groups, and with the key absent or `false` it does not. UA1001 already requires that blank line, and
knows first party from vendor where SA1516 only ever saw first-level namespaces, so the check is not
lost - only the second, blunter copy of it.

Sorting is case-insensitive, deliberately matching what SA1210 accepts. Ordinal comparison would put
`CSharp` above `CodeActions`, since `S` sits below `o` in character order, and this rule would then
contradict the one it asks you to keep switched on everywhere else.

## What it deliberately leaves alone

- **Global usings.** The compiler already pins them to the front, and moving a using across that
  boundary is a change in meaning rather than in layout.
- **Any file with a `#if` around a using.** The position of a conditioned using is meaningful, and
  layout is not worth changing which usings the compiler sees.
- **A file header.** A comment above the first using that is followed by a blank line stays at the
  top. A comment with no blank line under it belongs to the directive below it and travels with it.
- **The file's line endings**, taken from the file rather than assumed, so a fix never turns into a
  whole-file diff on the other platform.

## Licence

MIT. See [LICENSE](LICENSE).
