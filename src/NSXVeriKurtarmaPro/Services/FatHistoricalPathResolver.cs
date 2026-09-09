using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Rebuilds historical FAT directory paths from on-disk directory relationships only.
/// No filename/path is guessed from file content. A path is emitted only when a real
/// parent directory entry (and, where available, FAT '.'/'..' evidence) connects it to
/// a proven live or historical volume root.
/// </summary>
internal static class FatHistoricalPathResolver
{
    internal sealed record EntryEvidence(
        uint ContainerCluster,
        bool IsDirectory,
        uint FirstCluster,
        string Name,
        bool Deleted);

    internal sealed record PathState(string Path, bool Historical, int Confidence);

    internal static Dictionary<uint, PathState> Build(
        IReadOnlyList<EntryEvidence> entries,
        IReadOnlyDictionary<uint, uint>? parentHints,
        uint rootContainerCluster,
        bool rootIsDataCluster,
        bool allowHistoricalRootInference,
        Func<uint, IReadOnlyList<uint>> enumerateDirectoryClusters)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(enumerateDirectoryClusters);

        var paths = new Dictionary<uint, PathState>();
        var chainCache = new Dictionary<uint, IReadOnlyList<uint>>();
        var childClusters = new HashSet<uint>();
        var containerClusters = new HashSet<uint>(entries.Select(item => item.ContainerCluster));
        // Index directory edges once: formatted media can contain hundreds of thousands
        // of entries, so scanning the entire catalog for each orphan root is quadratic.
        var directoriesByContainer = entries
            .Where(item => item.IsDirectory && item.FirstCluster != 0)
            .GroupBy(item => item.ContainerCluster)
            .ToDictionary(group => group.Key, group => group.ToArray());

        IReadOnlyList<uint> GetChain(uint firstCluster)
        {
            if (chainCache.TryGetValue(firstCluster, out IReadOnlyList<uint>? cachedChain) && cachedChain is not null)
                return cachedChain;

            IReadOnlyList<uint> resolvedChain = enumerateDirectoryClusters(firstCluster) ?? Array.Empty<uint>();
            if (resolvedChain.Count == 0 && firstCluster != 0)
                resolvedChain = new[] { firstCluster };

            chainCache[firstCluster] = resolvedChain;
            return resolvedChain;
        }

        foreach (EntryEvidence item in entries)
        {
            if (!item.IsDirectory || item.FirstCluster == 0)
                continue;
            foreach (uint cluster in GetChain(item.FirstCluster))
                childClusters.Add(cluster);
        }

        var rootState = new PathState("\\", Historical: false, Confidence: 10000);
        if (rootIsDataCluster)
            RegisterDirectoryChain(rootContainerCluster, rootState, GetChain, paths);
        else
            paths[rootContainerCluster] = rootState;

        if (allowHistoricalRootInference)
        {
            // FAT32/exFAT style formatted media can have an old root directory cluster that is
            // no longer the current BPB root. We only accept an orphan root when its directory
            // relationships are self-consistent: it owns a child directory and the child's '..'
            // record points back to the candidate parent, or the child itself contains further
            // directory evidence. This anchors old camera trees without fabricating a folder.
            foreach (uint candidate in containerClusters)
            {
                if (candidate == 0 || candidate == rootContainerCluster || childClusters.Contains(candidate))
                    continue;

                bool confirmed = false;
                if (!directoriesByContainer.TryGetValue(candidate, out EntryEvidence[]? edges))
                    continue;
                foreach (EntryEvidence edge in edges)
                {
                    if (edge.ContainerCluster != candidate || !edge.IsDirectory || edge.FirstCluster == 0)
                        continue;

                    if (parentHints is not null &&
                        parentHints.TryGetValue(edge.FirstCluster, out uint hintedParent) &&
                        (hintedParent == candidate || hintedParent == 0))
                    {
                        confirmed = true;
                        break;
                    }

                    if (containerClusters.Contains(edge.FirstCluster) &&
                        directoriesByContainer.ContainsKey(edge.FirstCluster))
                    {
                        confirmed = true;
                        break;
                    }
                }

                if (!confirmed)
                    continue;

                var historicalRoot = new PathState("\\", Historical: true, Confidence: 7000);
                paths[candidate] = historicalRoot;
            }
        }

        // Resolve from proven roots outwards. A deleted edge or a historical parent keeps every
        // descendant historical even when its individual entry still has the active bit set.
        for (int pass = 0; pass < 128; pass++)
        {
            bool changed = false;
            foreach (EntryEvidence item in entries)
            {
                if (!item.IsDirectory || item.FirstCluster == 0 ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Name is "." or "..")
                {
                    continue;
                }

                if (!paths.TryGetValue(item.ContainerCluster, out PathState? parent) || parent is null)
                    continue;

                bool historical = parent.Historical || item.Deleted;
                int confidence = Math.Max(1, parent.Confidence - (item.Deleted ? 20 : 1));
                string childPath = Combine(parent.Path, item.Name);
                var candidate = new PathState(childPath, historical, confidence);

                foreach (uint cluster in GetChain(item.FirstCluster))
                {
                    if (!paths.TryGetValue(cluster, out PathState? existing) || existing is null || IsBetter(candidate, existing))
                    {
                        paths[cluster] = candidate;
                        changed = true;
                    }
                }
            }

            if (!changed)
                break;
        }

        return paths;
    }

    private static void RegisterDirectoryChain(
        uint firstCluster,
        PathState state,
        Func<uint, IReadOnlyList<uint>> getChain,
        IDictionary<uint, PathState> paths)
    {
        if (firstCluster == 0)
            return;
        foreach (uint cluster in getChain(firstCluster))
            paths[cluster] = state;
    }

    private static bool IsBetter(PathState candidate, PathState existing)
    {
        if (candidate.Confidence != existing.Confidence)
            return candidate.Confidence > existing.Confidence;
        if (candidate.Historical != existing.Historical)
            return !candidate.Historical;

        int candidateDepth = candidate.Path.Count(ch => ch == '\\');
        int existingDepth = existing.Path.Count(ch => ch == '\\');
        if (candidateDepth != existingDepth)
            return candidateDepth < existingDepth;

        return string.Compare(candidate.Path, existing.Path, StringComparison.OrdinalIgnoreCase) < 0;
    }

    internal static string Combine(string directoryPath, string name)
    {
        string cleanName = FileTypeHelper.SanitizeFileName(name);
        if (string.IsNullOrWhiteSpace(directoryPath) || directoryPath == "\\")
            return $"\\{cleanName}";
        return $"{directoryPath.TrimEnd('\\')}\\{cleanName}";
    }
}
