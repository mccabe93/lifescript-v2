namespace LifeScript.Tests;

/// <summary>Checks the engine against a naive, obviously-correct stepper.</summary>
public class ReferenceTests
{
    [Theory]
    [InlineData("amoeba-3d.ls")]
    [InlineData("life-3d-extruded.ls")]
    [InlineData("life-like.ls")]
    [InlineData("bugs.ls")]
    [InlineData("brians-brain.ls")]
    public void Single_type_samples_match_a_naive_stepper(string file)
    {
        var program = Compiler.Compile(File.ReadAllText(Path.Combine("samples", file)));
        var sim = new Simulation(program);
        var type = Assert.Single(program.Types);
        var ids = sim.Ids.ToArray();
        var ctx = new EvalContext(program);
        Lut.EnsureQueries(ctx, type.Queries.Count);

        for (int g = 0; g < 8; g++)
        {
            var next = new byte[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                var p = sim.PositionOf(i);
                for (int q = 0; q < type.Queries.Count; q++)
                {
                    double sum = 0;
                    foreach (var (union, weight) in type.Weights)
                    {
                        int j = sim.IndexOf(p.Zip(program.UnionOffsets[union], (a, b) => a + b).ToArray());
                        byte neighbor = j >= 0 ? ids[j] : program.DefaultId;
                        if (type.Queries[q].Matches(program, neighbor))
                        {
                            sum += weight;
                        }
                    }
                    ctx.Queries[q] = sum;
                }
                // The script's own rule, through the interpreter, on independently computed sums.
                next[i] = CellEvaluator.Interpret(ctx, type, program.StateOf[ids[i]]);
            }
            ids = next;
            sim.Step();
            Assert.Equal(ids, sim.Ids.ToArray());
        }
    }
}
