namespace WindowsTranscriber.App.Services;

public sealed class TranscriptDeduplicator
{
    private const int MaximumHistoryWords = 80;
    private const int MaximumOverlapWords = 24;
    private const int MaximumRecentCandidates = 12;

    private readonly List<string> _recentNormalizedWords = [];
    private readonly Queue<string> _recentCandidates = [];
    private readonly HashSet<string> _recentCandidateSet =
        new(StringComparer.OrdinalIgnoreCase);
    private string _previousCandidate = string.Empty;

    public string GetNovelText(string candidate)
    {
        candidate = CollapseRepeatedPhrase(candidate.Trim());
        var normalizedCandidate = NormalizePhrase(candidate);
        if (candidate.Length == 0 ||
            normalizedCandidate.Length == 0 ||
            string.Equals(candidate, _previousCandidate, StringComparison.OrdinalIgnoreCase) ||
            _recentCandidateSet.Contains(normalizedCandidate))
        {
            return string.Empty;
        }

        _previousCandidate = candidate;
        var originalWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var normalizedWords = originalWords.Select(Normalize).ToArray();

        var overlap = FindOverlap(normalizedWords);
        if (overlap == normalizedWords.Length)
        {
            RememberCandidate(normalizedCandidate);
            return string.Empty;
        }

        var novelWords = originalWords.Skip(overlap).ToArray();
        foreach (var word in normalizedWords.Skip(overlap))
        {
            if (word.Length > 0)
            {
                _recentNormalizedWords.Add(word);
            }
        }

        if (_recentNormalizedWords.Count > MaximumHistoryWords)
        {
            _recentNormalizedWords.RemoveRange(
                0,
                _recentNormalizedWords.Count - MaximumHistoryWords);
        }

        RememberCandidate(normalizedCandidate);
        return string.Join(' ', novelWords);
    }

    private int FindOverlap(IReadOnlyList<string> candidateWords)
    {
        var maximumOverlap = Math.Min(
            MaximumOverlapWords,
            Math.Min(_recentNormalizedWords.Count, candidateWords.Count));

        for (var overlap = maximumOverlap; overlap > 0; overlap--)
        {
            var historyStart = _recentNormalizedWords.Count - overlap;
            var matches = true;

            for (var index = 0; index < overlap; index++)
            {
                if (!string.Equals(
                    _recentNormalizedWords[historyStart + index],
                    candidateWords[index],
                    StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return overlap;
            }
        }

        return 0;
    }

    private static string Normalize(string word) =>
        new(word
            .Where(character => char.IsLetterOrDigit(character) || character == '\'')
            .Select(char.ToLowerInvariant)
            .ToArray());

    private void RememberCandidate(string candidate)
    {
        if (!_recentCandidateSet.Add(candidate))
        {
            return;
        }

        _recentCandidates.Enqueue(candidate);
        while (_recentCandidates.Count > MaximumRecentCandidates)
        {
            _recentCandidateSet.Remove(_recentCandidates.Dequeue());
        }
    }

    private static string NormalizePhrase(string text) => string.Join(
        ' ',
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(word => word.Length > 0));

    private static string CollapseRepeatedPhrase(string candidate)
    {
        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length % 2 != 0)
        {
            return candidate;
        }

        var half = words.Length / 2;
        for (var index = 0; index < half; index++)
        {
            if (!string.Equals(
                Normalize(words[index]),
                Normalize(words[index + half]),
                StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return string.Join(' ', words.Take(half));
    }
}
