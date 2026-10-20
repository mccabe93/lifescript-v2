namespace LifeScript;

/// <summary>A <c>neighbors [that are "T"] [with state N]</c> query: which neighbors count toward the weighted sum.</summary>
public readonly record struct QuerySpec(int? Type, int? State)
{
    /// <summary>
    /// A type filter matches every cell of that type; a state filter matches that exact state.
    /// With neither, a neighbor matches when it is alive (state > 0).
    /// </summary>
    public bool Matches(CompiledProgram program, byte id) =>
        Type is int t
            ? program.TypeOf[id] == t && (State is not int st || program.StateOf[id] == st)
            : State is int s ? program.StateOf[id] == s : program.StateOf[id] > 0;

    public string Describe(CompiledProgram program) =>
        "neighbors"
        + (Type is int t ? $" that are \"{program.Types[t].Name}\"" : "")
        + (State is int s ? $" with state {s}" : "");
}

public sealed class CellType
{
    public required string Name { get; init; }
    public required int Index { get; init; }
    public required TypeDecl Decl { get; init; }
    public int BaseId { get; set; }
    public int StateCount { get; set; }

    /// <summary>Drawn neighborhood entries with a non-zero weight, as indices into the union neighborhood.</summary>
    public (int Union, double Weight)[] Weights { get; set; } = [];

    /// <summary>Union indices this type reads (weights plus <c>neighbor at</c>).</summary>
    public int[] UsedUnion { get; set; } = [];

    public List<QuerySpec> Queries { get; } = new();

    /// <summary>Why every cell must be evaluated every step (empty if the rule is a pure function of the neighborhood).</summary>
    public SortedSet<string> AllCellsReasons { get; } = new();

    /// <summary>Why the rule can't be compiled to a lookup table (empty if it can).</summary>
    public SortedSet<string> LutBlockers { get; } = new();

    public Lut? Lut { get; set; }

    public bool UsesAge { get; set; }
    public bool UsesPosition { get; set; }

    /// <summary>Local variable names to slots, for the inspector.</summary>
    public Dictionary<string, int> Locals { get; } = new();
}

public enum UpdateMode
{
    /// <summary>Only cells that changed, or whose neighbors changed, are evaluated.</summary>
    ActiveCells,

    /// <summary>Every cell is evaluated every step.</summary>
    AllCells,
}

/// <summary>A script after parsing and compilation. Immutable once built; simulations share it.</summary>
public sealed class CompiledProgram
{
    public string Title { get; internal set; } = "Untitled";
    public string Source { get; internal set; } = "";
    public int[] Size { get; internal set; } = [];
    public bool[] Wrap { get; internal set; } = [];
    public int CellCount { get; internal set; }
    public ulong Seed { get; internal set; } = 1;
    public long? Generations { get; internal set; }
    public double Interval { get; internal set; } = 0.1;
    public int? CellSize { get; internal set; }

    public CellType[] Types { get; internal set; } = [];
    public int DefaultType { get; internal set; }
    public byte DefaultId => (byte)Types[DefaultType].BaseId;

    /// <summary>Number of distinct (type, state) ids.</summary>
    public int IdCount { get; internal set; }
    public byte[] TypeOf { get; internal set; } = new byte[256];
    public byte[] StateOf { get; internal set; } = new byte[256];

    /// <summary>RGBA (R in the low byte) for each id.</summary>
    public uint[] Palette { get; internal set; } = new uint[256];

    /// <summary>Every offset any type reads; the library's neighborhood.</summary>
    public int[][] UnionOffsets { get; internal set; } = [];

    public int GlobalCount { get; internal set; }
    public string[] GlobalNames { get; internal set; } = [];
    public IReadOnlyList<AssignStmt> GlobalInits { get; internal set; } = [];
    public int GlobalInitLocals { get; internal set; }
    public WorldDecl? World { get; internal set; }
    public IReadOnlyList<StartItem> Start { get; internal set; } = [];

    public UpdateMode UpdateMode { get; internal set; }
    public List<string> UpdateModeReasons { get; } = new();
    public List<Diagnostic> Warnings { get; } = new();

    public int MaxQueries => Types.Length == 0 ? 0 : Types.Max(t => t.Queries.Count);

    public byte IdOf(int type, int state) => (byte)(Types[type].BaseId + Math.Clamp(state, 0, Types[type].StateCount - 1));

    public string DescribeId(byte id) =>
        Types.Length == 1 ? $"state {StateOf[id]}" : $"{Types[TypeOf[id]].Name} {StateOf[id]}";
}
