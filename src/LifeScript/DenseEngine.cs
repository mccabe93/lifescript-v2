namespace LifeScript;

/// <summary>
/// The built-in engine: a flat array, single-threaded. Away from the edges a neighbor is a fixed
/// distance away in the array; at the edges positions are resolved one dimension at a time. When
/// the rule allows it, only cells that changed and the cells that read them are evaluated.
/// </summary>
public sealed class DenseEngine : IEngine
{
    private CellRule _rule = null!;
    private byte[] _ids = [];
    private int[] _size = [];
    private bool[] _wrap = [];
    private int[][] _offsets = [];

    // Array distance to each neighbor slot, valid when the cell is at least _reach[d] from each edge.
    private int[] _flat = [];
    private int[] _reach = [];

    // Cells changed since the last step (active-cell mode only).
    private readonly List<int> _dirty = new();
    private readonly List<int> _candidates = new();
    private int[] _mark = [];
    private int _stamp;

    private readonly List<int> _changed = new();
    private readonly List<byte> _changedTo = new();

    public void Reset(CellRule rule, byte[] ids)
    {
        _rule = rule;
        _ids = ids;
        _size = rule.Size;
        _wrap = rule.Wrap;
        _offsets = rule.Offsets;
        _flat = new int[_offsets.Length];
        _reach = new int[_size.Length];
        for (int u = 0; u < _offsets.Length; u++)
        {
            int stride = 1;
            for (int d = 0; d < _size.Length; d++)
            {
                _flat[u] += _offsets[u][d] * stride;
                _reach[d] = Math.Max(_reach[d], Math.Abs(_offsets[u][d]));
                stride *= _size[d];
            }
        }
        _mark = new int[ids.Length];
        _stamp = 0;
        _dirty.Clear();
        if (!rule.EvaluateAllCells)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] != rule.DefaultId)
                {
                    _dirty.Add(i);
                }
            }
        }
    }

    public void Edited(IEnumerable<int> indices)
    {
        if (!_rule.EvaluateAllCells)
        {
            _dirty.AddRange(indices);
        }
    }

    public int Step()
    {
        _changed.Clear();
        _changedTo.Clear();
        int evaluated;
        Span<int> position = stackalloc int[_size.Length];
        if (_rule.EvaluateAllCells)
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                Evaluate(i, position);
            }
            evaluated = _ids.Length;
        }
        else
        {
            CollectCandidates(position);
            foreach (int i in _candidates)
            {
                Evaluate(i, position);
            }
            evaluated = _candidates.Count;
        }

        // Every cell read the previous generation; now write the next one.
        for (int k = 0; k < _changed.Count; k++)
        {
            _ids[_changed[k]] = _changedTo[k];
        }
        if (!_rule.EvaluateAllCells)
        {
            _dirty.AddRange(_changed);
        }
        return evaluated;
    }

    /// <summary>Changed cells, plus every cell that has a changed cell as a neighbor.</summary>
    private void CollectCandidates(Span<int> position)
    {
        _candidates.Clear();
        int stamp = ++_stamp;
        foreach (int c in _dirty)
        {
            if (_mark[c] != stamp)
            {
                _mark[c] = stamp;
                _candidates.Add(c);
            }
            bool interior = PositionOf(c, position);
            for (int u = 0; u < _offsets.Length; u++)
            {
                // Cell x reads c through slot u when x + offset = c.
                int x = interior ? c - _flat[u] : Resolve(position, _offsets[u], -1);
                if (x >= 0 && _mark[x] != stamp)
                {
                    _mark[x] = stamp;
                    _candidates.Add(x);
                }
            }
        }
        _dirty.Clear();
    }

    private void Evaluate(int i, Span<int> position)
    {
        byte id = _ids[i];
        var slots = _rule.SlotsUsedBy(id);
        var neighbors = _rule.Neighbors;
        if (slots.Length > 0)
        {
            if (PositionOf(i, position))
            {
                foreach (int u in slots)
                {
                    neighbors[u] = _ids[i + _flat[u]];
                }
            }
            else
            {
                foreach (int u in slots)
                {
                    int n = Resolve(position, _offsets[u], 1);
                    neighbors[u] = n < 0 ? _rule.DefaultId : _ids[n];
                }
            }
        }
        byte next = _rule.Next(i, id);
        if (next != id)
        {
            _changed.Add(i);
            _changedTo.Add(next);
        }
    }

    /// <summary>Fills <paramref name="position"/>; true if every neighbor is in range without wrapping.</summary>
    private bool PositionOf(int index, Span<int> position)
    {
        bool interior = true;
        for (int d = 0; d < _size.Length; d++)
        {
            int v = index % _size[d];
            index /= _size[d];
            position[d] = v;
            interior &= v >= _reach[d] && v < _size[d] - _reach[d];
        }
        return interior;
    }

    /// <summary>Index of position + sign * offset, wrapping where the world wraps; -1 if outside.</summary>
    private int Resolve(ReadOnlySpan<int> position, int[] offset, int sign)
    {
        int index = 0, stride = 1;
        for (int d = 0; d < _size.Length; d++)
        {
            int v = position[d] + sign * offset[d], size = _size[d];
            if (v < 0 || v >= size)
            {
                if (!_wrap[d])
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
}
