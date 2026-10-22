namespace LifeScript;

public static class Compiler
{
    public const int MaxCells = 4_000_000;
    public const int MaxRadius = 8;

    /// <summary>Parses and compiles a script. Throws <see cref="LifeScriptException"/> with every problem found.</summary>
    public static CompiledProgram Compile(string source)
    {
        var ast = Parser.Parse(source);
        var program = new Binder(ast).Run();
        program.Source = source;
        return program;
    }
}

/// <summary>
/// Resolves names, enforces the locality rules (a cell reads only its neighborhood and writes
/// only itself), assigns state ids, and decides how each type can be executed.
/// </summary>
internal sealed class Binder(ScriptAst ast)
{
    private enum Context
    {
        Cell,
        World,
        Function,
        Init,
    }

    private sealed class Scope(Context kind, CellType? type = null, FunctionInfo? function = null)
    {
        public Context Kind { get; } = kind;
        public CellType? Type { get; } = type;
        public FunctionInfo? Function { get; } = function;
        public Dictionary<string, int> Locals { get; } = new();
    }

    private sealed class FunctionInfo(FunctionDecl decl)
    {
        public FunctionDecl Decl { get; } = decl;
        public HashSet<string> Callees { get; } = new();
        public bool UsesRandom { get; set; }
        public bool UsesGeneration { get; set; }
        public bool UsesGlobals { get; set; }
    }

    private readonly List<Diagnostic> _errors = new();
    private readonly CompiledProgram _p = new();
    private readonly Dictionary<string, int> _typeIndex = new();
    private readonly Dictionary<string, FunctionInfo> _functions = new();
    private readonly Dictionary<string, int> _globals = new();
    private readonly Dictionary<string, double> _constants = new();
    private readonly Dictionary<string, int> _unionIndex = new();
    private readonly List<int[]> _union = new();
    private int[] _maxState = [];
    private int?[] _explicitStates = [];

    private int Dims => _p.Size.Length;

    private void Error(string message, SourceSpan span) => _errors.Add(new Diagnostic(Severity.Error, message, span));

    private void Warn(string message, SourceSpan span) => _p.Warnings.Add(new Diagnostic(Severity.Warning, message, span));

    public CompiledProgram Run()
    {
        BindProperties();
        if (ast.Types.Count == 0)
        {
            Error("A script needs at least one type.", new SourceSpan(1, 1, 0));
            throw new LifeScriptException(_errors);
        }
        _p.Types = new CellType[ast.Types.Count];
        _maxState = new int[ast.Types.Count];
        _explicitStates = new int?[ast.Types.Count];
        for (int i = 0; i < ast.Types.Count; i++)
        {
            var decl = ast.Types[i];
            if (!_typeIndex.TryAdd(decl.Name, i))
            {
                Error($"Type \"{decl.Name}\" is declared twice.", decl.Span);
            }
            _p.Types[i] = new CellType { Name = decl.Name, Index = i, Decl = decl };
            _maxState[i] = 1;
            if (decl.States != null)
            {
                if (decl.States.Constant is double s && s == Math.Floor(s) && s >= 1 && s <= 256)
                {
                    _explicitStates[i] = (int)s;
                }
                else
                {
                    Error("States must be a constant whole number from 1 to 256.", decl.States.Span);
                }
            }
        }
        _p.DefaultType = ResolveDefaultType();
        if (_errors.Count > 0)
        {
            throw new LifeScriptException(_errors);
        }

        CollectGlobals();
        BindFunctions();
        BindGlobalInits();
        if (ast.World != null)
        {
            var scope = new Scope(Context.World);
            BindBlock(ast.World.Body, scope);
            ast.World.LocalCount = scope.Locals.Count;
            _p.World = ast.World;
        }
        BindStart();
        foreach (var type in _p.Types)
        {
            BindNeighborhood(type);
            var scope = new Scope(Context.Cell, type);
            BindBlock(type.Decl.Body, scope);
            type.Decl.LocalCount = scope.Locals.Count;
            foreach (var (name, slot) in scope.Locals)
            {
                type.Locals[name] = slot;
            }
            type.UsedUnion = type.Weights.Select(w => w.Union).Concat(type.UsedUnion).Distinct().Order().ToArray();
        }
        AssignIds();
        if (_errors.Count > 0)
        {
            throw new LifeScriptException(_errors);
        }
        _p.UnionOffsets = _union.ToArray();
        long links = (long)_p.CellCount * _union.Count;
        if (links > 64_000_000)
        {
            Error($"World size × neighborhood size is {links:N0}; the limit is 64,000,000. Use a smaller world or neighborhood.", SourceSpan.None);
            throw new LifeScriptException(_errors);
        }
        BuildPalette();
        Analyze();
        return _p;
    }

