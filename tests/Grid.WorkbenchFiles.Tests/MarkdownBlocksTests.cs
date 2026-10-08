// Copy into GRID's test project after matching its namespace/test framework.
// Assertions from CODE's current renderer contract; intentionally framework-neutral.
namespace Grid.DocumentViewer.Tests;

public static class MarkdownBlocksTests
{
    public static void Run()
    {
        var blocks = MarkdownBlocks.Parse("# Terms\r\n\r\nRead `carefully`.\r\n\r\n```cs\r\nvar x = 1;\r\n```");
        if (blocks.Count != 3 || blocks[0].HeadingLevel != 1 || blocks[0].Text != "Terms") throw new System.Exception("Heading parse failed");
        if (blocks[1].Text != "Read `carefully`.") throw new System.Exception("Paragraph parse failed");
        if (blocks[2].Kind != MarkdownBlockKind.CodeFence || blocks[2].Text != "var x = 1;") throw new System.Exception("Fence parse failed");
        if (DocumentClassification.Classify("GRID_TERMS_OF_SERVICE.md") != DocumentKind.Markdown) throw new System.Exception("Markdown classification failed");
    }
}
