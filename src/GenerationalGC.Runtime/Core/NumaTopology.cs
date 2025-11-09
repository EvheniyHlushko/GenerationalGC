namespace GenerationalGC.Runtime.Core;

public sealed class NumaTopology
{
    public int NodeCount { get; }
    // cpuId -> nodeId (length may be <= number of logical CPUs; fallback maps all to 0)
    public int[] CpuToNode { get; }

    private NumaTopology(int nodeCount, int[] cpuToNode)
    {
        NodeCount = nodeCount;
        CpuToNode = cpuToNode;
    }

    public static NumaTopology SingleNode() =>
        new NumaTopology(nodeCount: 1, cpuToNode: []);

    // Optional: allow env override like GC_TOY_NUMA_NODES=2
    public static NumaTopology Simulate(int nodes)
    {
        nodes = Math.Max(1, nodes);
        var cpus = Environment.ProcessorCount;
        var map = new int[cpus];
        for (var i = 0; i < cpus; i++) map[i] = i % nodes;
        return new NumaTopology(nodes, map);
    }

    public int GetNodeForCpu(int cpuId)
    {
        if (CpuToNode.Length == 0) return 0;
        cpuId = Math.Abs(cpuId) % CpuToNode.Length;
        return CpuToNode[cpuId];
    }

    public static int TryGetCurrentProcessorId()
    {
        try { return System.Threading.Thread.GetCurrentProcessorId(); } // .NET 6+
        catch { return -1; }
    }
}