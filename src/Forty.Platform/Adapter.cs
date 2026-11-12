using Forty.Core;

namespace Forty.Platform;

/// <summary>
/// A source of facts from Windows. Adapters raise their events on whatever thread Windows uses; the
/// app moves them to the UI thread. A failing adapter reports itself Unavailable instead of throwing,
/// so the app can hide what cannot work (F138).
/// </summary>
public abstract class Adapter : IDisposable
{
    protected Adapter(string name) => Status = AdapterStatus.Ok(name);

    public AdapterStatus Status { get; private set; }
    public string Name => Status.Name;
    public event Action<AdapterStatus>? StatusChanged;

    /// <summary>Starts listening. Never throws: problems become the adapter's status.</summary>
    public void Start()
    {
        if (Faults.IsDown(Name))
        {
            Report(Availability.Unavailable, "switched off for a test (FORTY_FAULT)");
            return;
        }
        try { OnStart(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(Availability.Unavailable, ex.Message);
        }
    }

    protected abstract void OnStart();

    protected void Report(Availability availability, string? reason = null)
    {
        var status = new AdapterStatus(Name, availability, reason);
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
