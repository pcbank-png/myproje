using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed record RecoveryPathTreeEntry(
    string Path,
    string ParentPath,
    string Title,
    int Depth,
    int FileCount,
    int SelectedFileCount,
    bool HasChildren)
{
    public bool? SelectionState => FileCount == 0
        ? false
        : SelectedFileCount == 0
            ? false
            : SelectedFileCount == FileCount
                ? true
                : null;
}

public sealed record RecoveryPathTreeSnapshot(
    IReadOnlyList<RecoveryPathTreeEntry> Entries,
    int FileCount,
    int SelectedFileCount,
    int UnresolvedFileCount,
    int SelectedUnresolvedFileCount)
{
    public bool? RootSelectionState => FileCount == 0
        ? false
        : SelectedFileCount == 0
            ? false
            : SelectedFileCount == FileCount
                ? true
                : null;

    public bool? UnresolvedSelectionState => UnresolvedFileCount == 0
        ? false
        : SelectedUnresolvedFileCount == 0
            ? false
            : SelectedUnresolvedFileCount == UnresolvedFileCount
                ? true
                : null;
}

/// <summary>
/// Builds the volume-relative folder tree shown by the recovery workspace.
/// The returned entries are in pre-order, so every expanded folder is followed
/// immediately by its descendants instead of being grouped by depth.
/// </summary>
public static class RecoveryPathTreeService
{
    private sealed class FolderAccumulator
    {
        public required string Path { get; init; }
        public required string ParentPath { get; init; }
        public required string Title { get; init; }
        public required int Depth { get; init; }
        public int FileCount { get; set; }
        public int SelectedFileCount { get; set; }
    }

    public static RecoveryPathTreeSnapshot Build(
        IEnumerable<RecoveryFileItem>? files,
        IEnumerable<string>? historicalFolders)
    {
        var folders = new Dictionary<string, FolderAccumulator>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;
        int selectedFileCount = 0;
        int unresolvedFileCount = 0;
        int selectedUnresolvedFileCount = 0;

        // Result paths are processed first so their original casing wins over a
        // stale metadata-only spelling of the same folder.
        foreach (RecoveryFileItem file in files ?? [])
        {
            fileCount++;
            if (file.IsChecked)
                selectedFileCount++;

            string? folderPath = TryGetFolderPath(file);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                unresolvedFileCount++;
                if (file.IsChecked)
                    selectedUnresolvedFileCount++;
                continue;
            }

            AddPath(folders, folderPath, incrementCount: true, file.IsChecked);
        }

        foreach (string historicalPath in historicalFolders ?? [])
        {
            if (!string.IsNullOrWhiteSpace(historicalPath))
                AddPath(folders, historicalPath, incrementCount: false, selected: false);
        }

        var childrenByParent = folders.Values
            .GroupBy(folder => folder.ParentPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(folder => folder.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var entries = new List<RecoveryPathTreeEntry>(folders.Count);
        if (childrenByParent.TryGetValue("\\", out List<FolderAccumulator>? rootChildren))
        {
            var pending = new Stack<FolderAccumulator>(rootChildren.Count);
            for (int index = rootChildren.Count - 1; index >= 0; index--)
                pending.Push(rootChildren[index]);

            while (pending.Count > 0)
            {
                FolderAccumulator folder = pending.Pop();
                bool hasChildren = childrenByParent.TryGetValue(folder.Path, out List<FolderAccumulator>? children)
                                   && children.Count > 0;
                entries.Add(new RecoveryPathTreeEntry(
                    folder.Path,
                    folder.ParentPath,
                    folder.Title,
                    folder.Depth,
                    folder.FileCount,
                    folder.SelectedFileCount,
                    hasChildren));

                if (!hasChildren)
                    continue;

                for (int index = children!.Count - 1; index >= 0; index--)
                    pending.Push(children[index]);
            }
        }

        return new RecoveryPathTreeSnapshot(
            entries,
            fileCount,
            selectedFileCount,
            unresolvedFileCount,
            selectedUnresolvedFileCount);
    }

    public static string? TryGetFolderPath(RecoveryFileItem file)
    {
        if (!string.IsNullOrWhiteSpace(file.RecoveredOriginalPath))
            return NormalizeFolderPath(file.RecoveredFolderPath);

        const string folderPrefix = "Klasör •";
        if (!file.SourceText.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string sourcePath = file.SourceText[folderPrefix.Length..].Trim();
        return NormalizeFolderPath(string.IsNullOrWhiteSpace(sourcePath) ? "\\" : sourcePath);
    }

    public static bool MatchesFolder(RecoveryFileItem file, string requestedFolder)
    {
        string? actualFolder = TryGetFolderPath(file);
        return actualFolder is not null && IsSameOrDescendant(actualFolder, requestedFolder);
    }

    public static bool IsSameOrDescendant(string candidatePath, string requestedFolder)
    {
        string candidate = NormalizeFolderPath(candidatePath);
        string requested = NormalizeFolderPath(requestedFolder);
        return requested == "\\"
            ? candidate == "\\"
            : candidate.Equals(requested, StringComparison.OrdinalIgnoreCase)
              || candidate.StartsWith(requested + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeFolderPath(string? path)
    {
        string value = (path ?? string.Empty).Trim().Trim('"').Replace('/', '\\');
        if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            value = value[8..];
        else if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
            value = value[2..];

        var parts = new List<string>();
        foreach (string rawPart in value.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPart == ".")
                continue;
            if (rawPart == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(rawPart);
        }

        return parts.Count == 0 ? "\\" : "\\" + string.Join('\\', parts);
    }

    private static void AddPath(
        IDictionary<string, FolderAccumulator> folders,
        string path,
        bool incrementCount,
        bool selected)
    {
        string normalized = NormalizeFolderPath(path);
        if (normalized == "\\")
            return;

        string current = string.Empty;
        string parent = "\\";
        int depth = 0;
        foreach (string part in normalized.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            depth++;
            current += "\\" + part;
            if (!folders.TryGetValue(current, out FolderAccumulator? folder))
            {
                folder = new FolderAccumulator
                {
                    Path = current,
                    ParentPath = parent,
                    Title = part,
                    Depth = depth
                };
                folders.Add(current, folder);
            }

            if (incrementCount)
            {
                folder.FileCount++;
                if (selected)
                    folder.SelectedFileCount++;
            }

            parent = current;
        }
    }
}
