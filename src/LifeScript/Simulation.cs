using System.Diagnostics;
using System.Numerics;
using CellularAutomata.NET;

namespace LifeScript;

public sealed record CellInspection(
    int Index,
    int[] Position,
    string Type,
    int State,
    int Age,
    IReadOnlyList<(string Query, double Value)> Queries,
    IReadOnlyList<(string Name, double Value)> Locals,
    IReadOnlyList<string> Prints,
    string Next,
    string? Error
);

/// <summary>
/// Runs a compiled script on CellularAutomata.NET. Each cell's state is one byte: a
/// (type, state) id from <see cref="CompiledProgram"/>.
/// </summary>
public sealed class Simulation
{
    private readonly EvalContext _ctx;
    private readonly CellularAutomataDimension[] _dimensions;
    private CellularAutomata<byte> _automaton = null!;
    private byte[] _ids;
    private byte[] _previous;
    private ushort[] _ages;
    private readonly bool _needsAge;

    public Simulation(CompiledProgram program, bool useLut = true)
    {
        Program = program;
        UseLut = useLut;
        _ctx = new EvalContext(program);
        Lut.EnsureQueries(_ctx, program.MaxQueries);
        _dimensions = program.Size
            .Select((size, d) => new CellularAutomataDimension { Cells = size, WrapStart = program.Wrap[d], WrapEnd = program.Wrap[d] })
            .ToArray();
        _ids = new byte[program.CellCount];
        _previous = new byte[program.CellCount];
        _ages = new ushort[program.CellCount];
        _needsAge = program.Types.Any(t => t.UsesAge);
        Reset();
    }

    public CompiledProgram Program { get; }

    /// <summary>Use lookup tables where available. Off forces the interpreter (for parity checks).</summary>
    public bool UseLut { get; set; }

    public long Generation => _automaton.Generation;

    /// <summary>Current (type, state) id of every cell, first dimension fastest.</summary>
    public ReadOnlySpan<byte> Ids => _ids;

    public ReadOnlySpan<ushort> Ages => _ages;

    public double LastStepMs { get; private set; }

    public int LastActiveCells { get; private set; }

    /// <summary>Output of print statements in the world block during the last step.</summary>
    public List<string> WorldLog { get; } = new();

    public void Reset()
    {
        var config = new CellularAutomataConfiguration<byte>
        {
            Dimensions = _dimensions,
            DefaultState = Program.DefaultId,
            UpdateMode = Program.UpdateMode == UpdateMode.AllCells
                ? CellularAutomataUpdateMode.AllCells
                : CellularAutomataUpdateMode.ActiveCells,
        };
        var neighborhood = new CellularAutomataNeighborhood<byte>
        {
            NeighborLocations = Program.UnionOffsets.Select(o => AutomataVector.Create(o)).ToArray(),
        };
        _automaton = new CellularAutomata<byte>(config, neighborhood, Rule);

        Array.Clear(_ctx.Globals);
        Array.Fill(_ids, Program.DefaultId);
        Array.Clear(_ages);
        _ctx.Generation = 0;
        _ctx.CellIndex = -1;
        _ctx.World = new WorldAccess(Program, _ids);
        _ctx.BeginFrame(Program.GlobalInitLocals);
        _ctx.Trace = WorldLog;
        WorldLog.Clear();
        try
        {
            Stmt.ExecBlock(Program.GlobalInits, _ctx);
            ApplyStart();
        }
        finally
        {
            _ctx.World = null;
            _ctx.Trace = null;
        }
        _automaton.InitializeGrid(_ids);
        _ids.CopyTo(_previous, 0);
    }

