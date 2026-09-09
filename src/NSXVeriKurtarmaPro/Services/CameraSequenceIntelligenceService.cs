using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public static class CameraSequenceIntelligenceService
{
    private sealed record SequenceToken(string Prefix, long Number, int Width);

    public static IReadOnlyList<RecoveryFileItem> Enrich(IReadOnlyList<RecoveryFileItem> items, bool cameraOptimized)
    {
        if (!cameraOptimized || items.Count == 0)
            return items;

        var parsed = new List<(RecoveryFileItem Item, SequenceToken Token)>();
        foreach (RecoveryFileItem item in items)
        {
            if (!FileTypeHelper.IsPhoto(item.Extension) && !FileTypeHelper.IsVideo(item.Extension))
                continue;
            if (TryParseSequence(item.FileName, out SequenceToken token))
                parsed.Add((item, token));
        }

        foreach (IGrouping<string, (RecoveryFileItem Item, SequenceToken Token)> group in parsed.GroupBy(
                     pair => $"{pair.Token.Prefix}|{FileTypeHelper.GetCategory(pair.Item.Extension)}",
                     StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(pair => pair.Token.Number).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                (RecoveryFileItem item, SequenceToken token) = ordered[i];
                bool previous = i > 0 && IsNeighbour(ordered[i - 1], ordered[i]);
                bool next = i + 1 < ordered.Length && IsNeighbour(ordered[i], ordered[i + 1]);
                if (!previous && !next)
                    continue;

                string sequenceText = token.Number.ToString(new string('0', Math.Clamp(token.Width, 3, 8)));
                if (!item.SourceText.Contains("Kamera sıra", StringComparison.OrdinalIgnoreCase))
                    item.SourceText = $"{item.SourceText} • Kamera sıra {sequenceText}";
            }
        }

        return items;
    }

    internal static bool TryParseSequence(string fileName, out string prefix, out long number, out int width)
    {
        if (TryParseSequence(fileName, out SequenceToken token))
        {
            prefix = token.Prefix;
            number = token.Number;
            width = token.Width;
            return true;
        }

        prefix = string.Empty;
        number = 0;
        width = 0;
        return false;
    }

    private static bool TryParseSequence(string fileName, out SequenceToken token)
    {
        token = null!;
        string name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Trim();
        if (name.Length < 4)
            return false;

        int end = name.Length - 1;
        while (end >= 0 && char.IsDigit(name[end])) end--;
        int start = end + 1;
        int width = name.Length - start;
        if (width is < 3 or > 8)
            return false;

        string prefix = name[..start].TrimEnd('_', '-', ' ');
        if (prefix.Length > 24)
            return false;
        if (!long.TryParse(name[start..], out long number))
            return false;

        token = new SequenceToken(prefix.ToUpperInvariant(), number, width);
        return true;
    }

    private static bool IsNeighbour(
        (RecoveryFileItem Item, SequenceToken Token) left,
        (RecoveryFileItem Item, SequenceToken Token) right)
    {
        long delta = right.Token.Number - left.Token.Number;
        if (delta is <= 0 or > 3)
            return false;

        DateTimeOffset? leftTime = RecoveryScanPriorityService.GetPriorityTime(left.Item);
        DateTimeOffset? rightTime = RecoveryScanPriorityService.GetPriorityTime(right.Item);
        if (leftTime.HasValue && rightTime.HasValue &&
            (rightTime.Value - leftTime.Value).Duration() > TimeSpan.FromHours(12))
            return false;

        return true;
    }
}
