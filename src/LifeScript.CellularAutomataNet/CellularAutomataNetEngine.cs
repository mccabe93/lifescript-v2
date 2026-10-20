using CellularAutomata.NET;

namespace LifeScript.CellularAutomataNet;

/// <summary>Runs lifescript rules on CellularAutomata.NET instead of the built-in engine.</summary>
public sealed class CellularAutomataNetEngine : IEngine
{
    private CellRule _rule = null!;
    private byte[] _ids = [];
    private CellularAutomata<byte> _automaton = null!;

    public void Reset(CellRule rule, byte[] ids)
    {
        _rule = rule;
        _ids = ids;
        var config = new CellularAutomataConfiguration<byte>
        {
            Dimensions = rule.Size
                .Select((size, d) => new CellularAutomataDimension { Cells = size, WrapStart = rule.Wrap[d], WrapEnd = rule.Wrap[d] })
                .ToArray(),
            DefaultState = rule.DefaultId,
            UpdateMode = rule.EvaluateAllCells ? CellularAutomataUpdateMode.AllCells : CellularAutomataUpdateMode.ActiveCells,
        };
        var neighborhood = new CellularAutomataNeighborhood<byte>
        {
            NeighborLocations = rule.Offsets.Select(o => AutomataVector.Create(o)).ToArray(),
        };
        _automaton = new CellularAutomata<byte>(config, neighborhood, Rule);
        _automaton.InitializeGrid(ids);
    }

    public void Edited(IEnumerable<int> indices)
    {
        var edits = new Dictionary<System.Numerics.Vector<int>, byte>();
        foreach (int index in indices)
        {
            edits[AutomataVector.Create(PositionOf(index))] = _ids[index];
        }
        if (edits.Count > 0)
        {
            _automaton.InitializeGrid(edits);
        }
    }

    public int Step()
    {
        int active = _automaton.ActiveCellCount;
        _automaton.Step();
        _automaton.Grid.CopyStatesTo(_ids);
        return active;
    }

    private void Rule(CellularAutomataCell<byte> cell, CellularAutomataNeighbors<byte> neighbors, CellularAutomata<byte> automaton)
    {
        byte id = cell.State;
        var buffer = _rule.Neighbors;
        foreach (int u in _rule.SlotsUsedBy(id))
        {
            buffer[u] = neighbors.GetState(u, _rule.DefaultId);
        }
        byte next = _rule.Next(cell.Index, id);
        if (next != id)
        {
            cell.SetState(next);
        }
    }

    private int[] PositionOf(int index)
    {
        var position = new int[_rule.Size.Length];
        for (int d = 0; d < position.Length; d++)
        {
            position[d] = index % _rule.Size[d];
            index /= _rule.Size[d];
        }
        return position;
    }
}
