namespace WindowsTranscriber.App.Services;

public sealed record TranscriptSearchResult(
    int Index,
    int Length,
    int MatchNumber,
    int MatchCount,
    bool Wrapped);

public sealed class TranscriptSearchService
{
    public int CountMatches(string text, string query) =>
        FindMatchIndexes(text, query).Count;

    public TranscriptSearchResult? FindNext(
        string text,
        string query,
        int selectionStart,
        int selectionLength)
    {
        var matches = FindMatchIndexes(text, query);
        if (matches.Count == 0)
        {
            return null;
        }

        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionLength = Math.Clamp(selectionLength, 0, text.Length - selectionStart);

        var selectedText = text.Substring(selectionStart, selectionLength);
        var searchStart = selectionLength > 0 &&
            string.Equals(selectedText, query, StringComparison.CurrentCultureIgnoreCase)
                ? selectionStart + selectionLength
                : selectionStart;

        var matchNumber = matches.FindIndex(index => index >= searchStart);
        var wrapped = matchNumber < 0;
        if (wrapped)
        {
            matchNumber = 0;
        }

        return new TranscriptSearchResult(
            matches[matchNumber],
            query.Length,
            matchNumber + 1,
            matches.Count,
            wrapped);
    }

    private static List<int> FindMatchIndexes(string text, string query)
    {
        var matches = new List<int>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
        {
            return matches;
        }

        var searchStart = 0;
        while (searchStart <= text.Length - query.Length)
        {
            var matchIndex = text.IndexOf(
                query,
                searchStart,
                StringComparison.CurrentCultureIgnoreCase);
            if (matchIndex < 0)
            {
                break;
            }

            matches.Add(matchIndex);
            searchStart = matchIndex + query.Length;
        }

        return matches;
    }
}
