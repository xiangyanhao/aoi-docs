namespace Aoi.Playground.Services;

/// <summary>
/// Survives SPA navigation between Playground and Docs so editor source is not wiped.
/// </summary>
public sealed class PlaygroundState
{
    public string Source { get; set; } = SampleCatalog.Default.Source;
    public string? ActiveSampleId { get; set; } = SampleCatalog.Default.Id;
    public string Output { get; set; } = "";
    public string? Error { get; set; }
    public long? LastElapsedMs { get; set; }
    public bool HashApplied { get; set; }
}
