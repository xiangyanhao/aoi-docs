using System.Text.RegularExpressions;
using Markdig;

namespace Aoi.Playground.Services;

public abstract record DocBlock;

public sealed record DocHtmlBlock(string Html) : DocBlock;

public sealed record DocAoiBlock(string Code) : DocBlock;

public sealed record DocCodeBlock(string Language, string Code) : DocBlock;

public static partial class DocMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    // ```lang\n...\n```  (info string optional)
    [GeneratedRegex(@"^```([^\n`]*)\n([\s\S]*?)^```\s*$", RegexOptions.Multiline)]
    private static partial Regex FenceRegex();

    public static IReadOnlyList<DocBlock> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [new DocHtmlBlock("")];

        var blocks = new List<DocBlock>();
        var text = markdown.Replace("\r\n", "\n");
        var matches = FenceRegex().Matches(text);
        int cursor = 0;

        foreach (Match m in matches)
        {
            if (m.Index > cursor)
            {
                var prose = text[cursor..m.Index];
                AppendProse(blocks, prose);
            }

            var info = m.Groups[1].Value.Trim();
            var lang = info.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";
            var code = m.Groups[2].Value.TrimEnd('\n');

            if (lang.Equals("aoi", StringComparison.OrdinalIgnoreCase))
                blocks.Add(new DocAoiBlock(code));
            else
                blocks.Add(new DocCodeBlock(lang, code));

            cursor = m.Index + m.Length;
        }

        if (cursor < text.Length)
            AppendProse(blocks, text[cursor..]);

        return blocks;
    }

    private static void AppendProse(List<DocBlock> blocks, string prose)
    {
        if (string.IsNullOrWhiteSpace(prose)) return;
        var html = Markdown.ToHtml(prose, Pipeline);
        if (!string.IsNullOrWhiteSpace(html))
            blocks.Add(new DocHtmlBlock(html));
    }
}
