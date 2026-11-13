using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aoi.Playground.Services;

public sealed class DocEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("chapter")]
    public string Chapter { get; set; } = "";
}

public sealed class DocChapter
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("docs")]
    public List<DocEntry> Docs { get; set; } = [];
}

public sealed class DocIndex
{
    [JsonPropertyName("chapters")]
    public List<DocChapter> Chapters { get; set; } = [];

    [JsonPropertyName("docs")]
    public List<DocEntry> Docs { get; set; } = [];
}

public sealed class DocCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private DocIndex? _index;
    private Task<DocIndex>? _loading;

    public DocCatalog(HttpClient http) => _http = http;

    public async Task<DocIndex> GetIndexAsync()
    {
        if (_index != null) return _index;
        _loading ??= LoadAsync();
        return await _loading;
    }

    public async Task<string?> GetMarkdownAsync(string docId)
    {
        var entry = await FindAsync(docId);
        if (entry == null) return null;
        return await _http.GetStringAsync($"docs/{entry.Path}");
    }

    public async Task<DocEntry?> FindAsync(string docId)
    {
        var index = await GetIndexAsync();
        return index.Docs.FirstOrDefault(d => d.Id == docId)
               ?? index.Docs.FirstOrDefault(d => d.Id.Equals(docId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<DocIndex> LoadAsync()
    {
        var index = await _http.GetFromJsonAsync<DocIndex>("docs/index.json", JsonOptions)
                    ?? new DocIndex();
        _index = index;
        return index;
    }
}
