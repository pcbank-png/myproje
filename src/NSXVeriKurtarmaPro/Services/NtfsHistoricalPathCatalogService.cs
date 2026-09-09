namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Builds the left-side historical NTFS folder catalog independently from recovered files.
///
/// Important distinction: a recovery result having no resolved original path must not make the
/// folder disappear from YOL. EaseUS-style navigation is a directory-history catalog first;
/// recovered files are correlated to that catalog afterwards. This service therefore starts from
/// every directory FILE record found in the MFT (active or deleted) and augments missing/reused
/// parent links with exact 64-bit $I30 and $UsnJrnl evidence.
///
/// No media I/O is performed here. It consumes metadata that the NTFS scan has already read.
/// </summary>
internal static class NtfsHistoricalPathCatalogService
{
    private const ulong RecordMask = 0x0000FFFFFFFFFFFFUL;

    private sealed record FolderNode(
        ulong FileReference,
        ulong ParentReference,
        string Name,
        int Confidence);

    public static IReadOnlyList<string> Build(
        IReadOnlyDictionary<long, NtfsForensicMetadataService.DirectoryEntry> directories,
        IReadOnlyDictionary<ulong, NtfsDirectoryIndexRecoveryService.IndexPathEvidence>? indexEntries = null,
        IReadOnlyDictionary<ulong, NtfsUsnJournalService.JournalPathEvidence>? historicalEntries = null)
    {
        var nodes = new Dictionary<ulong, FolderNode>();
        var startReferences = new HashSet<ulong>();

        foreach (NtfsForensicMetadataService.DirectoryEntry directory in directories.Values)
        {
            if (!NtfsQuickScanService.IsUsableName(directory.Name) || directory.RecordIndex < 0)
                continue;

            ulong reference = ((ulong)directory.SequenceNumber << 48) |
                              ((ulong)directory.RecordIndex & RecordMask);
            if (reference == 0)
                continue;

            AddOrUpgrade(nodes, new FolderNode(
                reference,
                directory.ParentReference,
                directory.Name,
                directory.InUse ? 100 : 96));
            startReferences.Add(reference);
        }

        if (indexEntries is not null)
        {
            foreach (NtfsDirectoryIndexRecoveryService.IndexPathEvidence evidence in indexEntries.Values)
            {
                if (!evidence.IsDirectory || evidence.FileReferenceNumber == 0 ||
                    !NtfsQuickScanService.IsUsableName(evidence.FileName))
                {
                    continue;
                }

                AddOrUpgrade(nodes, new FolderNode(
                    evidence.FileReferenceNumber,
                    evidence.ParentReferenceNumber,
                    evidence.FileName,
                    Math.Clamp(evidence.Confidence, 1, 99)));
                startReferences.Add(evidence.FileReferenceNumber);
            }
        }

        // USN_RECORD_V2 carries FileAttributes. Directory-bit evidence lets the historical
        // catalog surface old folders even when their MFT slot has already been reused and no
        // current recovered file is linked to that path.
        if (historicalEntries is not null)
        {
            foreach (NtfsUsnJournalService.JournalPathEvidence evidence in historicalEntries.Values)
            {
                if (!evidence.IsDirectory || evidence.FileReferenceNumber == 0 ||
                    !NtfsQuickScanService.IsUsableName(evidence.FileName))
                {
                    continue;
                }

                AddOrUpgrade(nodes, new FolderNode(
                    evidence.FileReferenceNumber,
                    evidence.ParentReferenceNumber,
                    evidence.FileName,
                    92));
                startReferences.Add(evidence.FileReferenceNumber);
            }
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ulong startReference in startReferences)
        {
            string path = ResolveDirectoryPath(startReference, nodes, historicalEntries);
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        return paths
            .OrderBy(path => path.Count(ch => ch == '\\'))
            .ThenBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> BuildFromIndexEvidence(
        IReadOnlyDictionary<ulong, NtfsDirectoryIndexRecoveryService.IndexPathEvidence>? indexEntries)
    {
        if (indexEntries is null || indexEntries.Count == 0)
            return Array.Empty<string>();

        return Build(
            new Dictionary<long, NtfsForensicMetadataService.DirectoryEntry>(),
            indexEntries,
            historicalEntries: null);
    }

    private static string ResolveDirectoryPath(
        ulong startReference,
        IReadOnlyDictionary<ulong, FolderNode> nodes,
        IReadOnlyDictionary<ulong, NtfsUsnJournalService.JournalPathEvidence>? historicalEntries)
    {
        var names = new List<string>(16);
        var visited = new HashSet<ulong>();
        ulong current = startReference;
        bool reachedRoot = false;

        for (int depth = 0; depth < 96; depth++)
        {
            long recordIndex = (long)(current & RecordMask);
            if (recordIndex == 5)
            {
                reachedRoot = true;
                break;
            }

            if (current == 0 || !visited.Add(current))
                break;

            if (nodes.TryGetValue(current, out FolderNode? node) && node is not null)
            {
                if (NtfsQuickScanService.IsUsableName(node.Name) && node.Name is not "." and not "..")
                    names.Add(node.Name);

                if (node.ParentReference == 0 || node.ParentReference == current)
                    break;

                current = node.ParentReference;
                continue;
            }

            // USN evidence is only used while walking a reference that is already known to be
            // a directory because a child points to it. This avoids treating ordinary file USN
            // records as folders while still restoring deleted/reused parent names.
            if (historicalEntries is not null &&
                historicalEntries.TryGetValue(current, out NtfsUsnJournalService.JournalPathEvidence? historical) &&
                historical is not null &&
                NtfsQuickScanService.IsUsableName(historical.FileName))
            {
                names.Add(historical.FileName);
                if (historical.ParentReferenceNumber == 0 || historical.ParentReferenceNumber == current)
                    break;

                current = historical.ParentReferenceNumber;
                continue;
            }

            break;
        }

        if (!reachedRoot || names.Count == 0)
            return string.Empty;

        names.Reverse();
        return "\\" + string.Join("\\", names);
    }

    private static void AddOrUpgrade(IDictionary<ulong, FolderNode> nodes, FolderNode candidate)
    {
        if (!nodes.TryGetValue(candidate.FileReference, out FolderNode? existing) ||
            existing is null || candidate.Confidence > existing.Confidence)
        {
            nodes[candidate.FileReference] = candidate;
        }
    }
}