    private void ApplyStart()
    {
        var world = _ctx.World!;
        foreach (var item in Program.Start)
        {
            switch (item)
            {
                case PlaceStart place:
                {
                    int index = ResolveStart(place.Position);
                    world.Set(index, TypeIndex(place.TypeName), place.State?.Eval(_ctx) ?? 1, _ctx);
                    break;
                }
                case RandomStart random:
                {
                    int t = TypeIndex(random.TypeName);
                    double p = random.Probability.Eval(_ctx);
                    int[] from = random.From == null ? new int[Program.Size.Length] : Coordinates(random.From);
                    int[] to = random.To == null ? Program.Size.Select(s => s - 1).ToArray() : Coordinates(random.To);
                    for (int i = 0; i < Program.CellCount; i++)
                    {
                        var position = PositionOf(i);
                        bool inside = true;
                        for (int d = 0; d < position.Length; d++)
                        {
                            inside &= position[d] >= Math.Min(from[d], to[d]) && position[d] <= Math.Max(from[d], to[d]);
                        }
                        // Generation -1 keeps Start's draws separate from step 0's.
                        if (inside && Rng.Unit(Program.Seed, -1, i, 0) < p)
                        {
                            world.Set(i, t, random.State?.Eval(_ctx) ?? 1, _ctx);
                        }
                    }
                    break;
                }
                case PatternStart pattern:
                {
                    int t = TypeIndex(pattern.TypeName);
                    var origin = Coordinates(pattern.Position);
                    foreach (var cell in Rle.Parse(pattern.Rle))
                    {
                        var position = (int[])origin.Clone();
                        position[0] += cell.X;
                        if (position.Length > 1)
                        {
                            position[1] += cell.Y;
                        }
                        int index = IndexOf(position);
                        if (index < 0)
                        {
                            throw new LifeScriptRuntimeException("The pattern doesn't fit in the world at that position.", pattern.Span);
                        }
                        world.Set(index, t, cell.State, _ctx);
                    }
                    break;
                }
            }
        }
    }

    private int TypeIndex(string name) => Array.FindIndex(Program.Types, t => t.Name == name);

    private int[] Coordinates(TupleExpr tuple) => tuple.Items.Select(e => (int)Math.Floor(e.Eval(_ctx))).ToArray();

    private int ResolveStart(TupleExpr position)
    {
        int index = IndexOf(Coordinates(position));
        if (index < 0)
        {
            throw new LifeScriptRuntimeException("This Start position is outside the world.", position.Span);
        }
        return index;
    }

    /// <summary>Flat index of a position, wrapping where the world wraps; -1 if outside.</summary>
    public int IndexOf(int[] position)
    {
        int index = 0, stride = 1;
        for (int d = 0; d < Program.Size.Length; d++)
        {
            int v = position[d], size = Program.Size[d];
            if (v < 0 || v >= size)
            {
                if (!Program.Wrap[d])
                {
                    return -1;
                }
                v = ((v % size) + size) % size;
            }
            index += v * stride;
            stride *= size;
        }
        return index;
    }

    public int[] PositionOf(int index)
    {
        var position = new int[Program.Size.Length];
        for (int d = 0; d < position.Length; d++)
        {
            position[d] = index % Program.Size[d];
            index /= Program.Size[d];
        }
        return position;
    }

