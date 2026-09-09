using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Keeps directory evidence even when the directory contains no recoverable files.
/// Used synchronously by one scan worker; forwards immutable batches to the UI.
/// </summary>
internal sealed class HistoricalFolderProgress(IProgress<OperationProgress>? target)
    : IProgress<OperationProgress>
{
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pending = [];

    public void Add(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string normalized = RecoveryPathTreeService.NormalizeFolderPath(path);
        if (normalized != "\\" && _folders.Add(normalized))
            _pending.Add(normalized);
    }

    public void Report(OperationProgress value)
    {
        foreach (string path in value.NewHistoricalFolders ?? [])
            Add(path);
        string[] batch = _pending.ToArray();
        _pending.Clear();
        target?.Report(value with { NewHistoricalFolders = batch.Length == 0 ? null : batch });
    }

    public IReadOnlyList<string> Snapshot() =>
        _folders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
}
