# Open questions

Things the sandbox does one way for now, where it's worth trying the alternatives before
committing to them for automata.surf.

## Semantics

1. **What does `become` do to the state?** It keeps the state number (as in v1). In
   `forest-fire.ls` that splits trees into "tree 0" (grown from ground) and "tree 1" (placed by
   Start), which is invisible but shows up in the census. Alternatives: `become` resets to 0, or
   to 1.
2. **Should every type have a dead and an alive state?** Types get at least 2 states, so
   `empty`, `head` and `tail` in Wireworld each have an unused one. Types that never mention
   `alive`, `dead` or `setstate` could have just one.
3. **Edges.** Neighbors past a non-wrapping edge read as the default type, state 0. So
   `rule30.ls` can't tell "top row" from "empty row above", and Wolfram rules where 000 → 1 (odd
   numbers) don't work. Options: an `edge` value or `neighbors present`, or a
   `Boundary = "wall"` property naming the type outside the world.
4. **`States = N` friction.** `setstate to 1 - state` needs `States = 2`, because the compiler
   can't bound the expression. Smarter range inference, or default to the largest state used
   anywhere?
5. **Globals from `world:`** make every reading type interpreted and evaluated every step.
   Should rules be able to declare parameters (sliders in the player) that stay constant during
   a run, so they keep lookup tables?
6. **Sums other than counts.** `neighbors` adds weights of matching cells. There's no way to sum
   neighbor states (useful for multi-state totalistic and continuous rules), e.g.
   `sum of state over neighbors`.

## Syntax

7. **Keyword blocks.** `if ... then ... endif` and `endtype` keep the v1 feel, and
   single-line ifs read fine. Is that the identity, or would indentation or braces be nicer
   to type?
8. **Precedence.** v1 gave `&` and `|` the same precedence; v2 makes `&` bind tighter. `!(n in
   [9..26])` needs parentheses. Add `not in`?
9. **Rule-table shorthand.** `born [3] survive [2, 3]` exists now. In B/S notation unlisted
   counts mean dead, but elsewhere a cell that writes nothing keeps its state, so the dying is
   built into `born/survive` (see `life-3d-b4.ls`). Still open: a Generations form, e.g.
   `born [2] survive [] decay 3`, for rules like Brian's Brain.
10. **Properties.** Multi-word names (`Default Type`) are a fixed list. Keep them, or switch to
    single identifiers?

## Engine

11. **Speed.** The built-in engine (flat array, active cells, single thread) runs forest fire
    at ~7 ms/step on 30k cells and Amoeba 3D at ~8 ms (Debug build). The interpreter is most of
    that. Next: threads, then a GPU engine generated from the same lookup tables. Engines plug
    in through `IEngine`, so the language doesn't depend on any of them.
12. **3D starting patterns.** RLE is 2D only; 3D worlds can use `random` and placements.
13. **History view.** v1's `addrow` grew the world to show 1D rules over time. A render mode that
    stacks generations would do that without changing the world.