    public void Step()
    {
        var watch = Stopwatch.StartNew();
        _ctx.Generation = _automaton.Generation;
        if (Program.World != null)
        {
            RunWorld();
        }
        LastActiveCells = _automaton.ActiveCellCount;
        _automaton.Step();
        (_previous, _ids) = (_ids, _previous);
        _automaton.Grid.CopyStatesTo(_ids);
        if (_needsAge)
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                _ages[i] = _ids[i] == _previous[i] && _ages[i] < ushort.MaxValue ? (ushort)(_ages[i] + 1) : (ushort)0;
            }
        }
        LastStepMs = watch.Elapsed.TotalMilliseconds;
    }

    private void RunWorld()
    {
        WorldLog.Clear();
        _ctx.CellIndex = -1;
        _ctx.RandomCounter = 0;
        _ctx.World = new WorldAccess(Program, _ids);
        _ctx.Trace = WorldLog;
        try
        {
            _ctx.BeginFrame(Program.World!.LocalCount);
            Stmt.ExecBlock(Program.World.Body, _ctx);
            if (_ctx.World.Edits.Count > 0)
            {
                _automaton.InitializeGrid(_ctx.World.Edits.ToDictionary(e => PositionVector(e.Key), e => e.Value));
                foreach (var index in _ctx.World.Edits.Keys)
                {
                    _ages[index] = 0;
                }
            }
        }
        finally
        {
            _ctx.World = null;
            _ctx.Trace = null;
        }
    }

    private Vector<int> PositionVector(int index) => AutomataVector.Create(PositionOf(index));

    /// <summary>Sets cells to an id (painting). Takes effect immediately.</summary>
    public void Paint(IEnumerable<int> indices, byte id)
    {
        var edits = new Dictionary<Vector<int>, byte>();
        foreach (int index in indices)
        {
            if (index >= 0 && index < _ids.Length)
            {
                edits[PositionVector(index)] = id;
                _ids[index] = id;
                _ages[index] = 0;
            }
        }
        if (edits.Count > 0)
        {
            _automaton.InitializeGrid(edits);
        }
    }

    private void Rule(CellularAutomataCell<byte> cell, CellularAutomataNeighbors<byte> neighbors, CellularAutomata<byte> automaton)
    {
        byte id = cell.State;
        var type = Program.Types[Program.TypeOf[id]];
        var c = _ctx;
        byte missing = Program.DefaultId;
        foreach (int u in type.UsedUnion)
        {
            c.Neighbors[u] = neighbors.GetState(u, missing);
        }
        c.CellIndex = cell.Index;
        if (type.UsesAge)
        {
            c.Age = _ages[cell.Index];
        }
        if (type.UsesPosition)
        {
            var p = cell.Position;
            for (int d = 0; d < Program.Size.Length; d++)
            {
                c.Position[d] = p[d];
            }
        }
        byte next = CellEvaluator.Evaluate(c, type, id, UseLut);
        if (next != id)
        {
            cell.SetState(next);
        }
    }

    /// <summary>Runs one cell's rule with tracing on, without changing anything.</summary>
    public CellInspection Inspect(int index)
    {
        byte id = _ids[index];
        var type = Program.Types[Program.TypeOf[id]];
        var c = _ctx;
        var position = PositionOf(index);
        foreach (int u in type.UsedUnion)
        {
            var neighbor = (int[])position.Clone();
            for (int d = 0; d < neighbor.Length; d++)
            {
                neighbor[d] += Program.UnionOffsets[u][d];
            }
            int ni = IndexOf(neighbor);
            c.Neighbors[u] = ni < 0 ? Program.DefaultId : _ids[ni];
        }
        c.CellIndex = index;
        c.Age = _ages[index];
        c.Generation = _automaton.Generation;
        for (int d = 0; d < position.Length; d++)
        {
            c.Position[d] = position[d];
        }
        var prints = new List<string>();
        c.Trace = prints;
        string next;
        string? error = null;
        try
        {
            next = Program.DescribeId(CellEvaluator.Evaluate(c, type, id, useLut: false));
        }
        catch (LifeScriptRuntimeException e)
        {
            next = "?";
            error = e.Message;
        }
        finally
        {
            c.Trace = null;
        }
        var queries = type.Queries.Select((q, i) => (q.Describe(Program), c.Queries[i])).ToList();
        var locals = type.Locals.OrderBy(l => l.Value).Select(l => (l.Key, c.Stack[l.Value])).ToList();
        return new CellInspection(index, position, type.Name, Program.StateOf[id], _ages[index], queries, locals, prints, next, error);
    }

    /// <summary>Number of cells per id.</summary>
    public int[] Census()
    {
        var counts = new int[256];
        foreach (byte id in _ids)
        {
            counts[id]++;
        }
        return counts;
    }

    /// <summary>A hash of the grid, for comparing runs.</summary>
    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in _ids)
        {
            h = (h ^ b) * 1099511628211UL;
        }
        return h;
    }
}
