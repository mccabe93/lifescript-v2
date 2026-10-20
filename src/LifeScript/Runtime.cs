namespace LifeScript;

/// <summary>
/// Mutable state for evaluating one cell (or the world block). One instance is reused for
/// every cell, so evaluation does not allocate.
/// </summary>
public sealed class EvalContext(CompiledProgram program)
{
    public CompiledProgram Program { get; } = program;

    // The cell being evaluated. Reads see the previous generation.
    public int CellIndex;
    public int Type;
    public int State;
    public int Age;
    public readonly int[] Position = new int[3];
    public long Generation;
    public byte[] Neighbors = new byte[Math.Max(1, program.UnionOffsets.Length)];
    public double[] Queries = new double[16];

    // The cell's next state. Last write wins.
    public int NextType;
    public int NextState;

    public double[] Globals = new double[program.GlobalCount];
    public double[] Stack = new double[256];
    public int FrameBase;
    public int FrameSize;
    public double ReturnValue;
    public int RandomCounter;

    /// <summary>Collects print output when not null (the cell inspector and the world log).</summary>
    public List<string>? Trace;

    /// <summary>Set while running the world block or Start; null for cells.</summary>
    public WorldAccess? World;

    public void SetNextState(int type, double state)
    {
        NextType = type;
        int max = Program.Types[type].StateCount - 1;
        NextState = state <= 0 || double.IsNaN(state) ? 0 : state >= max ? max : (int)state;
    }

    public void BeginFrame(int size)
    {
        FrameBase = 0;
        FrameSize = size;
        EnsureStack(size);
        Array.Clear(Stack, 0, size);
    }

    public double Call(FunctionDecl function, IReadOnlyList<Expr> args, SourceSpan span)
    {
        if (args.Count != function.Parameters.Count)
        {
            throw new LifeScriptRuntimeException(
                $"{function.Name} takes {function.Parameters.Count} argument(s) but got {args.Count}.",
                span
            );
        }
        int newBase = FrameBase + FrameSize;
        EnsureStack(newBase + function.LocalCount);
        // Arguments are evaluated in the caller's frame, then copied into the callee's.
        Span<double> values = stackalloc double[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            values[i] = args[i].Eval(this);
        }
        Array.Clear(Stack, newBase, function.LocalCount);
        values.CopyTo(Stack.AsSpan(newBase));
        (int savedBase, int savedSize) = (FrameBase, FrameSize);
        FrameBase = newBase;
        FrameSize = function.LocalCount;
        ReturnValue = 0;
        Stmt.ExecBlock(function.Body, this);
        (FrameBase, FrameSize) = (savedBase, savedSize);
        double result = ReturnValue;
        ReturnValue = 0;
        return result;
    }

    private void EnsureStack(int size)
    {
        if (size > 4096)
        {
            throw new LifeScriptRuntimeException("Function calls nested too deeply.", SourceSpan.None);
        }
        if (Stack.Length < size)
        {
            Array.Resize(ref Stack, Math.Max(size, Stack.Length * 2));
        }
    }

    public double NextRandom() => Rng.Unit(Program.Seed, Generation, CellIndex, RandomCounter++);
}

/// <summary>
/// Counter-based RNG: every draw is a hash of (seed, generation, cell, draw number), so results
/// do not depend on the order cells are evaluated in. That keeps runs reproducible and lets
/// the same script run in parallel or on a GPU later.
/// </summary>
public static class Rng
{
    public static ulong Hash(ulong seed, long generation, long cell, long counter)
    {
        ulong h = Mix(seed + 0x9E3779B97F4A7C15UL);
        h = Mix(h ^ (ulong)generation);
        h = Mix(h ^ (ulong)cell);
        return Mix(h ^ (ulong)counter);
    }