    #region Properties

    private void BindProperties()
    {
        var seen = new HashSet<string>();
        foreach (var prop in ast.Properties)
        {
            if (!seen.Add(prop.Name))
            {
                Error($"{prop.Name} is set twice.", prop.Span);
            }
            switch (prop.Name)
            {
                case "Title":
                    _p.Title = ((StringLiteralExpr)prop.Value!).Text;
                    break;
                case "Dimensions":
                {
                    var values = ConstantInts(prop.Value!, "Dimensions");
                    if (values == null)
                    {
                        break;
                    }
                    if (values.Length is < 1 or > 3)
                    {
                        Error("Dimensions needs 1 to 3 sizes, e.g. (128, 128) or (32, 32, 32).", prop.Span);
                        break;
                    }
                    if (values.Any(v => v < 1 || v > 4096))
                    {
                        Error("Each dimension must be from 1 to 4096.", prop.Span);
                        break;
                    }
                    long cells = values.Aggregate(1L, (a, b) => a * b);
                    if (cells > Compiler.MaxCells)
                    {
                        Error($"The world has {cells:N0} cells; the limit is {Compiler.MaxCells:N0}.", prop.Span);
                        break;
                    }
                    _p.Size = values;
                    break;
                }
                case "Wraparound":
                    if (prop.Value is TupleExpr wraps)
                    {
                        _p.Wrap = wraps.Items.Select(i => ConstantDouble(i, "Wraparound") != 0).ToArray();
                    }
                    else
                    {
                        _p.Wrap = [ConstantDouble(prop.Value!, "Wraparound") != 0];
                    }
                    break;
                case "Seed":
                    _p.Seed = (ulong)(long)ConstantDouble(prop.Value!, "Seed");
                    break;
                case "Generations":
                    _p.Generations = (long)ConstantDouble(prop.Value!, "Generations");
                    break;
                case "Interval":
                    _p.Interval = Math.Max(0, ConstantDouble(prop.Value!, "Interval"));
                    break;
                case "Cell Size":
                {
                    var values = ConstantInts(prop.Value!, "Cell Size");
                    _p.CellSize = values?.FirstOrDefault();
                    break;
                }
                case "Pausable":
                case "Steppable":
                    Warn($"{prop.Name} is ignored: play, pause and step are always available in the player.", prop.Span);
                    break;
            }
        }
        if (_p.Size.Length == 0)
        {
            _p.Size = [128, 128];
        }
        if (_p.Wrap.Length == 1)
        {
            _p.Wrap = Enumerable.Repeat(_p.Wrap[0], Dims).ToArray();
        }
        else if (_p.Wrap.Length == 0)
        {
            _p.Wrap = new bool[Dims];
        }
        else if (_p.Wrap.Length != Dims)
        {
            Error($"Wraparound has {_p.Wrap.Length} values but the world has {Dims} dimensions.", SourceSpan.None);
            _p.Wrap = new bool[Dims];
        }
        _p.CellCount = _p.Size.Aggregate(1, (a, b) => a * b);
    }

    private double ConstantDouble(Expr e, string what)
    {
        if (e.Constant is double v)
        {
            return v;
        }
        Error($"{what} must be a constant.", e.Span);
        return 0;
    }

