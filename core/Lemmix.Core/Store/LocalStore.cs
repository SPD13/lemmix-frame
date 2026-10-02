namespace Lemmix.Store;

// The web keeps every setting in localStorage (string keys, string values, getItem null when
// missing); the native app has the same store, kept in a JSON file the app names (the Frame's
// user data directory). Every web module's reads and writes go through this contract, so the
// ports read like the JS. There is no server mode: config-store.js's HTTP sync is not ported,
// only its build/apply of the three files (ConfigFiles.cs).
public interface IStorage
{
    string? GetItem(string key);
    void SetItem(string key, string value);
    void RemoveItem(string key);
}

// A localStorage in memory, saved to `path` as a JSON object of strings: a write schedules a save
// `debounceMs` later (one save however many keys change), Flush saves at once (the app quitting).
// The file is written to a temporary name and moved over the old one, so a crash mid-write leaves
// the previous settings. A file that does not parse starts the store empty and is kept beside it
// as <path>.bad rather than overwritten unseen.
public sealed class LocalStore : IStorage, IDisposable
{
    readonly object _lock = new();
    readonly JsObject _items = new(); // the JSON file's object: its key order is JS's
    readonly List<string> _order = new(); // insertion order, for Key(i)
    readonly string? _path;
    readonly int _debounceMs;
    Timer? _timer;
    bool _dirty;

    // raised after every change (key), as config-store.js hooks Storage.prototype.setItem
    public event Action<string>? Changed;

    public LocalStore(string? path = null, int debounceMs = 250)
    {
        _path = path;
        _debounceMs = debounceMs;
        if (path != null) Load(path);
    }

    void Load(string path)
    {
        if (!File.Exists(path)) return;
        string text = File.ReadAllText(path);
        if (text.Length > 0 && text[0] == '\ufeff') text = text[1..];
        object? data;
        try { data = JsJson.Parse(text); }
        catch (JsSyntaxError) { data = null; }
        if (data is not JsObject o)
        {
            try { File.Copy(path, path + ".bad", true); } catch (IOException) { }
            return;
        }
        foreach (var k in o.Keys)
        {
            var v = o.Get(k);
            if (v is string s) { _items.Set(k, s); _order.Add(k); }
        }
    }

    public string? GetItem(string key)
    {
        lock (_lock) return _items.Get(key) as string;
    }

    public void SetItem(string key, string value)
    {
        lock (_lock)
        {
            if (!_items.Has(key)) _order.Add(key);
            _items.Set(key, value);
            Touch();
        }
        Changed?.Invoke(key);
    }

    public void RemoveItem(string key)
    {
        lock (_lock)
        {
            if (!_items.Remove(key)) return;
            _order.Remove(key);
            Touch();
        }
        Changed?.Invoke(key);
    }

    public void Clear()
    {
        List<string> keys;
        lock (_lock)
        {
            keys = new List<string>(_order);
            foreach (var k in keys) _items.Remove(k);
            _order.Clear();
            Touch();
        }
        foreach (var k in keys) Changed?.Invoke(k);
    }

    public int Length { get { lock (_lock) return _order.Count; } }

    public string? Key(int index)
    {
        lock (_lock) return index >= 0 && index < _order.Count ? _order[index] : null;
    }

    // every entry, sorted by key (UTF-16 order, as JS's default sort)
    public List<KeyValuePair<string, string>> Entries()
    {
        lock (_lock)
            return _order.OrderBy(k => k, StringComparer.Ordinal).Select(k => new KeyValuePair<string, string>(k, (string)_items.Get(k)!)).ToList();
    }

    void Touch()
    {
        _dirty = true;
        if (_path == null) return;
        if (_debounceMs <= 0) { SaveLocked(); return; }
        _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(_debounceMs, Timeout.Infinite);
    }

    // a save soon (the debounce), as every change schedules one
    public void Save()
    {
        lock (_lock) Touch();
    }

    // the pending save, now
    public void Flush()
    {
        lock (_lock)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_dirty) SaveLocked();
        }
    }

    void SaveLocked()
    {
        if (_path == null) return;
        string text = JsJson.Stringify(_items, 2)!;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (dir != null) Directory.CreateDirectory(dir);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, text + "\n");
        File.Move(tmp, _path, true);
        _dirty = false;
    }

    public void Dispose()
    {
        Flush();
        _timer?.Dispose();
        _timer = null;
    }
}
