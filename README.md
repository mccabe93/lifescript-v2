# lifescript sandbox

A testbed for **lifescript v2**, the rule language planned for [automata.surf](https://automata.surf).
It revives [lifescript](https://github.com/mccabe93/lifescript) (2013) and runs scripts on
[CellularAutomata.NET](https://github.com/mccabe93/CellularAutomata.NET).

- [docs/LANGUAGE.md](docs/LANGUAGE.md): the language as implemented here
- [docs/NOTES.md](docs/NOTES.md): open design questions to try out
- [samples/](samples): Life, Rule 30, Wireworld, Brian's Brain, forest fire, rain, Larger than Life, 3D rules, and the original v1 `conway.ls` unchanged

## Setup

Requires the .NET 10 SDK.

```bash
git clone --recurse-submodules <this repo>
# or, in an existing clone:
git submodule update --init
```

`external/CellularAutomata.NET` is a submodule pinned to the `claude-01` branch of
CellularAutomata.NET-innerivy, which has the update modes and neighbor tables this sandbox uses.

## Viewer

```bash
dotnet run --project src/LifeScript.Viewer -- samples/conway.ls
```

A Silk.NET + Dear ImGui window: the script on the left, the world in the middle, controls and the
cell inspector on the right.

- Edit in the window: it recompiles 0.4 s after you stop typing (untick **Live** to compile with
  Ctrl+Enter). Ctrl+S saves.
- Or edit the file in your own editor: the viewer reloads it when it's saved.
- Errors show under the editor with line:column. If the new script doesn't compile, the old one
  keeps running.

| Input | 2D | 3D |
|---|---|---|
| Space / S / R | play-pause / step / reset | same |
| Left drag | paint with the brush (pick it in the Cells list) | orbit |
| Right click | inspect a cell: neighbor sums, locals, `print` output, next state | |
| Right drag, middle drag | pan | orbit |
| Wheel | zoom at the cursor | zoom |
| F | fit | |

3D worlds also have a slice view, where you can paint and inspect.

## CLI

```bash
dotnet run --project src/LifeScript.Cli -- info samples/wireworld.ls     # how it compiles
dotnet run --project src/LifeScript.Cli -- run samples/rule30.ls -n 30   # text output
dotnet run --project src/LifeScript.Cli -- parity samples/bugs.ls        # lookup tables vs interpreter
dotnet run --project src/LifeScript.Cli -- bench samples/conway.ls -n 500
```

## Tests

```bash
dotnet test
```

Covers the parser and its error positions, the locality rules, Life/Wolfram/Wireworld behavior,
2D and 3D neighborhoods against a naive reference stepper, deterministic randomness, active-cell
vs all-cell stepping, the world block, the inspector, and lookup-table vs interpreter parity for
every sample.

## Layout

```
src/LifeScript          lexer, parser, compiler (binder + lookup tables), runtime on CellularAutomata.NET
src/LifeScript.Cli      check / info / run / parity / bench
src/LifeScript.Viewer   Silk.NET + ImGui testbed
tests/LifeScript.Tests
samples/                *.ls scripts
external/               CellularAutomata.NET (submodule)
```
