using Server.MirEnvir;

namespace Server.Persistence.Tests;

/// <summary>Roadmap P6: the loop profiler attributes time to phases and publishes a coherent snapshot.</summary>
[Collection(PersistenceCollection.Name)] // LoopProfiler is static: keep it away from anything else using it
public class LoopProfilerTests
{
    private static void Spin(double ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ms) { }
    }

    [Fact]
    public void Phases_are_attributed_and_published()
    {
        LoopProfiler.Publish(); // reset the window
        for (var i = 0; i < 5; i++)
        {
            LoopProfiler.BeginIteration();
            Spin(2); LoopProfiler.Mark(LoopProfiler.Phase.Connections);
            Spin(4); LoopProfiler.Mark(LoopProfiler.Phase.Objects);
            LoopProfiler.CountPacketIn();
            LoopProfiler.CountPacketsOut(2, 100);
            LoopProfiler.EndIteration();
            Spin(1); // parked gap, attributed to Park at the next BeginIteration
        }
        LoopProfiler.BeginIteration();
        LoopProfiler.EndIteration();
        LoopProfiler.Publish();

        var s = LoopProfiler.Last;
        Assert.NotNull(s);
        Assert.Equal(6, s.Iterations);
        Assert.InRange(s.PhaseMs["Connections"], 9, 30);
        Assert.InRange(s.PhaseMs["Objects"], 19, 60);
        Assert.InRange(s.PhaseMs["Park"], 4, 30);
        Assert.True(s.Attributed > 0.9, $"attributed {s.Attributed}");
        Assert.Equal(5, s.PacketsIn);
        Assert.Equal(10, s.PacketsOut);
        Assert.Equal(500, s.BytesOut);
        Assert.Equal(6, s.Histogram.Values.Sum());
        Assert.Contains("[Diag] Loop it=6", LoopProfiler.Format(s));
    }
}
