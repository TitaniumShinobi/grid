// Faithful, dependency-free port of CODE's renderMarkdownNodes block parser.
// This is a data projection, NOT a complete Markdown implementation.
namespace Grid.DocumentViewer;

public enum MarkdownBlockKind { Heading, Paragraph, CodeFence }
public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, int HeadingLevel = 0);

public static class MarkdownBlocks
{
    public static IReadOnlyList<MarkdownBlock> Parse(string content)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();
        var code = new List<string>();
        var fenced = false;

        void FlushParagraph()
        {
            var value = string.Join(" ", paragraph).Trim();
            if (value.Length > 0) blocks.Add(new(MarkdownBlockKind.Paragraph, value));
            paragraph.Clear();
        }
        void FlushCode()
        {
            blocks.Add(new(MarkdownBlockKind.CodeFence, string.Join("\n", code)));
            code.Clear();
            fenced = false;
        }

        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (fenced) FlushCode(); else fenced = true;
                continue;
            }
            if (fenced) { code.Add(line); continue; }
            var trimmed = line.Trim();
            var hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes is >= 1 and <= 3 && hashes < trimmed.Length && char.IsWhiteSpace(trimmed[hashes]))
            {
                FlushParagraph();
                blocks.Add(new(MarkdownBlockKind.Heading, trimmed[(hashes + 1)..].Trim(), hashes));
            }
            else if (trimmed.Length == 0) FlushParagraph();
            else paragraph.Add(trimmed);
        }
        FlushParagraph();
        if (fenced) FlushCode();
        if (blocks.Count == 0) blocks.Add(new(MarkdownBlockKind.Paragraph, "This file is empty."));
        return blocks;
    }
}
