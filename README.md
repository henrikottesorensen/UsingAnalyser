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

One key, in `.editorconfig`:

```ini
[*.cs]
using_first_party_prefixes = SolutionPrefix
```

Comma-separated for a solution spanning several roots (`Contoso.Platform, Contoso.Internal`).
Matching is ordinal, because namespaces are case sensitive, and a root only matches at a dot
boundary, so `System` never swallows `SystemsManager`.

Leaving it unset is a legitimate configuration rather than an error: the scheme collapses to
System-then-everything-else.

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

Or as a package, `dotnet pack UsingAnalyser.CodeFixes` (package id `UsingGroupAnalyser`), which puts
both assemblies where Roslyn looks for them.

## Turning it on in an existing repository

`dotnet format` applies the fix in bulk:

```bash
dotnet format analyzers YourSolution.slnx --diagnostics UA1000 UA1001 --severity warn
```

Two settings have to give way first, or they will fight the new layout:

- **SA1210 must go to `none`.** This is not optional. SA1210 sorts the whole list alphabetically, and
  a first-party root that sorts before a vendor - `Homespool` before `Microsoft`, say - is exactly
  what this scheme moves to the bottom. With SA1210 on, every fixed file becomes an SA1210 warning,
  and under `TreatWarningsAsErrors` that is a broken build. UA1000 takes over sorting entirely.
- **`dotnet_separate_import_directive_groups` should go to `false`,** or the editor's sort-usings
  action will regroup by first-level namespace behind you.

SA1208, SA1209, SA1211, SA1216 and SA1217 can stay: the layout puts System first, and keeps
`using static` and aliases as trailing blocks in the order those rules want.

## What it deliberately leaves alone

- **Global usings.** The compiler already pins them to the front, and moving a using across that
  boundary is a change in meaning rather than in layout.
- **Any file with a `#if` around a using.** The position of a conditioned using is meaningful, and
  layout is not worth changing which usings the compiler sees.
- **A file header.** A comment above the first using that is followed by a blank line stays at the
  top. A comment with no blank line under it belongs to the directive below it and travels with it.
- **The file's line endings**, taken from the file rather than assumed, so a fix never turns into a
  whole-file diff on the other platform.
