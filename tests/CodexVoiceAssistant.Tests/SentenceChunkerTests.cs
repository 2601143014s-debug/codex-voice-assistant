using CodexVoiceAssistant.Domain;

namespace CodexVoiceAssistant.Tests;

public sealed class SentenceChunkerTests
{
    [Fact]
    public void EmitsOnlyCompleteSentences()
    {
        var chunker = new SentenceChunker();

        var first = chunker.Append("第一句。第二");
        var second = chunker.Append("句！剩余");

        Assert.Equal(new[] { "第一句。" }, first);
        Assert.Equal(new[] { "第二句！" }, second);
        Assert.Equal("剩余", chunker.Flush());
    }

    [Fact]
    public void FlushReturnsNullForWhitespace()
    {
        var chunker = new SentenceChunker();
        Assert.Null(chunker.Flush());
    }

    [Fact]
    public void EmitsLongTextWithoutPunctuation()
    {
        var chunker = new SentenceChunker();

        var chunks = chunker.Append(new string('语', 81));

        Assert.Single(chunks);
        Assert.Equal(80, chunks[0].Length);
        Assert.Equal("语", chunker.Flush());
    }
}
