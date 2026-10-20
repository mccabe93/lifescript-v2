namespace LifeScript;

/// <summary>
/// A type's rule precomputed as a table over (own state, value of each neighbors query).
/// Only built when the rule depends on nothing else, which also means it could run as a
/// GPU shader. Stepping becomes a few table reads per cell.
/// </summary>
public sealed class Lut
{
    public const int MaxEntries = 1 << 16;
    private const int MaxDistinctSums = 4096;

    private readonly QueryIndexer[] _indexers;
    private readonly byte[] _table;

    private Lut(QueryIndexer[] indexers, byte[] table)
    {
        _indexers = indexers;
        _table = table;
    }

    public int Entries => _table.Length;

    public byte Lookup(int state, double[] queries)
    {
        int index = state;
        for (int q = 0; q < _indexers.Length; q++)
        {
            index = index * _indexers[q].Count + _indexers[q].IndexOf(queries[q]);
        }
        return _table[index];
    }

    public static Lut? TryBuild(CompiledProgram program, CellType type, EvalContext ctx, out string? reason)
    {
        // A query sums the weights of the neighbors that match, so its value is a subset sum.
        var sums = new SortedSet<double> { 0 };
        foreach (var (_, weight) in type.Weights)
        {
            foreach (var s in sums.ToArray())
            {
                sums.Add(Math.Round(s + weight, 9));
            }
            if (sums.Count > MaxDistinctSums)
            {
                reason = "neighborhood weights produce too many distinct sums for a lookup table";
                return null;
            }
        }
        var values = sums.ToArray();
        long entries = type.StateCount;
        foreach (var _ in type.Queries)
        {
            entries *= values.Length;
            if (entries > MaxEntries)
            {
                reason = $"a lookup table would need more than {MaxEntries:N0} entries";
                return null;
            }
        }

        var indexers = type.Queries.Select(_ => new QueryIndexer(values)).ToArray();
        var table = new byte[entries];
        var digits = new int[type.Queries.Count];
        EnsureQueries(ctx, type.Queries.Count);
        try
        {
            for (int i = 0; i < entries; i++)
            {
                int rest = i;
                for (int q = type.Queries.Count - 1; q >= 0; q--)
                {
                    digits[q] = rest % values.Length;
                    rest /= values.Length;
                    ctx.Queries[q] = values[digits[q]];
                }
                table[i] = CellEvaluator.Interpret(ctx, type, rest);
            }
        }
        catch (LifeScriptRuntimeException e)
        {
            reason = $"evaluating the rule failed: {e.Message}";
            return null;
        }
        reason = null;
        return new Lut(indexers, table);
    }

    internal static void EnsureQueries(EvalContext ctx, int count)
    {
        if (ctx.Queries.Length < count)
        {
            ctx.Queries = new double[count];
        }
    }

    private sealed class QueryIndexer
    {
        private readonly int _min;
        private readonly int[]? _direct;
        private readonly Dictionary<double, int>? _map;

        public QueryIndexer(double[] values)
        {
            Count = values.Length;
            bool integral = values.All(v => v == Math.Floor(v));
            if (integral && values[^1] - values[0] < 65536)
            {
                _min = (int)values[0];
                _direct = new int[(int)values[^1] - _min + 1];
                Array.Fill(_direct, 0);
                for (int i = 0; i < values.Length; i++)
                {
                    _direct[(int)values[i] - _min] = i;
                }
            }
            else
            {
                _map = new Dictionary<double, int>();
                for (int i = 0; i < values.Length; i++)
                {
                    _map[values[i]] = i;
                }
            }
        }

        public int Count { get; }

        public int IndexOf(double value) =>
            _direct != null ? _direct[(int)value - _min] : _map![Math.Round(value, 9)];
    }
}

/// <summary>Evaluates one cell given its neighbors (already in <see cref="EvalContext.Neighbors"/>).</summary>
public static class CellEvaluator
{
    public static void ComputeQueries(EvalContext c, CellType type)
    {
        var program = c.Program;
        var weights = type.Weights;
        for (int q = 0; q < type.Queries.Count; q++)
        {
            var query = type.Queries[q];
            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                if (query.Matches(program, c.Neighbors[weights[i].Union]))
                {
                    sum += weights[i].Weight;
                }
            }
            c.Queries[q] = sum;
        }
    }

    /// <summary>Runs the type's body for a cell in <paramref name="state"/>; queries must already be set.</summary>
    public static byte Interpret(EvalContext c, CellType type, int state)
    {
        c.Type = type.Index;
        c.State = state;
        c.NextType = type.Index;
        c.NextState = state;
        c.RandomCounter = 0;
        c.BeginFrame(type.Decl.LocalCount);
        Stmt.ExecBlock(type.Decl.Body, c);
        return c.Program.IdOf(c.NextType, c.NextState);
    }

    public static byte Evaluate(EvalContext c, CellType type, byte id, bool useLut)
    {
        Lut.EnsureQueries(c, type.Queries.Count);
        ComputeQueries(c, type);
        int state = c.Program.StateOf[id];
        return useLut && type.Lut != null ? type.Lut.Lookup(state, c.Queries) : Interpret(c, type, state);
    }
}
