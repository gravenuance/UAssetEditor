using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace UAssetAPI.Unversioned
{
    /// <summary>
    /// A dictionary whose writes land in a private layer while reads fall through to a shared,
    /// read-only base. Lets many assets read one loaded .usmap concurrently while each registers
    /// its own blueprint schemas without seeing the others'.
    /// </summary>
    public sealed class OverlayDictionary<TValue> : IDictionary<string, TValue>
    {
        private readonly IDictionary<string, TValue> _shared;
        private readonly Dictionary<string, TValue> _local;

        public OverlayDictionary(IDictionary<string, TValue> shared, IEqualityComparer<string> comparer)
        {
            _shared = shared;
            _local = new Dictionary<string, TValue>(comparer);
        }

        public TValue this[string key]
        {
            get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
            set => _local[key] = value;
        }

        public bool TryGetValue(string key, out TValue value) => _local.TryGetValue(key, out value) || (_shared?.TryGetValue(key, out value) ?? false);

        public bool ContainsKey(string key) => _local.ContainsKey(key) || (_shared?.ContainsKey(key) ?? false);

        public void Add(string key, TValue value)
        {
            if (ContainsKey(key)) throw new System.ArgumentException("An item with the same key has already been added.", nameof(key));
            _local.Add(key, value);
        }

        public bool Remove(string key) => _local.Remove(key);

        public ICollection<string> Keys => this.Select(pair => pair.Key).ToList();

        public ICollection<TValue> Values => this.Select(pair => pair.Value).ToList();

        public int Count => _local.Count + (_shared?.Keys.Count(key => !_local.ContainsKey(key)) ?? 0);

        public bool IsReadOnly => false;

        public void Add(KeyValuePair<string, TValue> item) => Add(item.Key, item.Value);

        public void Clear() => _local.Clear();

        public bool Contains(KeyValuePair<string, TValue> item) => TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);

        public void CopyTo(KeyValuePair<string, TValue>[] array, int arrayIndex)
        {
            foreach (var pair in this) array[arrayIndex++] = pair;
        }

        public bool Remove(KeyValuePair<string, TValue> item) => Contains(item) && _local.Remove(item.Key);

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
        {
            foreach (var pair in _local) yield return pair;
            if (_shared == null) yield break;
            foreach (var pair in _shared)
            {
                if (!_local.ContainsKey(pair.Key)) yield return pair;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
