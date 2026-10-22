namespace LifeScript.Tests;

public class ColorTests
{
    private const string Life = """
        Properties:
            Dimensions = (8, 8)
        type life:
            born [3] survive [2, 3]
        endtype
        """;

    [Fact]
    public void A_single_type_gets_a_live_color_unlike_its_background()
    {
        var p = Compiler.Compile(Life);
        Assert.NotEqual(p.Palette[0], p.Palette[1]);
    }

    [Fact]
    public void Color_declarations_record_where_they_are()
    {
        string source = """
            type wire:
                States = 3
                setcolor to #102030
                statecolor 2 is #ff8a4c
            endtype
            """;
        var p = Compiler.Compile(source);
        var decl = p.Types[0].Decl;
        var color = decl.ColorRange!.Value;
        Assert.Equal("setcolor to #102030", source[color.Start..color.End]);
        var state = decl.StateColorRanges[2];
        Assert.Equal("statecolor 2 is #ff8a4c", source[state.Start..state.End]);
    }
}
