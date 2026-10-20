namespace LifeScript.CellularAutomataNet.Tests;

/// <summary>The same script gives the same grids on CellularAutomata.NET and the built-in engine.</summary>
public class EngineParityTests
{
    public static TheoryData<string> Samples() =>
        new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.ls").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleMatchesBuiltInEngine(string sample)
    {
        var program = Compiler.Compile(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", sample)));
        var builtIn = new Simulation(program);
        var library = new Simulation(program, engine: new CellularAutomataNetEngine());
        for (int g = 0; g < 40; g++)
        {
            builtIn.Step();
            library.Step();
            Assert.True(builtIn.Hash() == library.Hash(), $"{sample}: grids differ at generation {g + 1}");
        }
    }

    [Fact]
    public void PaintingMatchesBuiltInEngine()
    {
        var program = Compiler.Compile(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "conway.ls")));
        var builtIn = new Simulation(program);
        var library = new Simulation(program, engine: new CellularAutomataNetEngine());
        byte alive = program.IdOf(0, 1);
        int[] glider = [builtIn.IndexOf([3, 30]), builtIn.IndexOf([4, 31]), builtIn.IndexOf([2, 32]), builtIn.IndexOf([3, 32]), builtIn.IndexOf([4, 32])];
        builtIn.Paint(glider, alive);
        library.Paint(glider, alive);
        for (int g = 0; g < 40; g++)
        {
            builtIn.Step();
            library.Step();
            Assert.Equal(builtIn.Hash(), library.Hash());
        }
    }
}
