namespace LifeScript.Tests;

public class LanguageTests
{
    private const string LifeRule = """
        type life:
            Neighborhood = moore(1)
            if alive then
                if neighbors < 2 | neighbors > 3 then dead endif
            else
                if neighbors == 3 then alive endif
            endif
        endtype
        """;

    private static Simulation Run(string source, int generations, bool useLut = true)
    {
        var sim = new Simulation(Compiler.Compile(source), useLut);
        for (int i = 0; i < generations; i++)
        {
            sim.Step();
        }
        return sim;
    }

    private static string Grid(Simulation sim) => AsciiRenderer.Render(sim.Program, sim.Ids);

    private static LifeScriptException CompileError(string source) =>
        Assert.Throws<LifeScriptException>(() => Compiler.Compile(source));

    [Fact]
    public void Blinker_oscillates()
    {
        var source = $"""
            Properties:
                Dimensions = (5, 5)
                Start = ("life", (1, 2)), ("life", (2, 2)), ("life", (3, 2))
            {LifeRule}
            """;
        Assert.Equal(".....\n..#..\n..#..\n..#..\n.....\n", Grid(Run(source, 1)));
        Assert.Equal(".....\n.....\n.###.\n.....\n.....\n", Grid(Run(source, 2)));
    }

    [Fact]
    public void Glider_moves_one_cell_diagonally_every_four_generations()
    {
        var source = $"""
            Properties:
                Dimensions = (8, 8)
                Wraparound = true
                Start = pattern "bo$2bo$3o!" of "life" at (1, 1)
            {LifeRule}
            """;
        var start = Run(source, 0).Ids.ToArray();
        var after = Run(source, 4).Ids.ToArray();
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Assert.Equal(start[x + y * 8], after[(x + 1) % 8 + (y + 1) % 8 * 8]);
            }
        }
    }

    [Fact]
    public void Original_lifescript_conway_still_compiles_and_runs()
    {
        var program = Compiler.Compile(File.ReadAllText("samples/original-conway.ls"));
        Assert.Contains(program.Warnings, w => w.Message.Contains("Pausable"));
        Assert.Equal([60, 60], program.Size);
        var sim = new Simulation(program);
        int initial = sim.Census()[1];
        for (int i = 0; i < 30; i++)
        {
            sim.Step();
        }
        // The glider gun has emitted gliders, so the population grew.
        Assert.True(sim.Census()[1] > initial);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(110)]
    public void Weighted_neighborhood_reproduces_wolfram_rules(int rule)
    {
        var source = $$"""
            rule = {{rule}}
            Properties:
                Dimensions = (41, 20)
                Start = ("cell", (20, 0))
            type cell:
                Neighborhood[3] = { 4, 2, 1,
                                    0, VAR, 0 }
                if neighbors == 0 then return endif
                setstate to bit(rule, neighbors)
            endtype
            """;
        var sim = Run(source, 20);
        Assert.NotNull(sim.Program.Types[0].Lut);

        // Reference: classic 1D evolution, row y is generation y. Pattern 000 is excluded
        // (it would also flip the empty rows), which only matters for odd rules.
        var row = new int[41];
        row[20] = 1;
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 41; x++)
            {
                Assert.Equal(row[x], sim.Program.StateOf[sim.Ids[x + y * 41]]);
            }
            var next = new int[41];
            for (int x = 0; x < 41; x++)
            {
                int l = x > 0 ? row[x - 1] : 0, c = row[x], r = x < 40 ? row[x + 1] : 0;
                next[x] = (rule >> (l * 4 + c * 2 + r)) & 1;
            }
            row = next;
        }
    }

    [Fact]
    public void Types_drive_wireworld()
    {
        var source = """
            Properties:
                Dimensions = (8, 3)
                Default Type = "empty"
                Start = pattern "8o!" of "copper" at (0, 1), ("tail", (1, 1)), ("head", (2, 1))
            type empty: Neighborhood = none endtype
            type copper:
                heads = neighbors that are "head"
                if heads == 1 | heads == 2 then become "head" endif
            endtype
            type head: Neighborhood = none become "tail" endtype
            type tail: Neighborhood = none become "copper" endtype
            """;
        Assert.Equal("........\nCTHCCCCC\n........\n", Grid(Run(source, 0)));
        Assert.Equal("........\nCCTHCCCC\n........\n", Grid(Run(source, 1)));
        Assert.Equal("........\nCCCTHCCC\n........\n", Grid(Run(source, 2)));
    }

    [Fact]
    public void Three_d_moore_neighborhood_has_26_neighbors()
    {
        var source = """
            Properties:
                Dimensions = (3, 3, 3)
                Start = random 1 of "cell"
            type cell:
                States = 32
                Neighborhood = moore(1)
                setstate to neighbors
            endtype
            """;
        var sim = Run(source, 1);
        Assert.Equal(26, sim.Program.StateOf[sim.Ids[13]]);
        Assert.Equal(7, sim.Program.StateOf[sim.Ids[0]]);
    }

    [Fact]
    public void Three_d_slices_define_offsets()
    {
        // Only the cell directly below (z - 1) counts.
        var source = """
            Properties:
                Dimensions = (3, 3, 3)
                Start = ("cell", (1, 1, 0))
            type cell:
                Neighborhood[3, 3] = { 0, 0, 0,  0, 1, 0,  0, 0, 0 ;
                                       0, 0, 0,  0, VAR, 0,  0, 0, 0 ;
                                       0, 0, 0,  0, 0, 0,  0, 0, 0 }
                if neighbors == 1 then alive endif
            endtype
            """;
        var sim = Run(source, 2);
        Assert.Equal(1, sim.Program.StateOf[sim.Ids[1 + 3 + 18]]);
    }

    [Fact]
    public void Random_is_reproducible_and_depends_on_seed()
    {
        string Script(int seed) => $$"""
            Properties:
                Dimensions = (40, 40)
                Seed = {{seed}}
                Start = random 0.4 of "cell"
            type cell:
                Neighborhood = moore(1)
                if chance(0.5) then if alive then dead else alive endif endif
            endtype
            """;
        Assert.Equal(Run(Script(1), 10).Hash(), Run(Script(1), 10).Hash());
        Assert.NotEqual(Run(Script(1), 10).Hash(), Run(Script(2), 10).Hash());
        Assert.Equal(UpdateMode.AllCells, Compiler.Compile(Script(1)).UpdateMode);
    }

    [Fact]
    public void Active_cell_stepping_matches_stepping_every_cell()
    {
        var source = File.ReadAllText("samples/conway.ls");
        var active = new Simulation(Compiler.Compile(source));
        Assert.Equal(UpdateMode.ActiveCells, active.Program.UpdateMode);
        var allProgram = Compiler.Compile(source);
        allProgram.UpdateMode = UpdateMode.AllCells;
        var all = new Simulation(allProgram);
        for (int i = 0; i < 60; i++)
        {
            active.Step();
            all.Step();
            Assert.Equal(all.Hash(), active.Hash());
        }
    }

    [Fact]
    public void Default_cell_that_changes_on_its_own_forces_all_cells()
    {
        var program = Compiler.Compile("""
            Properties:
                Dimensions = (4, 4)
            type cell:
                Neighborhood = moore(1)
                if neighbors == 0 then alive endif
            endtype
            """);
        Assert.Equal(UpdateMode.AllCells, program.UpdateMode);
    }

    [Fact]
    public void World_block_creates_cells_and_counts_them()
    {
        var source = """
            Properties:
                Dimensions = (10, 10)
            world:
                create "cell" at (generation, 0)
                total = count "cell" with state 1
                print "total ", total
            endworld
            type cell: Neighborhood = none endtype
            """;
        var sim = Run(source, 3);
        Assert.Equal(3, sim.Census()[1]);
        Assert.Equal(["total 3"], sim.WorldLog);
    }

    [Fact]
    public void Constant_globals_keep_lookup_tables_but_world_globals_dont()
    {
        var constant = Compiler.Compile("limit = 3\ntype c: if neighbors == limit then alive endif endtype");
        Assert.NotNull(constant.Types[0].Lut);
        var changing = Compiler.Compile("limit = 3\nworld: limit = generation % 4 endworld\ntype c: if neighbors == limit then alive endif endtype");
        Assert.Null(changing.Types[0].Lut);
        Assert.Contains("reads global limit", changing.Types[0].LutBlockers);
    }

    [Fact]
    public void Functions_are_inlined_into_lookup_tables()
    {
        var program = Compiler.Compile("""
            def born(n):
                return n == 3
            enddef
            type c:
                if born(neighbors) then alive endif
            endtype
            """);
        Assert.NotNull(program.Types[0].Lut);
    }

    [Fact]
    public void In_lists_and_ranges()
    {
        var program = Compiler.Compile("type c: States = 4 setstate to (neighbors in [1, 3..5]) + 2 * (neighbors in [8]) endtype");
        var sim = new Simulation(program);
        var ctx = new EvalContext(program);
        Lut.EnsureQueries(ctx, 1);
        foreach (var (n, expected) in new[] { (0, 0), (1, 1), (2, 0), (3, 1), (5, 1), (6, 0), (8, 2) })
        {
            ctx.Queries[0] = n;
            Assert.Equal(expected, program.StateOf[CellEvaluator.Interpret(ctx, program.Types[0], 0)]);
        }
    }

    [Fact]
    public void Inspector_reports_queries_locals_and_prints()
    {
        var source = """
            Properties:
                Dimensions = (5, 5)
                Start = ("c", (1, 2)), ("c", (2, 2)), ("c", (3, 2))
            type c:
                n = neighbors
                print "n=", n
                if n == 3 then alive endif
            endtype
            """;
        var sim = Run(source, 0);
        var inspection = sim.Inspect(2 + 1 * 5);
        Assert.Equal(3, inspection.Queries[0].Value);
        Assert.Equal(("n", 3.0), inspection.Locals[0]);
        Assert.Equal(["n=3"], inspection.Prints);
        Assert.Equal("state 1", inspection.Next);
    }

    [Theory]
    [InlineData("type c: setstate of (1, 1) to 1 endtype", "not allowed in a type")]
    [InlineData("type c: create \"c\" at (1, 1) endtype", "not allowed in a type")]
    [InlineData("type c: kill at (1, 1) endtype", "not allowed in a type")]
    [InlineData("type c: if cell at (0, 0) is \"c\" then alive endif endtype", "not allowed in a type")]
    [InlineData("type c: x = state of (0, 0) endtype", "not allowed in a type")]
    [InlineData("type c: x = neighbors of (0, 0) endtype", "own neighborhood")]
    [InlineData("type c: while alive do dead endwhile endtype", "while loops are not supported")]
    [InlineData("type c: addrow endtype", "fixed size")]
    [InlineData("def f(n): return f(n) enddef\ntype c: x = f(1) endtype", "Recursion is not allowed")]
    [InlineData("type c: x = y endtype", "Unknown variable y")]
    [InlineData("type c: become \"d\" endtype", "Unknown type \"d\"")]
    [InlineData("type c: setstate to neighbors endtype", "needs a States = N declaration")]
    [InlineData("g = 1\nworld: g = 2 endworld\ntype c: g = 3 endtype", "Cells can read it but not change it")]
    [InlineData("world: alive endworld\ntype c: endtype", "only be used inside a type")]
    [InlineData("type c: Neighborhood[3] = { 1, 1, 1, 1 } endtype", "isn't a multiple of its width")]
    [InlineData("type c: Neighborhood[3] = { 1, 1, 1 } endtype", "needs a VAR")]
    [InlineData("type c: x = neighbor at (dx, 0) endtype", "constant offsets")]
    public void Locality_and_other_errors_are_reported(string source, string message)
    {
        var error = CompileError(source);
        Assert.Contains(error.Diagnostics, d => d.Message.Contains(message));
    }

    [Fact]
    public void Errors_have_line_and_column()
    {
        var error = CompileError("type c:\n    if alive then\n        dead\nendtype");
        var d = Assert.Single(error.Diagnostics);
        Assert.Equal(4, d.Span.Line);
        Assert.Equal(1, d.Span.Column);
        Assert.Contains("endif", d.Message);
    }

    [Fact]
    public void Painted_cells_take_part_in_the_next_step()
    {
        var sim = new Simulation(Compiler.Compile($"Properties: Dimensions = (5, 5)\n{LifeRule}"));
        sim.Paint([1 + 2 * 5, 2 + 2 * 5, 3 + 2 * 5], sim.Program.IdOf(0, 1));
        sim.Step();
        Assert.Equal(".....\n..#..\n..#..\n..#..\n.....\n", Grid(sim));
    }

    [Fact]
    public void Runaway_for_loops_stop()
    {
        var program = Compiler.Compile("type c: for i = 1 to 100000 x = i next i endtype");
        Assert.Throws<LifeScriptRuntimeException>(() => new Simulation(program, useLut: false).Step());
    }

    [Fact]
    public void Rle_parses_runs_rows_and_states()
    {
        var cells = Rle.Parse("x = 3, y = 2\n2bo$A2B!");
        Assert.Equal([new Rle.RleCell(2, 0, 1), new Rle.RleCell(0, 1, 1), new Rle.RleCell(1, 1, 2), new Rle.RleCell(2, 1, 2)], cells);
    }

    public static TheoryData<string> Samples() => new(Directory.GetFiles("samples", "*.ls").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Samples_compile_and_lookup_tables_match_the_interpreter(string file)
    {
        var program = Compiler.Compile(File.ReadAllText(Path.Combine("samples", file)));
        var lut = new Simulation(program, useLut: true);
        var interp = new Simulation(program, useLut: false);
        for (int i = 0; i < 25; i++)
        {
            lut.Step();
            interp.Step();
            Assert.Equal(interp.Hash(), lut.Hash());
        }
    }
}
