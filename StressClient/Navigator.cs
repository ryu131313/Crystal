using System.Drawing;

namespace StressClient;

/// <summary>
/// Path bookkeeping only: the list of cells to walk and the index of the next one. Movement packets, ack
/// reconciliation and door opening live in <see cref="VirtualPlayer"/> because they need the socket and the world.
/// </summary>
internal sealed class Navigator
{
    private readonly List<Point> _path = new();
    private int _index;

    public bool HasPath => _index < _path.Count;

    public int Remaining => _path.Count - _index;

    public Point Next => _path[_index];

    public Point Second => _index + 1 < _path.Count ? _path[_index + 1] : default;

    public Point Goal => _path.Count > 0 ? _path[^1] : default;

    /// <summary>Starts following a path that begins at or near <paramref name="start"/>; the start cell itself is skipped.</summary>
    public void SetPath(List<Point> path, Point start)
    {
        _path.Clear();
        _index = 0;
        if (path == null) return;

        _path.AddRange(path);
        while (_index < _path.Count && _path[_index] == start) _index++;
    }

    public void Advance(int cells)
    {
        _index += Math.Max(1, cells);
        if (_index >= _path.Count) Clear();
    }

    public void Clear()
    {
        _path.Clear();
        _index = 0;
    }

    public bool CellsAhead(int count)
    {
        if (_index + count > _path.Count) return false;
        return true;
    }

    public Point CellAt(int offset) => _path[_index + offset];
}