    public static double Unit(ulong seed, long generation, long cell, long counter) =>
        (Hash(seed, generation, cell, counter) >> 11) * (1.0 / (1UL << 53));

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

public static class Builtins
{
    private static readonly Dictionary<string, int> Arity = new()
    {
        ["random"] = 2,
        ["chance"] = 1,
        ["abs"] = 1,
        ["floor"] = 1,
        ["ceil"] = 1,
        ["round"] = 1,
        ["sqrt"] = 1,
        ["sin"] = 1,
        ["cos"] = 1,
        ["min"] = 2,
        ["max"] = 2,
        ["clamp"] = 3,
        ["bit"] = 2,
    };

    public static bool Exists(string name) => Arity.ContainsKey(name);

    public static int ArgumentCount(string name) => Arity[name];

    public static bool IsRandom(string name) => name is "random" or "chance";

    public static bool IsPure(string name) => Exists(name) && !IsRandom(name);

    public static double Call(string name, IReadOnlyList<Expr> args, EvalContext c, SourceSpan span)
    {
        double A(int i) => args[i].Eval(c);
        switch (name)
        {
            case "random":
            {
                // Whole number from a to b inclusive, like the original random(0, 100).
                double lo = Math.Floor(A(0)), hi = Math.Floor(A(1));
                if (hi < lo)
                {
                    (lo, hi) = (hi, lo);
                }
                return lo + Math.Floor(c.NextRandom() * (hi - lo + 1));
            }
            case "chance":
                return c.NextRandom() < A(0) ? 1 : 0;
            case "abs":
                return Math.Abs(A(0));
            case "floor":
                return Math.Floor(A(0));
            case "ceil":
                return Math.Ceiling(A(0));
            case "round":
                return Math.Round(A(0), MidpointRounding.AwayFromZero);
            case "sqrt":
                return Math.Sqrt(Math.Max(0, A(0)));
            case "sin":
                return Math.Sin(A(0));
            case "cos":
                return Math.Cos(A(0));
            case "min":
                return Math.Min(A(0), A(1));
            case "max":
                return Math.Max(A(0), A(1));
            case "clamp":
                return Math.Clamp(A(0), A(1), Math.Max(A(1), A(2)));
            case "bit":
            {
                // bit(30, i) is bit i of 30: handy for Wolfram rule numbers.
                double value = Math.Floor(A(0)), index = Math.Floor(A(1));
                return index < 0 || index > 52 ? 0 : Math.Floor(value / Math.Pow(2, index)) % 2;
            }
        }
        throw new LifeScriptRuntimeException($"Unknown function {name}.", span);
    }
}

/// <summary>Whole-grid access for the world block and Start. Edits are visible immediately.</summary>
public sealed class WorldAccess(CompiledProgram program, byte[] ids)
{
    public byte[] Ids { get; } = ids;
    public Dictionary<int, byte> Edits { get; } = new();

    public int Get(int index) => index < 0 ? program.DefaultId : Ids[index];

    public void Set(int index, int type, double state, EvalContext c)
    {
        if (index < 0)
        {
            return;
        }
        c.SetNextState(type, state);
        byte id = (byte)(program.Types[c.NextType].BaseId + c.NextState);
        Ids[index] = id;
        Edits[index] = id;
    }

    public double Count(int type, int? state)
    {
        int n = 0;
        foreach (byte id in Ids)
        {
            if (program.TypeOf[id] == type && (state is not int s || program.StateOf[id] == s))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>Flat index of a position, wrapping where the world wraps. -1 if outside.</summary>
    public int Resolve(TupleExpr position, EvalContext c)
    {
        var size = program.Size;
        if (position.Items.Count != size.Length)
        {
            throw new LifeScriptRuntimeException($"Expected {size.Length} coordinates.", position.Span);
        }
        int index = 0, stride = 1;
        for (int d = 0; d < size.Length; d++)
        {
            int v = (int)Math.Floor(position.Items[d].Eval(c));
            if (v < 0 || v >= size[d])
            {
                if (!program.Wrap[d])
                {
                    return -1;
                }
                v = ((v % size[d]) + size[d]) % size[d];
            }
            index += v * stride;
            stride *= size[d];
        }
        return index;
    }
}
