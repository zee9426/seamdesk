namespace Seamdesk.Transport;

/// <summary>A paired peer: its certificate's SPKI SHA-256 (hex), its machine name and the address it was last seen at.</summary>
public sealed record Pin(string Spki, string Name, string Address);

/// <summary>
/// The paired peers, in the order they paired, one per machine name. Persisting them is the host's job (they are public
/// key hashes, not secrets).
/// </summary>
public sealed class Pins
{
    private readonly List<Pin> pins = [];

    public Pins(IEnumerable<Pin>? initial = null)
    {
        foreach (Pin pin in initial ?? [])
        {
            Replace(pin);
        }
    }

    public event Action<Pin>? Added;

    public Pin? Find(string spki)
    {
        lock (pins)
        {
            return pins.Find(p => p.Spki == spki);
        }
    }

    public IReadOnlyList<Pin> All
    {
        get
        {
            lock (pins)
            {
                return [.. pins];
            }
        }
    }

    /// <summary>Forgets a peer: from now on its certificate is unknown, as if it never paired.</summary>
    public bool Remove(string spki)
    {
        lock (pins)
        {
            return pins.RemoveAll(p => p.Spki == spki) > 0;
        }
    }

    /// <summary>
    /// Pins a newly paired peer, last in the order. It replaces any pin with its key or its name: a machine that paired
    /// again with a new identity must not leave its old key trusted.
    /// </summary>
    public void Add(Pin pin)
    {
        lock (pins)
        {
            Replace(pin);
        }

        Added?.Invoke(pin);
    }

    private void Replace(Pin pin)
    {
        pins.RemoveAll(p => p.Spki == pin.Spki || p.Name == pin.Name);
        pins.Add(pin);
    }
}