    private int[]? ConstantInts(Expr e, string what)
    {
        var items = e is TupleExpr t ? t.Items : [e];
        var result = new int[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Constant is not double v || v != Math.Floor(v))
            {
                Error($"{what} must be constant whole numbers.", items[i].Span);
                return null;
            }
            result[i] = (int)v;
        }
        return result;
    }

    private int ResolveDefaultType()
    {
        var prop = ast.Properties.FirstOrDefault(p => p.Name == "Default Type");
        if (prop == null)
        {
            return 0;
        }
        var name = ((StringLiteralExpr)prop.Value!).Text;
        if (_typeIndex.TryGetValue(name, out int index))
        {
            return index;
        }
        Error($"Default Type \"{name}\" is not a declared type.", prop.Span);
        return 0;
    }

    #endregion

    #region Names

    private void CollectGlobals()
    {
        var worldAssigned = new HashSet<string>();
        if (ast.World != null)
        {
            CollectAssigned(ast.World.Body, worldAssigned);
        }
        foreach (var name in ast.Globals.Select(g => g.Name).Concat(worldAssigned))
        {
            _globals.TryAdd(name, _globals.Count);
        }
        // A global assigned exactly once, at the top level, to a constant is a named constant:
        // reading it doesn't make a rule depend on the world.
        foreach (var group in ast.Globals.GroupBy(g => g.Name))
        {
            if (group.Count() == 1 && !worldAssigned.Contains(group.Key) && group.First().Value.Constant is double value)
            {
                _constants[group.Key] = value;
            }
        }
        _p.GlobalCount = _globals.Count;
        _p.GlobalNames = _globals.OrderBy(g => g.Value).Select(g => g.Key).ToArray();
    }

    private static void CollectAssigned(IEnumerable<Stmt> body, HashSet<string> names)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case AssignStmt a:
                    names.Add(a.Name);
                    break;
                case IfStmt i:
                    foreach (var (_, b) in i.Branches)
                    {
                        CollectAssigned(b, names);
                    }
                    if (i.Else != null)
                    {
                        CollectAssigned(i.Else, names);
                    }
                    break;
                case ForStmt f:
                    names.Add(f.Init.Name);
                    CollectAssigned(f.Body, names);
                    break;
            }
        }
    }

    private int ResolveType(string name, SourceSpan span)
    {
        if (_typeIndex.TryGetValue(name, out int index))
        {
            return index;
        }
        Error($"Unknown type \"{name}\". Types: {string.Join(", ", _typeIndex.Keys.Select(k => $"\"{k}\""))}.", span);
        return 0;
    }

    #endregion

    #region Functions

    private void BindFunctions()
    {
        foreach (var f in ast.Functions)
        {
            if (Builtins.Exists(f.Name))
            {
                Error($"{f.Name} is a built-in function.", f.Span);
            }
            if (!_functions.TryAdd(f.Name, new FunctionInfo(f)))
            {
                Error($"Function {f.Name} is declared twice.", f.Span);
            }
        }
        foreach (var info in _functions.Values)
        {
            var scope = new Scope(Context.Function, function: info);
            foreach (var param in info.Decl.Parameters)
            {
                if (!scope.Locals.TryAdd(param, scope.Locals.Count))
                {
                    Error($"Parameter {param} is repeated.", info.Decl.Span);
                }
            }
            BindBlock(info.Decl.Body, scope);
            info.Decl.LocalCount = scope.Locals.Count;
        }

        // No recursion: every call must finish in bounded time.
        var state = new Dictionary<string, int>();
        bool Visit(string name, List<string> path)
        {
            if (state.TryGetValue(name, out int s))
            {
                if (s == 1)
                {
                    Error($"Recursion is not allowed: {string.Join(" -> ", path.SkipWhile(p => p != name).Append(name))}.", _functions[name].Decl.Span);
                    return false;
                }
                return true;
            }
            state[name] = 1;
            path.Add(name);
            foreach (var callee in _functions[name].Callees)
            {
                if (!Visit(callee, path))
                {
                    return false;
                }
            }
            path.RemoveAt(path.Count - 1);
            state[name] = 2;
            return true;
        }
        foreach (var name in _functions.Keys)
        {
            Visit(name, new List<string>());
        }

        // Propagate effects to callers.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var f in _functions.Values)
            {
                foreach (var callee in f.Callees.Select(c => _functions[c]))
                {
                    if (callee.UsesRandom && !f.UsesRandom || callee.UsesGeneration && !f.UsesGeneration || callee.UsesGlobals && !f.UsesGlobals)
                    {
                        f.UsesRandom |= callee.UsesRandom;
                        f.UsesGeneration |= callee.UsesGeneration;
                        f.UsesGlobals |= callee.UsesGlobals;
                        changed = true;
                    }
                }
            }
        }
    }

    #endregion

    #region Globals, world, start

    private void BindGlobalInits()
    {
        var scope = new Scope(Context.Init);
        foreach (var init in ast.Globals)
        {
            BindStmt(init, scope);
        }
        _p.GlobalInits = ast.Globals;
        _p.GlobalInitLocals = scope.Locals.Count;
    }

    private void BindStart()
    {
        var scope = new Scope(Context.Init);
        var items = ast.Properties.FirstOrDefault(p => p.Name == "Start")?.Start ?? [];
        foreach (var item in items)
        {
            switch (item)
            {
                case PlaceStart place:
                {
                    int t = ResolveType(place.TypeName, place.Span);
                    BindPosition(place.Position, scope);
                    NoteState(t, place.State, scope, "a Start placement");
                    break;
                }
                case RandomStart random:
                {
                    int t = ResolveType(random.TypeName, random.Span);
                    BindExpr(random.Probability, scope);
                    NoteState(t, random.State, scope, "random start");
                    if (random.From != null)
                    {
                        BindPosition(random.From, scope);
                        BindPosition(random.To!, scope);
                    }
                    break;
                }
                case PatternStart pattern:
                {
                    int t = ResolveType(pattern.TypeName, pattern.Span);
                    BindPosition(pattern.Position, scope);
                    try
                    {
                        var cells = Rle.Parse(pattern.Rle);
                        if (cells.Count > 0)
                        {
                            _maxState[t] = Math.Max(_maxState[t], cells.Max(c => c.State));
                        }
                    }
                    catch (FormatException e)
                    {
                        Error(e.Message, pattern.Span);
                    }
                    break;
                }
            }
        }
        _p.Start = items;
    }

    private void BindPosition(TupleExpr position, Scope scope)
    {
        if (position.Items.Count != Dims)
        {
            Error($"Expected {Dims} coordinates for a {Dims}D world but got {position.Items.Count}.", position.Span);
        }
        foreach (var item in position.Items)
        {
            BindExpr(item, scope);
        }
    }

    /// <summary>Records the largest constant state used for a type, or requires an explicit States declaration.</summary>
    private void NoteState(int type, Expr? state, Scope scope, string what)
    {
        if (state == null)
        {
            return;
        }
        BindExpr(state, scope);
        if (state.Constant is double v)
        {
            if (v < 0 || v > 255 || v != Math.Floor(v))
            {
                Error("States are whole numbers from 0 to 255.", state.Span);
                return;
            }
            _maxState[type] = Math.Max(_maxState[type], (int)v);
        }
        else if (UpperBound(state) is double bound && bound >= 0)
        {
            _maxState[type] = Math.Max(_maxState[type], (int)Math.Min(255, Math.Floor(bound)));
        }
        else if (_explicitStates[type] == null)
        {
            Error($"{what} sets a computed state, so type \"{_p.Types[type].Name}\" needs a States = N declaration.", state.Span);
        }
    }

    /// <summary>
    /// A cheap upper bound for expressions whose range is obvious (comparisons, bit(), alive),
    /// so <c>setstate to bit(rule, neighbors)</c> doesn't need a States declaration.
    /// </summary>
    private static double? UpperBound(Expr e) =>
        e switch
        {
            _ when e.Constant is double v => v,
            ParenExpr p => UpperBound(p.Inner),
            BinaryExpr { Op: "==" or "!=" or "<" or "<=" or ">" or ">=" or "and" or "or" } => 1,
            UnaryExpr { Op: "!" } => 1,
            InExpr => 1,
            BuiltinExpr { Value: BuiltinValue.Alive } => 1,
            NeighborAtExpr { TypeName: not null } => 1,
            CallExpr { Function: null, Name: "bit" or "chance" } => 1,
            CallExpr { Function: null, Name: "random" } c when UpperBound(c.Args[1]) is double hi && UpperBound(c.Args[0]) is double lo => Math.Max(lo, hi),
            _ => null,
        };

    #endregion

    #region Neighborhoods

    private int Union(int[] offset)
    {
        string key = string.Join(",", offset);
        if (!_unionIndex.TryGetValue(key, out int index))
        {
            index = _union.Count;
            _unionIndex[key] = index;
            _union.Add(offset);
        }
        return index;
    }

    private void BindNeighborhood(CellType type)
    {
        var offsets = new List<(int[] Offset, double Weight)>();
        switch (type.Decl.Neighborhood)
        {
            case null:
                offsets.AddRange(Box(1, manhattan: false).Select(o => (o, 1.0)));
                break;
            case BuiltinNeighborhood b:
            {
                int radius = 1;
                if (b.Radius != null)
                {
                    if (b.Radius.Constant is double r && r == Math.Floor(r) && r >= 1 && r <= Compiler.MaxRadius)
                    {
                        radius = (int)r;
                    }
                    else
                    {
                        Error($"The radius must be a constant from 1 to {Compiler.MaxRadius}.", b.Radius.Span);
                    }
                }
                switch (b.Name)
                {
                    case "moore":
                        offsets.AddRange(Box(radius, manhattan: false).Select(o => (o, 1.0)));
                        break;
                    case "vonneumann":
                        offsets.AddRange(Box(radius, manhattan: true).Select(o => (o, 1.0)));
                        break;
                    case "none":
                        break;
                    default:
                        Error($"Unknown neighborhood {b.Name}. Use moore(r), vonneumann(r), none, or draw one with Neighborhood[w] = {{ ... }}.", b.Span);
                        break;
                }
                break;
            }
            case MatrixNeighborhood m:
                BindMatrix(m, offsets);
                break;
        }
        type.Weights = offsets.Where(o => o.Weight != 0).Select(o => (Union(o.Offset), o.Weight)).ToArray();
    }

    private IEnumerable<int[]> Box(int radius, bool manhattan)
    {
        int count = (int)Math.Pow(2 * radius + 1, Dims);
        for (int i = 0; i < count; i++)
        {
            var offset = new int[Dims];
            int rest = i;
            for (int d = 0; d < Dims; d++)
            {
                offset[d] = rest % (2 * radius + 1) - radius;
                rest /= 2 * radius + 1;
            }
            int distance = manhattan ? offset.Sum(Math.Abs) : offset.Max(Math.Abs);
            if (distance > 0 && distance <= radius)
            {
                yield return offset;
            }
        }
    }

    private void BindMatrix(MatrixNeighborhood m, List<(int[], double)> offsets)
    {
        // The original lifescript wrote "no neighbors" as Neighborhood[0] = {VAR}.
        if (m.Slices.Count == 1 && m.Slices[0].Count == 1 && m.Slices[0][0] == null)
        {
            return;
        }
        bool threeD = m.Slices.Count > 1 || m.Height != null && m.Slices[0].Count != m.Width * m.Height;
        if (m.Width < 1)
        {
            Error("The neighborhood width must be at least 1.", m.Span);
            return;
        }
        if (threeD && Dims != 3)
        {
            Error("A neighborhood with z-slices needs a 3D world.", m.Span);
            return;
        }
        int height = m.Height ?? (m.Slices[0].Count + m.Width - 1) / m.Width;
        (int x, int y, int z)? center = null;
        var entries = new List<(int X, int Y, int Z, Expr Weight)>();
        for (int z = 0; z < m.Slices.Count; z++)
        {
            var slice = m.Slices[z];
            if (slice.Count != m.Width * height)
            {
                Error(
                    m.Height == null && m.Slices.Count == 1
                        ? $"The neighborhood has {slice.Count} entries, which isn't a multiple of its width {m.Width}."
                        : $"Slice {z + 1} has {slice.Count} entries; expected {m.Width} × {height} = {m.Width * height}.",
                    m.Span
                );
                return;
            }
            for (int i = 0; i < slice.Count; i++)
            {
                if (slice[i] == null)
                {
                    if (center != null)
                    {
                        Error("The neighborhood has more than one VAR.", m.Span);
                        return;
                    }
                    center = (i % m.Width, i / m.Width, z);
                }
                else
                {
                    entries.Add((i % m.Width, i / m.Width, z, slice[i]!));
                }
            }
        }
        if (center == null)
        {
            Error("The neighborhood needs a VAR marking the cell itself.", m.Span);
            return;
        }
        if (Dims == 1 && height > 1)
        {
            Error("A 1D world needs a single-row neighborhood.", m.Span);
            return;
        }
        foreach (var (x, y, z, weightExpr) in entries)
        {
            if (weightExpr.Constant is not double weight)
            {
                Error("Neighborhood weights must be constants.", weightExpr.Span);
                continue;
            }
            var offset = new int[Dims];
            offset[0] = x - center.Value.x;
            if (Dims > 1)
            {
                offset[1] = y - center.Value.y;
            }
            if (Dims > 2)
            {
                offset[2] = z - center.Value.z;
            }
            if (offset.Any(o => Math.Abs(o) > Compiler.MaxRadius))
            {
                Error($"Neighborhood offsets are limited to {Compiler.MaxRadius} cells from the center.", m.Span);
                return;
            }
            offsets.Add((offset, weight));
        }
    }

    #endregion

    #region Statements and expressions

    private void BindBlock(IEnumerable<Stmt> body, Scope scope)
    {
        foreach (var s in body)
        {
            BindStmt(s, scope);
        }
    }

    private void BindAssignTarget(AssignStmt a, Scope scope)
    {
        switch (scope.Kind)
        {
            case Context.World:
            case Context.Init:
                a.Kind = VarKind.Global;
                a.Slot = _globals[a.Name];
                break;
            default:
                if (scope.Kind == Context.Cell && _globals.ContainsKey(a.Name) && !scope.Locals.ContainsKey(a.Name))
                {
                    Error($"{a.Name} is a global (set at the top level or in world:). Cells can read it but not change it; use another name for a local.", a.Span);
                }
                if (!scope.Locals.TryGetValue(a.Name, out int slot))
                {
                    slot = scope.Locals.Count;
                    scope.Locals[a.Name] = slot;
                }
                a.Kind = VarKind.Local;
                a.Slot = slot;
                break;
        }
    }

    private void RequireCell(Scope scope, string what, SourceSpan span)
    {
        if (scope.Kind != Context.Cell)
        {
            Error($"{what} can only be used inside a type.", span);
        }
    }

    private void RequireWorld(Scope scope, string what, SourceSpan span)
    {
        if (scope.Kind != Context.World)
        {
            Error(
                scope.Kind == Context.Cell
                    ? $"{what} is not allowed in a type: a cell can only change itself and read its own neighborhood. Move it to the world: block."
                    : $"{what} can only be used in the world: block.",
                span
            );
        }
    }

    private void BindStmt(Stmt stmt, Scope scope)
    {
        switch (stmt)
        {
            case AssignStmt a:
                BindExpr(a.Value, scope);
                BindAssignTarget(a, scope);
                break;
            case IfStmt i:
                foreach (var (condition, body) in i.Branches)
                {
                    BindExpr(condition, scope);
                    BindBlock(body, scope);
                }
                if (i.Else != null)
                {
                    BindBlock(i.Else, scope);
                }
                break;
            case ForStmt f:
                BindStmt(f.Init, scope);
                BindExpr(f.Limit, scope);
                if (f.Step != null)
                {
                    BindExpr(f.Step, scope);
                }
                BindBlock(f.Body, scope);
                break;
            case SetStateStmt s:
                RequireCell(scope, "alive, dead and setstate", s.Span);
                if (scope.Type != null)
                {
                    NoteState(scope.Type.Index, s.Value, scope, "setstate");
                }
                break;
            case BecomeStmt b:
                RequireCell(scope, "become", b.Span);
                b.TypeIndex = ResolveType(b.TypeName, b.Span);
                NoteState(b.TypeIndex, b.State, scope, "become ... with state");
                break;
            case PrintStmt p:
                foreach (var part in p.Parts.OfType<Expr>())
                {
                    BindExpr(part, scope);
                }
                break;
            case CallStmt c:
                BindExpr(c.Call, scope);
                break;
            case ReturnStmt r:
                if (r.Value != null)
                {
                    BindExpr(r.Value, scope);
                }
                break;
            case CreateStmt c:
                RequireWorld(scope, "create", c.Span);
                c.TypeIndex = ResolveType(c.TypeName, c.Span);
                BindPosition(c.Position, scope);
                NoteState(c.TypeIndex, c.State, scope, "create ... with state");
                break;
            case KillStmt k:
                RequireWorld(scope, "kill", k.Span);
                BindPosition(k.Position, scope);
                break;
            case SetStateAtStmt s:
                RequireWorld(scope, "setstate of (x, y)", s.Span);
                BindPosition(s.Position, scope);
                BindExpr(s.Value, scope);
                break;
            default:
                throw new InvalidOperationException(stmt.GetType().Name);
        }
    }

    private void Impure(Scope scope, string reason, bool blocksActive = true)
    {
        if (scope.Type == null)
        {
            return;
        }
        if (blocksActive)
        {
            scope.Type.AllCellsReasons.Add(reason);
        }
        scope.Type.LutBlockers.Add(reason);
    }

    private void BindExpr(Expr expr, Scope scope)
    {
        switch (expr)
        {
            case NumberExpr:
            case StringLiteralExpr:
                break;
            case ParenExpr p:
                BindExpr(p.Inner, scope);
                break;
            case TupleExpr t:
                Error("A tuple can only be used as a position or color.", t.Span);
                break;
            case VarExpr v:
                if (scope.Locals.TryGetValue(v.Name, out int slot))
                {
                    v.Kind = VarKind.Local;
                    v.Slot = slot;
                }
                else if (_constants.TryGetValue(v.Name, out double constant))
                {
                    v.Kind = VarKind.Global;
                    v.Slot = _globals[v.Name];
                    v.Folded = constant;
                }
                else if (_globals.TryGetValue(v.Name, out int global))
                {
                    v.Kind = VarKind.Global;
                    v.Slot = global;
                    Impure(scope, $"reads global {v.Name}");
                    if (scope.Function != null)
                    {
                        scope.Function.UsesGlobals = true;
                    }
                }
                else
                {
                    Error($"Unknown variable {v.Name}.", v.Span);
                }
                break;
            case UnaryExpr u:
                BindExpr(u.Operand, scope);
                break;
            case BinaryExpr b:
                BindExpr(b.Left, scope);
                BindExpr(b.Right, scope);
                break;
            case InExpr i:
                BindExpr(i.Value, scope);
                foreach (var (low, high) in i.Items)
                {
                    BindExpr(low, scope);
                    if (high != null)
                    {
                        BindExpr(high, scope);
                    }
                }
                break;
            case BuiltinExpr b:
                BindBuiltin(b, scope);
                break;
            case NeighborsExpr n:
            {
                RequireCell(scope, "neighbors", n.Span);
                int? type = n.TypeName == null ? null : ResolveType(n.TypeName, n.Span);
                if (type is int t && n.StateFilter is int s)
                {
                    _maxState[t] = Math.Max(_maxState[t], s);
                }
                if (scope.Type != null)
                {
                    var query = new QuerySpec(type, n.StateFilter);
                    int index = scope.Type.Queries.IndexOf(query);
                    if (index < 0)
                    {
                        index = scope.Type.Queries.Count;
                        scope.Type.Queries.Add(query);
                    }
                    n.QueryIndex = index;
                }
                break;
            }
            case NeighborAtExpr n:
            {
                RequireCell(scope, "neighbor at", n.Span);
                if (n.Offset.Items.Count != Dims)
                {
                    Error($"Expected {Dims} offsets for a {Dims}D world.", n.Span);
                    break;
                }
                var offset = new int[Dims];
                for (int d = 0; d < Dims; d++)
                {
                    if (n.Offset.Items[d].Constant is not double v || v != Math.Floor(v) || Math.Abs(v) > Compiler.MaxRadius)
                    {
                        Error($"neighbor at needs constant offsets from -{Compiler.MaxRadius} to {Compiler.MaxRadius}.", n.Offset.Items[d].Span);
                        return;
                    }
                    offset[d] = (int)v;
                }
                if (offset.All(o => o == 0))
                {
                    Error("neighbor at (0, 0) is the cell itself; use state.", n.Span);
                    break;
                }
                n.UnionIndex = Union(offset);
                if (n.TypeName != null)
                {
                    n.TypeIndex = ResolveType(n.TypeName, n.Span);
                }
                if (scope.Type != null)
                {
                    scope.Type.UsedUnion = [.. scope.Type.UsedUnion, n.UnionIndex];
                    Impure(scope, "neighbor at", blocksActive: false);
                }
                break;
            }
            case CountExpr c:
                RequireWorld(scope, "count", c.Span);
                c.TypeIndex = ResolveType(c.TypeName, c.Span);
                break;
            case CellAtExpr c:
                RequireWorld(scope, c.TypeName == null ? "state of (x, y)" : "cell at (x, y)", c.Span);
                BindPosition(c.Position, scope);
                if (c.TypeName != null)
                {
                    c.TypeIndex = ResolveType(c.TypeName, c.Span);
                }
                break;
            case CallExpr c:
                foreach (var arg in c.Args)
                {
                    BindExpr(arg, scope);
                }
                if (_functions.TryGetValue(c.Name, out var function))
                {
                    c.Function = function.Decl;
                    if (c.Args.Count != function.Decl.Parameters.Count)
                    {
                        Error($"{c.Name} takes {function.Decl.Parameters.Count} argument(s) but got {c.Args.Count}.", c.Span);
                    }
                    scope.Function?.Callees.Add(c.Name);
                    if (function.UsesRandom)
                    {
                        Impure(scope, "random()");
                    }
                    if (function.UsesGeneration)
                    {
                        Impure(scope, "generation");
                    }
                    if (function.UsesGlobals)
                    {
                        Impure(scope, "reads globals");
                    }
                }
                else if (Builtins.Exists(c.Name))
                {
                    if (c.Args.Count != Builtins.ArgumentCount(c.Name))
                    {
                        Error($"{c.Name} takes {Builtins.ArgumentCount(c.Name)} argument(s) but got {c.Args.Count}.", c.Span);
                    }
                    if (Builtins.IsRandom(c.Name))
                    {
                        Impure(scope, "random()");
                        if (scope.Function != null)
                        {
                            scope.Function.UsesRandom = true;
                        }
                    }
                }
                else
                {
                    Error($"Unknown function {c.Name}.", c.Span);
                }
                break;
            default:
                throw new InvalidOperationException(expr.GetType().Name);
        }
    }

    private void BindBuiltin(BuiltinExpr b, Scope scope)
    {
        switch (b.Value)
        {
            case BuiltinValue.Alive:
            case BuiltinValue.State:
                RequireCell(scope, b.Value == BuiltinValue.Alive ? "alive" : "state", b.Span);
                break;
            case BuiltinValue.Age:
                RequireCell(scope, "age", b.Span);
                Impure(scope, "age");
                if (scope.Type != null)
                {
                    scope.Type.UsesAge = true;
                }
                break;
            case BuiltinValue.Generation:
                Impure(scope, "generation");
                if (scope.Function != null)
                {
                    scope.Function.UsesGeneration = true;
                }
                break;
            case BuiltinValue.CellX:
            case BuiltinValue.CellY:
            case BuiltinValue.CellZ:
                RequireCell(scope, "cell.x/y/z", b.Span);
                int axis = b.Value - BuiltinValue.CellX;
                if (axis >= Dims)
                {
                    Error($"cell.{"xyz"[axis]} doesn't exist in a {Dims}D world.", b.Span);
                }
                Impure(scope, "cell position");
                if (scope.Type != null)
                {
                    scope.Type.UsesPosition = true;
                }
                break;
        }
    }

    #endregion

    #region Ids, colors, analysis

    private void AssignIds()
    {
        int id = 0;
        foreach (var type in _p.Types)
        {
            int states = _explicitStates[type.Index] ?? _maxState[type.Index] + 1;
            if (_explicitStates[type.Index] is int declared && _maxState[type.Index] >= declared)
            {
                Error($"Type \"{type.Name}\" declares States = {declared} but uses state {_maxState[type.Index]}.", type.Decl.States!.Span);
            }
            foreach (var key in type.Decl.StateColors.Keys.Where(k => k >= states))
            {
                Error($"statecolor {key} is out of range: type \"{type.Name}\" has {states} states.", type.Decl.Span);
            }
            type.BaseId = id;
            type.StateCount = states;
            for (int s = 0; s < states; s++)
            {
                if (id + s < 256)
                {
                    _p.TypeOf[id + s] = (byte)type.Index;
                    _p.StateOf[id + s] = (byte)s;
                }
            }
            id += states;
        }
        if (id > 256)
        {
            Error($"The script uses {id} (type, state) combinations; the limit is 256.", SourceSpan.None);
        }
        _p.IdCount = Math.Min(id, 256);
    }

    // Colors for scripts that don't pick their own: a sunset over dusk, in the order types use them.
    private static readonly ColorSpec Background = new(21, 16, 27);
    private static readonly ColorSpec[] Swatches =
    [
        new(255, 138, 76),  // sunset orange
        new(245, 230, 211), // sand
        new(127, 211, 224), // sea
        new(217, 102, 143), // dusk pink
        new(255, 196, 107), // amber
        new(169, 139, 214), // lavender
        new(95, 158, 110),  // dune grass
    ];

    private void BuildPalette()
    {
        uint Pack(ColorSpec c) => c.R | (uint)c.G << 8 | (uint)c.B << 16 | 0xFF000000u;
        int swatch = 0;
        foreach (var type in _p.Types)
        {
            // Empty space's state 0 is the background. When other types exist the empty type is
            // only background, so it doesn't use up a swatch; a single type still gets one for its
            // live states.
            bool emptyOnly = type.Index == _p.DefaultType && _p.Types.Length > 1;
            var hue = emptyOnly ? Background : Swatches[swatch++ % Swatches.Length];
            for (int s = 0; s < type.StateCount; s++)
            {
                ColorSpec color;
                if (type.Decl.StateColors.TryGetValue(s, out var explicitColor))
                {
                    color = explicitColor;
                }
                else if (type.Decl.Color != null)
                {
                    color = type.Decl.Color;
                }
                else if (s == 0)
                {
                    color = type.Index == _p.DefaultType ? Background : Mix(Background, hue, 0.25);
                }
                else
                {
                    // State 1 is brightest; higher states (decay) fade toward the background.
                    double t = type.StateCount <= 2 ? 1 : 1 - 0.7 * (s - 1) / Math.Max(1, type.StateCount - 2);
                    color = Mix(Background, hue, t);
                }
                _p.Palette[type.BaseId + s] = Pack(color);
            }
        }
    }

    private static ColorSpec Mix(ColorSpec from, ColorSpec to, double t) =>
        new((byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));

    private void Analyze()
    {
        var ctx = new EvalContext(_p);
        foreach (var type in _p.Types)
        {
            if (type.LutBlockers.Count == 0)
            {
                type.Lut = Lut.TryBuild(_p, type, ctx, out string? reason);
                if (reason != null)
                {
                    type.LutBlockers.Add(reason);
                }
            }
        }

        if (_p.World != null)
        {
            _p.UpdateModeReasons.Add("the world: block can change any cell");
        }
        foreach (var type in _p.Types.Where(t => t.AllCellsReasons.Count > 0))
        {
            _p.UpdateModeReasons.Add($"\"{type.Name}\" uses {string.Join(", ", type.AllCellsReasons)}");
        }
        if (_p.UpdateModeReasons.Count == 0)
        {
            // A quiet region stays quiet only if the default cell, surrounded by default cells, stays put.
            Array.Fill(ctx.Neighbors, _p.DefaultId);
            var type = _p.Types[_p.DefaultType];
            try
            {
                if (CellEvaluator.Evaluate(ctx, type, _p.DefaultId, useLut: false) != _p.DefaultId)
                {
                    _p.UpdateModeReasons.Add("an empty region doesn't stay empty");
                }
            }
            catch (LifeScriptRuntimeException e)
            {
                _p.UpdateModeReasons.Add(e.Message);
            }
        }
        _p.UpdateMode = _p.UpdateModeReasons.Count > 0 ? UpdateMode.AllCells : UpdateMode.ActiveCells;
    }

    #endregion
}
