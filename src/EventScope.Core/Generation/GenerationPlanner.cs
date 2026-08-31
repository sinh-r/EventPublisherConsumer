using System.Collections.Frozen;

namespace EventScope.Core.Generation;

/// <summary>
/// Build plan §3.5 pass 1: lexes every leaf's generator template, builds the {{ref}}
/// dependency graph in CSR form, and computes a fill order plus cycle/unresolved-ref
/// diagnostics. Both graph algorithms are iterative — the acceptance criterion is a
/// 100,000-node chain completing without <see cref="StackOverflowException"/>, which no
/// recursive implementation could guarantee and no <c>catch</c> can recover from.
/// </summary>
public static class GenerationPlanner
{
    public static GenerationPlan Build(IReadOnlyList<LeafTemplate> leaves)
    {
        var n = leaves.Count;
        var indexByPath = leaves
            .Select((leaf, index) => (leaf.Path, index))
            .ToFrozenDictionary(t => t.Path, t => t.index);

        var segments = new List<IReadOnlyList<TemplateSegment>>(n);

        // As written: leaf `From`'s template contains {{ref: leaf `To`'s path}}. This is the
        // natural direction for human-readable cycle reporting ("$.a -> $.b" means a refs b).
        var refEdges = new List<(int From, int To, TextSpan Span)>();
        var unresolved = new List<UnresolvedRef>();

        for (var i = 0; i < n; i++)
        {
            var lexed = GeneratorLexer.Lex(leaves[i].Template);
            segments.Add(lexed);

            foreach (var segment in lexed)
            {
                if (segment.Kind != SegmentKind.Ref) continue;

                var targetPath = segment.Text ?? string.Empty;
                if (indexByPath.TryGetValue(targetPath, out var targetIndex))
                {
                    refEdges.Add((i, targetIndex, segment.Span));
                }
                else
                {
                    unresolved.Add(new UnresolvedRef(leaves[i].Path, targetPath, segment.Span));
                }
            }
        }

        // Dependency -> dependent CSR graph for Kahn (build plan §3.5): a ref's *target* is
        // the dependency (must be filled first), the leaf containing the {{ref}} is the
        // dependent - i.e. this is refEdges reversed. Self-edges are not skipped: `$.a`
        // referencing `$.a` is a valid 1-cycle.
        var outDegree = new int[n];
        foreach (var (_, to, _) in refEdges) outDegree[to]++;

        var edgeStart = new int[n + 1];
        for (var i = 0; i < n; i++) edgeStart[i + 1] = edgeStart[i] + outDegree[i];

        var cursor = (int[])edgeStart.Clone();
        var edgeTarget = new int[refEdges.Count];
        var inDegree = new int[n];
        foreach (var (from, to, _) in refEdges)
        {
            edgeTarget[cursor[to]++] = from; // dependency `to`'s out-edge lands on dependent `from`
            inDegree[from]++;
        }

        var (fillOrder, residual) = KahnTopoSort(n, edgeStart, edgeTarget, inDegree);
        var cycles = FindCycles(leaves, residual, edgeStart, edgeTarget, refEdges);

        return new GenerationPlan(leaves, segments, indexByPath, fillOrder, new PlanDiagnostics(cycles, unresolved));
    }

    /// <summary>Iterative Kahn. Returns every node index exactly once (a-cyclic leaves in
    /// dependency-respecting order, followed by every leaf that never reached in-degree 0 —
    /// directly cyclic or transitively depending on a cycle — in index order) plus which
    /// indices fall into that second group.</summary>
    private static (int[] FillOrder, bool[] Residual) KahnTopoSort(
        int n, int[] edgeStart, int[] edgeTarget, int[] inDegree)
    {
        var fillOrder = new int[n];
        var filled = 0;
        var remainingInDegree = (int[])inDegree.Clone();
        var queue = new Queue<int>();

        for (var i = 0; i < n; i++)
        {
            if (remainingInDegree[i] == 0) queue.Enqueue(i);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            fillOrder[filled++] = node;

            for (var e = edgeStart[node]; e < edgeStart[node + 1]; e++)
            {
                var dependent = edgeTarget[e];
                if (--remainingInDegree[dependent] == 0) queue.Enqueue(dependent);
            }
        }

        var residual = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (remainingInDegree[i] > 0)
            {
                residual[i] = true;
                fillOrder[filled++] = i;
            }
        }

