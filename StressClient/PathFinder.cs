using System.Drawing;

namespace StressClient;

/// <summary>
/// Bounded 8-direction A* over a map grid, the same shape as the client's PathFinder
/// (Client/MirObjects/PathFinder.cs): octile costs 10/14 and a Chebyshev heuristic. Unlike the client it does not
/// allocate a Node per map cell — only expanded cells are materialised, so a 1000x1000 map costs a few thousand
/// objects per search and the caller controls the expansion cap.
/// </summary>
internal static class PathFinder
{
    private const int StraightCost = 10;
    private const int DiagonalCost = 14;

    /// <summary>Returns the path including <paramref name="start"/>, or null when no path exists within the cap.
    /// <paramref name="blocked"/> is consulted for every candidate cell (walls, dynamic objects, temporary avoid cells).</summary>
    public static List<Point> FindPath(int width, int height, Func<int, int, bool> blocked, Point start, Point goal, int maxNodes)
    {
        if (width <= 0 || height <= 0) return null;
        if (start.X < 0 || start.Y < 0 || start.X >= width || start.Y >= height) return null;
        if (goal.X < 0 || goal.Y < 0 || goal.X >= width || goal.Y >= height) return null;
        if (start == goal) return new List<Point> { start };

        var nodes = new Dictionary<int, Node>(256);
        var open = new MinHeap(256);

        Node Get(int x, int y)
        {
            int key = y * width + x;
            if (nodes.TryGetValue(key, out var node)) return node;
            node = new Node { X = x, Y = y, G = int.MaxValue, HeapIndex = -1 };
            nodes[key] = node;
            return node;
        }

        Node startNode = Get(start.X, start.Y);
        startNode.G = 0;
        startNode.F = Heuristic(start.X, start.Y, goal);
        open.Push(startNode);

        int expanded = 0;
        while (open.Count > 0)
        {
            Node current = open.Pop();
            current.Closed = true;
            if (++expanded > maxNodes) return null;

            if (current.X == goal.X && current.Y == goal.Y)
                return Retrace(current);

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    int nx = current.X + dx;
                    int ny = current.Y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                    // The goal itself is never rejected: callers pick a free goal cell, and a stale object on it
                    // must not turn an otherwise valid path into a failure.
                    bool isGoal = nx == goal.X && ny == goal.Y;
                    if (!isGoal && blocked(nx, ny)) continue;

                    Node neighbour = Get(nx, ny);
                    if (neighbour.Closed) continue;

                    int candidate = current.G + (dx != 0 && dy != 0 ? DiagonalCost : StraightCost);
                    if (candidate >= neighbour.G) continue;

                    neighbour.G = candidate;
                    neighbour.F = candidate + Heuristic(nx, ny, goal);
                    neighbour.Parent = current;

                    if (neighbour.HeapIndex >= 0) open.Update(neighbour);
                    else open.Push(neighbour);
                }
            }
        }

        return null;
    }

    private static int Heuristic(int x, int y, Point goal)
    {
        int dx = Math.Abs(x - goal.X);
        int dy = Math.Abs(y - goal.Y);
        return dx > dy ? DiagonalCost * dy + StraightCost * (dx - dy) : DiagonalCost * dx + StraightCost * (dy - dx);
    }

    private static List<Point> Retrace(Node end)
    {
        var path = new List<Point>();
        for (var node = end; node != null; node = node.Parent) path.Add(new Point(node.X, node.Y));
        path.Reverse();
        return path;
    }

    private sealed class Node
    {
        public int X, Y, G, F;
        public Node Parent;
        public bool Closed;
        public int HeapIndex;
    }

    /// <summary>Binary min-heap keyed by F, then by G (closer to the goal wins ties), like the client's Heap&lt;Node&gt;.</summary>
    private sealed class MinHeap
    {
        private readonly List<Node> _items;

        public MinHeap(int capacity) => _items = new List<Node>(capacity);

        public int Count => _items.Count;

        public void Push(Node node)
        {
            node.HeapIndex = _items.Count;
            _items.Add(node);
            SiftUp(node);
        }

        public Node Pop()
        {
            Node first = _items[0];
            int last = _items.Count - 1;

            if (last == 0)
            {
                _items.Clear();
                first.HeapIndex = -1;
                return first;
            }

            _items[0] = _items[last];
            _items[0].HeapIndex = 0;
            _items.RemoveAt(last);
            first.HeapIndex = -1;
            SiftDown(_items[0]);
            return first;
        }

        public void Update(Node node) => SiftUp(node);

        private void SiftUp(Node node)
        {
            while (node.HeapIndex > 0)
            {
                int parentIndex = (node.HeapIndex - 1) / 2;
                Node parent = _items[parentIndex];
                if (!Less(node, parent)) return;

                Swap(node, parent);
            }
        }

        private void SiftDown(Node node)
        {
            while (true)
            {
                int left = node.HeapIndex * 2 + 1;
                int right = left + 1;
                int best = -1;

                if (left < _items.Count) best = left;
                if (right < _items.Count && Less(_items[right], _items[left])) best = right;
                if (best < 0 || !Less(_items[best], node)) return;

                Swap(node, _items[best]);
            }
        }

        private static bool Less(Node a, Node b) => a.F < b.F || (a.F == b.F && a.G > b.G);

        private void Swap(Node a, Node b)
        {
            int ai = a.HeapIndex, bi = b.HeapIndex;
            _items[ai] = b;
            _items[bi] = a;
            a.HeapIndex = bi;
            b.HeapIndex = ai;
        }
    }
}
