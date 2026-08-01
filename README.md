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

Three keys, in `.editorconfig`. All of them carry the `usinglayout.` prefix, so nothing here can
collide with a built-in option, a StyleCop setting, or another analyser's.

```ini
[*.cs]
usinglayout.first_party_prefixes = SolutionPrefix
usinglayout.separate_system = true
usinglayout.separate_first_party = true
```

`first_party_prefixes` is comma-separated for a solution spanning several roots
(`Contoso.Platform, Contoso.Internal`). Matching is ordinal, because namespaces are case sensitive,
and a root only matches at a dot boundary, so `System` never swallows `SystemsManager`. Leaving it
unset is a legitimate configuration rather than an error: the scheme collapses to
System-then-everything-else.

The two `separate_*` keys default to `true` and control the blank lines independently:

| separate_system | separate_first_party | Result                                              |
|-----------------|----------------------|-----------------------------------------------------|
| `true`          | `true`               | Three blocks (the default).                          |
| `false`         | `true`               | System runs into third party; first party set apart. |
| `true`          | `false`              | System set apart; third party runs into first party. |
| `false`         | `false`              | One run, still ordered.                              |

Turning both off does not turn the scheme off - it stops the scheme being *visible*. UA1000 still
sorts System, then third party, then first party; there is just nothing between the blocks.

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

Both are warnings by default, both are fixable, and at most one is reported per file - the fix
rewrites the whole block in a single edit, so a report per misplaced line would be one problem
described N times.

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

Three settings, and the canonical layout then builds clean:

```ini
csharp_using_directive_placement = outside_namespace:error
dotnet_separate_import_directive_groups = false
dotnet_diagnostic.SA1210.severity = none
```

## Which other rules this touches

Measured rather than reasoned about, against StyleCop.Analyzers 1.2.0.556 with every `Style` category
rule at warning, on a canonical file including statics and aliases.

**Conflicts. These two must be configured, or the layout will not build clean.**

| Rule | What happens | Settle it with |
|------|--------------|----------------|
| `SA1210` | Sorts the whole list alphabetically, so it wants third party and first party interleaved - precisely the split this scheme creates. Every laid-out file becomes a warning, and under `TreatWarningsAsErrors` a broken build. | `dotnet_diagnostic.SA1210.severity = none`. UA1000 takes over sorting entirely. |
| `IDE0055` | Fires *only* when `dotnet_separate_import_directive_groups = true`, because that option wants blank lines by first-level namespace. Under `EnforceCodeStyleInBuild` it is a build warning, not merely the editor regrouping behind you. | `dotnet_separate_import_directive_groups = false`, which clears it with everything else held constant. |

**A trap that is not this analyser's doing.** `SA1200` fires on every using under StyleCop's defaults,
because it wants them *inside* the namespace. Declaring
`csharp_using_directive_placement = outside_namespace` silences it - StyleCop honours that option.
Either placement works here, so this only decides which shape you are enforcing, not whether the
analyser applies.

**Compatible, verified silent on the canonical layout:** `SA1208` (System first), `SA1209` (aliases
last), `SA1211` (aliases alphabetical), `SA1216` and `SA1217` (`using static` placement and order),
`SA1516` - including with `stylecop.layout.allowConsecutiveUsings = false` - and
`dotnet_sort_system_directives_first = true`. `IDE0005` is orthogonal: it removes usings nothing
needs, which is a separate question from where the rest go.

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