        return (fillOrder, residual);
    }

    /// <summary>Iterative Tarjan SCC (explicit per-node edge cursor instead of a call stack)
    /// over just the residual (non-acyclic) nodes Kahn left behind, then names each SCC of
    /// size &gt; 1 and each self-loop as a reported <see cref="RefCycle"/>.</summary>
    private static IReadOnlyList<RefCycle> FindCycles(
        IReadOnlyList<LeafTemplate> leaves, bool[] residual, int[] edgeStart, int[] edgeTarget,
        List<(int From, int To, TextSpan Span)> refEdges)
    {
        var n = residual.Length;
        var index = new int[n];
        var lowLink = new int[n];
        var onStack = new bool[n];
        var visited = new bool[n];
        var edgeCursor = (int[])edgeStart.Clone();
        var componentStack = new Stack<int>();
        var callStack = new Stack<int>();
        var nextIndex = 0;
        var sccs = new List<List<int>>();

        for (var start = 0; start < n; start++)
        {
            if (!residual[start] || visited[start]) continue;

            callStack.Push(start);
            visited[start] = true;
            index[start] = lowLink[start] = nextIndex++;
            componentStack.Push(start);
            onStack[start] = true;

            while (callStack.Count > 0)
            {
                var node = callStack.Peek();

                if (edgeCursor[node] < edgeStart[node + 1])
                {
                    var target = edgeTarget[edgeCursor[node]];
                    edgeCursor[node]++;

                    if (!residual[target]) continue; // dependency graph reversal guarantees this never fires; defensive only

                    if (!visited[target])
                    {
                        visited[target] = true;
                        index[target] = lowLink[target] = nextIndex++;
                        componentStack.Push(target);
                        onStack[target] = true;
                        callStack.Push(target);
                    }
                    else if (onStack[target])
                    {
                        lowLink[node] = Math.Min(lowLink[node], index[target]);
                    }
                }
                else
                {
                    callStack.Pop();

                    if (lowLink[node] == index[node])
                    {
                        var component = new List<int>();
                        int member;
                        do
                        {
                            member = componentStack.Pop();
                            onStack[member] = false;
                            component.Add(member);
                        } while (member != node);
                        sccs.Add(component);
                    }

                    if (callStack.Count > 0)
                    {
                        var parent = callStack.Peek();
                        lowLink[parent] = Math.Min(lowLink[parent], lowLink[node]);
                    }
                }
            }
        }

        var cycles = new List<RefCycle>();
        foreach (var component in sccs)
        {
            if (component.Count > 1)
            {
                cycles.Add(BuildCycleWalk(leaves, component, refEdges));
                continue;
            }

            var only = component[0];
            var hasSelfEdge = refEdges.Any(e => e.From == only && e.To == only);
            if (hasSelfEdge)
            {
                cycles.Add(BuildCycleWalk(leaves, component, refEdges));
            }
        }

        return cycles;
    }

    /// <summary>Walks {{ref}} edges (in the direction they were written) within one SCC's
    /// membership until a node repeats, then reports the closed loop from that repeat onward
    /// — a plain functional-graph cycle-find, robust regardless of which member happened to
    /// be picked as the starting point.</summary>
    private static RefCycle BuildCycleWalk(
        IReadOnlyList<LeafTemplate> leaves, List<int> members,
        List<(int From, int To, TextSpan Span)> refEdges)
    {
        var memberSet = new HashSet<int>(members);
        var outgoing = new Dictionary<int, (int To, TextSpan Span)>();
        foreach (var (from, to, span) in refEdges)
        {
            if (memberSet.Contains(from) && memberSet.Contains(to) && !outgoing.ContainsKey(from))
            {
                outgoing[from] = (to, span);
            }
        }

        var order = new List<int>();
        var firstSeenAt = new Dictionary<int, int>();
        var current = members[0];

        while (!firstSeenAt.ContainsKey(current))
        {
            firstSeenAt[current] = order.Count;
            order.Add(current);
            current = outgoing[current].To;
        }

        var hops = new List<CycleHop>();
        for (var i = firstSeenAt[current]; i < order.Count; i++)
        {
            var from = order[i];
            var (to, span) = outgoing[from];
            hops.Add(new CycleHop(leaves[from].Path, leaves[to].Path, span));
        }

        return new RefCycle(hops);
    }
}
