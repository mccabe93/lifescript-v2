namespace LifeScript;

/// <summary>
/// Steps the grid of a <see cref="Simulation"/>. The simulation owns the grid (one (type, state)
/// id per cell, first dimension fastest), runs Start and the world block, and tracks ages; an
/// engine only decides which cells to evaluate, gathers their neighbors and writes the next
/// generation. <see cref="DenseEngine"/> is the built-in one; others can run the same rules on a
/// different library or device.
/// </summary>
public interface IEngine
{
    /// <summary>Starts over with <paramref name="ids"/> as the grid. The engine may keep the array.</summary>
    void Reset(CellRule rule, byte[] ids);

    /// <summary>The caller has already written these cells into the grid (painting, world edits).</summary>
    void Edited(IEnumerable<int> indices);

    /// <summary>Advances one generation, leaving it in the grid. Returns the number of cells evaluated.</summary>
    int Step();
}

/// <summary>
/// A compiled script's cell rule, as an engine sees it. To evaluate a cell, write the id of each
/// neighbor it reads into <see cref="Neighbors"/>, then call <see cref="Next"/>.
/// </summary>
public sealed class CellRule
{
    private readonly EvalContext _ctx;
    private readonly ushort[] _ages;

    internal CellRule(CompiledProgram program, EvalContext ctx, ushort[] ages)
    {
        Program = program;
        _ctx = ctx;
        _ages = ages;
    }

    public CompiledProgram Program { get; }

    public int[] Size => Program.Size;
    public bool[] Wrap => Program.Wrap;

    /// <summary>Every neighbor offset any type reads. Indices into this are "neighbor slots".</summary>
    public int[][] Offsets => Program.UnionOffsets;

    /// <summary>The id of empty space, and of neighbors past a non-wrapping edge.</summary>
    public byte DefaultId => Program.DefaultId;

    /// <summary>
    /// When false, a cell can only change if it or one of its neighbors changed in the last
    /// generation, and empty space stays empty, so engines may skip the rest.
    /// </summary>
    public bool EvaluateAllCells => Program.UpdateMode == UpdateMode.AllCells;

    /// <summary>Neighbor slots a cell with this id reads.</summary>
    public ReadOnlySpan<int> SlotsUsedBy(byte id) => Program.Types[Program.TypeOf[id]].UsedUnion;

    /// <summary>Neighbor ids by slot. Only the slots in <see cref="SlotsUsedBy"/> need filling.</summary>
    public Span<byte> Neighbors => _ctx.Neighbors;

    internal bool UseLut { get; set; } = true;

    /// <summary>The next id of the cell at <paramref name="index"/>, currently <paramref name="id"/>.</summary>
    public byte Next(int index, byte id)
    {
        var type = Program.Types[Program.TypeOf[id]];
        var c = _ctx;
        c.CellIndex = index;
        if (type.UsesAge)
        {
            c.Age = _ages[index];
        }
        if (type.UsesPosition)
        {
            for (int d = 0; d < Program.Size.Length; d++)
            {
                c.Position[d] = index % Program.Size[d];
                index /= Program.Size[d];
            }
        }
        return CellEvaluator.Evaluate(c, type, id, UseLut);
    }
}
