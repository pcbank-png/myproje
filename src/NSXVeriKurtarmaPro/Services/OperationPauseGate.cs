namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Uzun suren salt-okunur tarama/kurtarma islemlerini guvenli bicimde
/// duraklatip devam ettirmek icin ortak kapi. Stop cagrisinda once Resume
/// edilerek bekleyen is parcaciginin CancellationToken'i gormesi saglanir.
/// </summary>
public sealed class OperationPauseGate : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private readonly ManualResetEventSlim _pauseAcknowledged = new(initialState: false);
    private volatile bool _isPaused;

    public bool IsPaused => _isPaused;

    public void Pause()
    {
        _pauseAcknowledged.Reset();
        _isPaused = true;
        _gate.Reset();
    }

    public void Resume()
    {
        _isPaused = false;
        _gate.Set();
        _pauseAcknowledged.Reset();
    }

    public bool WaitUntilPaused(TimeSpan timeout)
    {
        if (!_isPaused)
            return true;

        try
        {
            return _pauseAcknowledged.Wait(timeout);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Wait()
    {
        if (_isPaused)
            _pauseAcknowledged.Set();

        _gate.Wait();
    }

    public void Wait(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isPaused)
            _pauseAcknowledged.Set();

        _gate.Wait(cancellationToken);
    }

    public void Dispose()
    {
        _gate.Set();
        _gate.Dispose();
        _pauseAcknowledged.Set();
        _pauseAcknowledged.Dispose();
    }
}
