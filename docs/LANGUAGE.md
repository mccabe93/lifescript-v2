# lifescript v2 (draft)

A language for cellular automata, revived from [lifescript](https://github.com/mccabe93/lifescript)
(2013, Java + ANTLR) for automata.surf. This sandbox implements the draft below.

The main change from v1 is **locality**: a cell reads only its own neighborhood and writes
only itself. That one rule makes every script deterministic, safe to run anywhere, and
analyzable, so many scripts compile to lookup tables (and later GPU shaders) automatically.

## A script

```
// comments run to the end of the line; whitespace and newlines don't matter
rule = 30                          // top-level globals (constants if never reassigned)

Properties:
    Title = "Rule 30"
    Dimensions = (201, 100)
    Start = ("cell", (100, 0))

type cell:
    Neighborhood[3] = { 4,   2, 1,
                        0, VAR, 0 }
    if neighbors == 0 then return endif
    setstate to bit(rule, neighbors)
endtype
```

A script is any number of these, in any order:

| Block | Purpose |
|---|---|
| `Properties:` | World size, wrapping, seed, starting cells |
| `type name: ... endtype` | A kind of cell: its neighborhood, colors and rule |
| `world: ... endworld` | Runs once per generation, before the cells. The only place with global reach |
| `def f(a, b): ... enddef` | A helper function |
| `name = expr` | A global variable |

## Properties

| Property | Example | Default |
|---|---|---|
| `Title` | `"Brian's Brain"` | `"Untitled"` |
| `Dimensions` | `(128, 128)` or `(32, 32, 32)` | `(128, 128)`. 1 to 3 dimensions |
| `Wraparound` | `true`, or per dimension `(true, false)` | `false` |
| `Default Type` | `"empty"` | the first type |
| `Seed` | `42` | `1` |
| `Start` | see below | nothing |
| `Interval` | `0.1` (seconds per generation in the viewer) | `0.1` |
| `Generations` | `500` (the viewer pauses there) | no limit |
| `Cell Size` | accepted from v1 | |
| `Pausable`, `Steppable` | accepted from v1 with a warning; the player always has these controls | |

`Start` is a comma-separated list (braces optional) of:

```
("life", (10, 12))                               // one cell, state 1
("neuron", (10, 12), 2)                          // one cell, given state
random 0.3 of "life"                             // fill the world at 30%
random 0.3 of "life" in (10, 10) to (50, 50)     // ... inside a box (inclusive)
random 0.3 of "neuron" with state 2              // ... with a state
pattern "bo$2bo$3o!" of "life" at (10, 10)       // a Golly RLE pattern (o = 1, A-X = 1-24)
```

Later items overwrite earlier ones. Coordinates can use globals and `world.width` etc.

## Cells: types and states

Every cell has a **type** and a **state number**. State 0 is dead; anything above 0 is alive.
The default type, state 0, fills the world, and also stands in for neighbors outside a
non-wrapping edge.

Within a type, statements run top to bottom. Reads (`state`, `alive`, `neighbors`, ...)
always see the **previous** generation; writes set the **next** one, and the last write wins.
If nothing is written, the cell stays as it is.

| Statement | Effect |
|---|---|
| `alive` / `dead` | next state 1 / 0 |
| `setstate to expr` | next state (clamped to the type's states) |
| `become "T"` | next type T, same state number |
| `become "T" with state expr` | next type and state |
| `return` | stop evaluating this cell |
| `print "n = ", n` | shown in the viewer's cell inspector |
| `born [3] survive [2, 3]` | the B/S rule notation (below) |

### born / survive

`born [3] survive [2, 3]` is Life, written the way Golly and LifeWiki write B3/S23. A dead cell
comes alive when `neighbors` is in the born list. A live cell stays alive when it's in the
survive list, **and dies otherwise**. In B/S notation every count that isn't listed means dead,
while elsewhere in lifescript a cell that writes nothing keeps its state, so the dying is built
into this statement. It's short for:

```
if alive then
    if !(neighbors in [2, 3]) then dead endif
elseif neighbors in [3] then alive endif
```

Lists take ranges (`born [3, 6..8]`), and either list can be empty: `born [4] survive []` is
B4/S, where every live cell lives for exactly one generation. It goes directly inside a type,
at most once, and statements after it can still override it (the last write wins).

Declarations inside a type (anywhere in it):

| Declaration | Meaning |
|---|---|
| `Neighborhood = moore(1)` | built-in: `moore(r)`, `vonneumann(r)`, `none`. r is 1 to 8. The default is `moore(1)` |
| `Neighborhood[3] = { ... }` | drawn: a grid of weights with `VAR` marking the cell itself (see below) |
| `States = 4` | number of states. Usually inferred; required when a computed state can't be bounded |
| `statecolor 2 is #3a5ba0` | color for one state. Colors: `#rgb`, `#rrggbb`, `(r, g, b)`, `("#rrggbb")` |
| `setcolor to (255, 200, 0)` | color for every state of the type |

### Drawn neighborhoods

The number in brackets is the width. Rows follow; `VAR` can be anywhere, and it defines the
origin. Each entry is a **weight**. `0` means "not a neighbor".

```
Neighborhood[3] = { 1,   1, 1,          Neighborhood[3] = { 4,   2, 1,
                    1, VAR, 1,                              0, VAR, 0 }
                    1,   1, 1 }
// Moore: neighbors = live count       // the three cells above: neighbors = 4L + 2C + R
```

For 3D, give width and height and separate z-slices with `;`:

```
Neighborhood[3, 3] = { 1, 1, 1,  1, 0, 1,  1, 1, 1 ;
                       1, 1, 1,  1, VAR, 1,  1, 1, 1 ;
                       1, 1, 1,  1, 0, 1,  1, 1, 1 }
```

### Reading the neighborhood

| Expression | Value |
|---|---|
| `neighbors` | sum of weights of **alive** neighbors (any type) |
| `neighbors that are "T"` | sum of weights of neighbors of type T (any state) |
| `neighbors with state 2` | sum of weights of neighbors in state 2 (any type) |
| `neighbors that are "T" with state 2` | both filters |
| `neighbor at (dx, dy)` | that neighbor's state; offsets must be constants |
| `neighbor at (dx, dy) is "T"` | 1 if that neighbor is of type T |

### Other values

`state`, `alive`, `dead`, `age` (generations since this cell last changed), `generation`,
`cell.x`, `cell.y`, `cell.z`, `world.width`, `world.height`, `world.depth`, globals, and
function calls.

## Expressions

Numbers (`3`, `0.25`), `true`/`false`, `+ - * / %` (division and modulo by 0 give 0; `%` is
always non-negative), comparisons `== != < <= > >=`, logic `& | !` (also `and or not`;
`&` binds tighter than `|`), and membership: `n in [2, 3]`, `n in [3, 6..8]` (ranges inclusive).

Built-in functions: `random(a, b)` (whole number, inclusive), `chance(p)` (1 with probability p),
`bit(n, i)`, `abs`, `floor`, `ceil`, `round`, `sqrt`, `sin`, `cos`, `min`, `max`, `clamp(v, lo, hi)`.

Randomness is a hash of (seed, generation, cell, draw number), so a script with the same seed
always produces the same run, regardless of evaluation order.

## Control flow

```
if cond then ... elseif cond then ... else ... endif
for i = 1 to 10 step 2 ... next i          // at most 10,000 iterations
```

There's no `while`, and functions can't recurse: every cell must finish in bounded time.

## Functions

```
def born(n):
    return n == 3 | n == 6
enddef
```

Functions see their parameters, globals and `generation`, not the cell. Pass what they need.

## The world block

Runs once per generation before the cells. Assignments here set globals, which cells can read
but not write. Only here:

| | |
|---|---|
| `create "T" at (x, y) [with state n]` | place a cell |
| `kill at (x, y)` | reset to the default type, state 0 |
| `setstate of (x, y) to n` | set a state |
| `count "T" [with state n]` | number of cells of a type |
| `state of (x, y)`, `cell at (x, y) is "T"` | read any cell |

## How a script runs

The compiler decides two things per script, shown by `lifescript-cli info` and in the viewer:

- **Lookup table or interpreter**, per type. A type gets a table when its next state depends
  only on its own state and its `neighbors ...` sums (no `random`, `generation`, `age`,
  `cell.x`, world globals or `neighbor at`). Top-level constants and functions are fine.
- **Active cells or all cells.** If every rule is a pure function of the neighborhood and an
  empty region stays empty, only cells near a change are evaluated each step.

## Removed from v1

| v1 | Why | Instead |
|---|---|---|
| `setstate of`, `create`, `kill`, `setcolor at`, `cell at`, `state of`, `neighbors of` inside a type | Writes and reads anywhere make results depend on scan order | Use them in `world:` |
| `while` | Can hang | `for` |
| `addrow`, `addcolumn` | Worlds have a fixed size | A 2D world where each row reads the row above (see `rule30.ls`) |
| `input`, `end` | Not meaningful in a player | |
| Recursive functions | Unbounded | Loops |
