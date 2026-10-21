namespace LifeScript.Tests;

public class BornSurviveTests
{
    private static Simulation Run(string source, int generations)
    {
        var sim = new Simulation(Compiler.Compile(source));
        for (int i = 0; i < generations; i++)
        {
            sim.Step();
        }
        return sim;
    }

    private const string Start = """
        Properties:
            Dimensions = (40, 40)
            Wraparound = true
            Seed = 9
            Start = random 0.4 of "life"
        """;

    [Fact]
    public void Born_survive_is_life_written_out()
    {
        var shorthand = Run($"{Start}\ntype life:\n    born [3] survive [2, 3]\nendtype", 30);
        var longhand = Run(
            $"""
            {Start}
            type life:
                if alive then
                    if !(neighbors in [2, 3]) then dead endif
                elseif neighbors == 3 then alive endif
            endtype
            """,
            30
        );
        Assert.Equal(longhand.Hash(), shorthand.Hash());
    }

    [Fact]
    public void An_empty_survive_list_kills_every_live_cell()
    {
        var sim = Run($"{Start}\ntype life:\n    born [] survive []\nendtype", 1);
        Assert.All(sim.Ids.ToArray(), id => Assert.Equal(0, id));
    }

    [Fact]
    public void The_3D_sample_matches_its_shorthand()
    {
        string sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "life-3d-b4.ls"));
        int start = sample.IndexOf("    if dead then", StringComparison.Ordinal);
        int end = sample.IndexOf("endtype", StringComparison.Ordinal);
        string shorthand = sample[..start] + "    born [4] survive []\n" + sample[end..];
        Assert.Equal(Run(sample, 20).Hash(), Run(shorthand, 20).Hash());
    }

    [Fact]
    public void Ranges_expand_and_the_rule_gets_a_lookup_table()
    {
        var program = Compiler.Compile("type cell:\n    born [3, 6..8] survive [3, 4, 6..8]\nendtype");
        var rule = program.Types[0].Decl.BornSurvive!;
        Assert.Equal([3, 6, 7, 8], rule.Born);
        Assert.Equal([3, 4, 6, 7, 8], rule.Survive);
        Assert.NotNull(program.Types[0].Lut);
        Assert.Equal(UpdateMode.ActiveCells, program.UpdateMode);
    }

    [Fact]
    public void Source_ranges_cover_the_declarations()
    {
        const string source = "type cell:\n    Neighborhood = vonneumann(2)\n    born [1] survive [1, 2]\nendtype\n";
        var decl = Compiler.Compile(source).Types[0].Decl;
        var n = decl.NeighborhoodRange!.Value;
        var r = decl.BornSurvive!.Range;
        Assert.Equal("Neighborhood = vonneumann(2)", source[n.Start..n.End]);
        Assert.Equal("born [1] survive [1, 2]", source[r.Start..r.End]);
        Assert.Equal("type cell:", source[..decl.HeaderEnd]);
        Assert.Equal("endtype", source[decl.BodyEnd..(decl.BodyEnd + 7)]);
    }

    [Theory]
    [InlineData("type c:\n    born [3] survive [2]\n    born [3] survive [2]\nendtype", "declared twice")]
    [InlineData("type c:\n    if alive then born [3] survive [2] endif\nendtype", "directly inside the type")]
    [InlineData("type c:\n    born [1.5] survive []\nendtype", "whole numbers")]
    [InlineData("type c:\n    born [3]\nendtype", "survive")]
    public void Errors(string source, string message)
    {
        var e = Assert.Throws<LifeScriptException>(() => Compiler.Compile(source));
        Assert.Contains(message, e.Diagnostics[0].Message);
    }
}
