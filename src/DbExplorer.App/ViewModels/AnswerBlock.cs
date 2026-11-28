namespace DbExplorer.App.ViewModels;

/// <summary>A piece of a Claude answer: prose, or the body of a fenced code block (drawn monospaced, without the fences).</summary>
public sealed record AnswerBlock(string Text, bool IsCode)
{
    /// <summary>
    /// Splits markdown text on its ``` fences. An unclosed fence (an answer still streaming) runs to the end as code. Blank
    /// prose between blocks is dropped; the language after the opening fence is not shown.
    /// </summary>
    public static IReadOnlyList<AnswerBlock> Split(string text)
    {
        var blocks = new List<AnswerBlock>();
        var current = new System.Text.StringBuilder();
        var inCode = false;

        void Flush()
        {
            var body = current.ToString().TrimEnd('\n');
            if (!inCode) body = body.Trim('\n');
            if (inCode || body.Trim().Length > 0) blocks.Add(new AnswerBlock(body, inCode));
            current.Clear();
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                inCode = !inCode;
                continue;
            }
            current.Append(line).Append('\n');
        }
        Flush();
        return blocks;
    }
}
