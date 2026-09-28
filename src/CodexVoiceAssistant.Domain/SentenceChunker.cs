using System.Text;

namespace CodexVoiceAssistant.Domain;

public sealed class SentenceChunker
{
    private const int MaximumChunkLength = 80;

    private static readonly char[] Boundaries =
    {
        '。',
        '！',
        '？',
        '；',
        '!',
        '?',
        ';',
        '\n',
    };

    private readonly StringBuilder _pending = new();

    public IReadOnlyList<string> Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return Array.Empty<string>();
        }

        _pending.Append(delta);
        var sentences = new List<string>();
        while (TryTakeSentence(out var sentence))
        {
            sentences.Add(sentence);
        }
        return sentences;
    }

    public string? Flush()
    {
        var text = _pending.ToString().Trim();
        _pending.Clear();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private bool TryTakeSentence(out string sentence)
    {
        var text = _pending.ToString();
        var index = text.IndexOfAny(Boundaries);
        if (index < 0 && text.Length < MaximumChunkLength)
        {
            sentence = "";
            return false;
        }

        if (index < 0)
        {
            index = MaximumChunkLength - 1;
        }

        sentence = text[..(index + 1)].Trim();
        _pending.Remove(0, index + 1);
        return sentence.Length > 0;
    }
}
