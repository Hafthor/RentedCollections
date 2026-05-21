using System.Buffers;
using System.Collections;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace com.hafthor.RentedCollections;

public class RentedList<T> : IList<T>, IReadOnlyList<T>, IDisposable {
    private const int DefaultCapacity = 256;

    private int count;
    private T[] values;
    private readonly ArrayPool<T> arrayPool;

    public RentedList(ArrayPool<T> arrayPool = null) : this(DefaultCapacity, arrayPool) {
    }

    public RentedList(int capacity, ArrayPool<T> arrayPool = null) {
        this.arrayPool = arrayPool ?? ArrayPool<T>.Shared;
        values = this.arrayPool.Rent(capacity);
    }

    public RentedList(IEnumerable<T> collection, ArrayPool<T> arrayPool = null) {
        this.arrayPool = arrayPool ?? ArrayPool<T>.Shared;
        values = this.arrayPool.Rent(collection is ICollection<T> coll ? coll.Count : DefaultCapacity);
        try {
            AddRange(collection);
        } catch {
            if (typeof(T).IsClass) Array.Clear(values);
            this.arrayPool.Return(values);
            throw;
        }
    }

    public void Dispose() {
        if (typeof(T).IsClass) Array.Clear(values, 0, count);
        arrayPool.Return(values);
        count = 0;
        values = null;
    }
    void IDisposable.Dispose() => Dispose();

    public int Capacity => values.Length;
    public int Count => count;
    public bool IsReadOnly => false;
    public ReadOnlyCollection<T> AsReadOnly() => new(this);

    private void CheckIndex(int index, int length = int.MinValue,
        [CallerArgumentExpression(nameof(index))] string parameterName = null,
        [CallerArgumentExpression(nameof(length))] string lengthParameterName = null) {
        ArgumentOutOfRangeException.ThrowIfNegative(index, parameterName);
        if (length != int.MinValue) {
            ArgumentOutOfRangeException.ThrowIfNegative(length, lengthParameterName);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(index, count - length, parameterName);
        } else
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, parameterName);
    }

    public T this[int index] {
        get {
            if (index < 0) index += count;
            CheckIndex(index);
            return values[index];
        }
        set {
            if (index < 0) index += count;
            CheckIndex(index);
            values[index] = value;
        }
    }

    private T[] GrowCapacityNoCopy(long newMinimumCapacity, bool alwaysAllocate = false) {
        if (!alwaysAllocate && newMinimumCapacity <= values.Length) return null;
        long newCapacity = values.Length;
        while (newCapacity < newMinimumCapacity) newCapacity *= 2L;
        T[] newValues = arrayPool.Rent((int)Math.Min(newCapacity, int.MaxValue)), oldValues = values;
        values = newValues;
        return oldValues;
    }

    private int GrowCapacity(long newMinimumCapacity) {
        if (newMinimumCapacity > int.MaxValue) throw new OutOfMemoryException("Cannot allocate enough memory due to list size limitation.");
        T[] oldValues = GrowCapacityNoCopy(newMinimumCapacity);
        if (oldValues is null) return values.Length;
        Array.Copy(oldValues, 0, values, 0, count);
        if (typeof(T).IsClass) Array.Clear(oldValues, 0, count);
        arrayPool.Return(oldValues);
        return values.Length;
    }

    public void TrimExcess() {
        if (values.Length == count) return;
        T[] oldValues = GrowCapacityNoCopy(count, true);
        if (values.Length == oldValues.Length) {
            // if we didn't shrink, restore oldValues and return the new array
            arrayPool.Return(values);
            values = oldValues;
        } else {
            Array.Copy(values, oldValues, count);
            if (typeof(T).IsClass) Array.Clear(oldValues, 0, count);
            arrayPool.Return(oldValues);
        }
    }

    public void Add(T item) => Insert(count, item);

    public void Insert(Index start, T item) => Insert(start.GetOffset(count), item);

    public void Insert(int startIndex, T item) {
        if (startIndex < 0) startIndex += count + 1;
        if (startIndex != count) CheckIndex(startIndex);
        T[] oldValues = GrowCapacityNoCopy(count + 1L);
        Array.Copy(oldValues ?? values, startIndex, values, startIndex + 1, count - startIndex);
        if (oldValues is not null) {
            Array.Copy(oldValues, values, startIndex);
            if (typeof(T).IsClass) Array.Clear(oldValues, 0, count);
            arrayPool.Return(oldValues);
        }
        values[startIndex] = item;
        count++;
    }

    public void AddRange(ReadOnlySpan<T> collection) {
        GrowCapacity(count + collection.Length);
        foreach (var item in collection)
            Add(item);
    }

    public void AddRange(IEnumerable<T> collection) {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection is IReadOnlyCollection<T> coll)
            GrowCapacity(count + coll.Count);
        foreach (var item in collection)
            Add(item);
    }

    public void InsertRange(Index start, ReadOnlySpan<T> collection) => InsertRange(start.GetOffset(count), collection);
    public void InsertRange(int startIndex, ReadOnlySpan<T> collection) {
        if (startIndex < 0) startIndex += count + 1;
        if (startIndex == count) {
            AddRange(collection);
            return;
        }
        CheckIndex(startIndex);
        T[] oldValues = GrowCapacityNoCopy(count + collection.Length);
        Array.Copy(oldValues ?? values, startIndex, values, startIndex + collection.Length, count - startIndex);
        if (oldValues is not null) {
            Array.Copy(oldValues, values, startIndex);
            if (typeof(T).IsClass) Array.Clear(oldValues, 0, count);
            arrayPool.Return(oldValues);
        }
        int di = startIndex;
        foreach (var item in collection)
            values[di++] = item;
        count += collection.Length;
    }

    public void InsertRange(Index start, IEnumerable<T> collection) => InsertRange(start.GetOffset(count), collection);
    public void InsertRange(int startIndex, IEnumerable<T> collection) {
        if (startIndex < 0) startIndex += count;
        if (startIndex == count) {
            AddRange(collection);
            return;
        }
        ArgumentNullException.ThrowIfNull(collection);
        CheckIndex(startIndex);
        T[] oldValues;
        if (collection is IReadOnlyCollection<T> coll) {
            oldValues = GrowCapacityNoCopy(count + coll.Count);
            if (oldValues is null) {
                Array.Copy(values, startIndex, values, startIndex + coll.Count, count - startIndex);
                foreach (var item in collection) {
                    values[startIndex++] = item;
                    count++;
                }
                return;
            }
        } else {
            oldValues = GrowCapacityNoCopy(values.Length, true);
        }

        Array.Copy(oldValues, 0, values, 0, startIndex);
        int destinationIndex = startIndex, remaining = count - startIndex, oldCount = count;
        count = startIndex;
        foreach (var item in collection) {
            GrowCapacity(destinationIndex + remaining);
            values[destinationIndex++] = item;
            count++;
        }
        Array.Copy(oldValues, startIndex, values, destinationIndex, remaining);
        count += remaining;
        if (typeof(T).IsClass) Array.Clear(oldValues, 0, oldCount);
        arrayPool.Return(oldValues);
    }

    public int BinarySearch(T item, IComparer<T> comparer = null) =>
        BinarySearch(0, count, item, comparer);

    public int BinarySearch(Range range, T item, IComparer<T> comparer = null) {
        var ol = range.GetOffsetAndLength(count);
        return BinarySearch(ol.Offset, ol.Length, item, comparer);
    }
    
    public int BinarySearch(int startIndex, int length, T item, IComparer<T> comparer = null) {
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        return Array.BinarySearch(values, startIndex, length, item, comparer ?? Comparer<T>.Default);
    }

    public void Clear() {
        if (typeof(T).IsClass) Array.Clear(values, 0, count);
        count = 0;
    }

    public bool Contains(T item) => IndexOf(item) >= 0;

    public bool Exists(Predicate<T> match) => FindIndex(match) >= 0;

    public int IndexOf(T item) => IndexOf(item, 0);

    public int IndexOf(T item, Index start) => IndexOf(item, start.GetOffset(count));
    public int IndexOf(T item, int startIndex) {
        if (startIndex < 0) startIndex += count;
        CheckIndex(startIndex);
        for (int i = startIndex; i < count; i++)
            if (Equals(item, values[i]))
                return i;
        return -1;
    }

    public int LastIndexOf(T item, Index start) => LastIndexOf(item, start.GetOffset(count));
    public int LastIndexOf(T item, int startIndex = -1) {
        if (startIndex < 0) startIndex += count;
        CheckIndex(startIndex);
        for (; startIndex >= 0; startIndex--)
            if (Equals(item, values[startIndex]))
                return startIndex;
        return -1;
    }

    public T Find(Predicate<T> match) {
        int index = FindIndex(match);
        return index >= 0 ? values[index] : default;
    }

    public int FindIndex(Predicate<T> match) => FindIndex(0, count, match);
    public int FindIndex(Index start, Predicate<T> match) => FindIndex(start.GetOffset(count), match);
    public int FindIndex(Range range, Predicate<T> match) {
        var ol = range.GetOffsetAndLength(count);
        return FindIndex(ol.Offset, ol.Length, match);
    }
    public int FindIndex(int startIndex, Predicate<T> match) => FindIndex(startIndex, count - startIndex, match);
    public int FindIndex(int startIndex, int length, Predicate<T> match) {
        ArgumentNullException.ThrowIfNull(match);
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        for (int i = startIndex; i < startIndex+length; i++)
            if (match(values[i]))
                return i;
        return -1;
    }

    public T FindLast(Predicate<T> match) {
        int index = FindLastIndex(match);
        return index >= 0 ? values[index] : default;
    }

    public int FindLastIndex(Predicate<T> match) => FindLastIndex(count - 1, count, match);

    public int FindLastIndex(int startIndex, Predicate<T> match) =>
        FindLastIndex(startIndex, startIndex, match);

    public int FindLastIndex(Index start, Predicate<T> match) => FindLastIndex(start.GetOffset(count), match);
    public int FindLastIndex(Range range, Predicate<T> match) {
        var ol = range.GetOffsetAndLength(count);
        return FindLastIndex(ol.Offset, ol.Length, match);
    }
    public int FindLastIndex(int startIndex, int length, Predicate<T> match) {
        ArgumentNullException.ThrowIfNull(match);
        if (startIndex < 0) startIndex += count + 1;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, startIndex + 1);
        for (int i = startIndex - 1; i >= startIndex - length; i--)
            if (match(values[i]))
                return i;
        return -1;
    }

    public int EnsureCapacity(int capacity) => GrowCapacity(capacity);

    public List<T> FindAll(Predicate<T> match, Range range) {
        var ol = range.GetOffsetAndLength(count);
        return FindAll(match, ol.Offset, ol.Length);
    }
    public List<T> FindAll(Predicate<T> match, int startIndex = 0, int length = -1) {
        ArgumentNullException.ThrowIfNull(match);
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        List<T> list = [];
        for (int i = startIndex; i < length; i++)
            if (match(values[i]))
                list.Add(values[i]);
        return list;
    }

    public RentedList<T> FindAllRented(Predicate<T> match, Range range) {
        var ol = range.GetOffsetAndLength(count);
        return FindAllRented(match, ol.Offset, ol.Length);
    }
    public RentedList<T> FindAllRented(Predicate<T> match, int startIndex = 0, int length = -1) {
        ArgumentNullException.ThrowIfNull(match);
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count+1;
        CheckIndex(startIndex, length);
        RentedList<T> list = new();
        try {
            foreach (var item in this)
                if (match(item))
                    list.Add(item);
            return list;
        } catch {
            list.Dispose();
            throw;
        }
    }

    [OverloadResolutionPriority(1)]
    public void ForEach(Action<T> action, Range range) {
        var ol = range.GetOffsetAndLength(count);
        ForEach(action, ol.Offset, ol.Length);
    }
    [OverloadResolutionPriority(1)]
    public void ForEach(Action<T> action, int startIndex = 0, int length = -1) {
        ArgumentNullException.ThrowIfNull(action);
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count+1;
        CheckIndex(startIndex, length);
        for (int i = startIndex; i < startIndex + length; i++)
            action(values[i]);
    }
    public void ForEach(Action<T, int> action, Range range) {
        var ol = range.GetOffsetAndLength(count);
        ForEach(action, ol.Offset, ol.Length);
    }
    public void ForEach(Action<T, int> action, int startIndex = 0, int length = -1) {
        ArgumentNullException.ThrowIfNull(action);
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count+1;
        CheckIndex(startIndex, length);
        for (int i = startIndex; i < startIndex + length; i++)
            action(values[i], i);
    }

    public bool TrueForAll(Predicate<T> match, Range range) {
        var ol = range.GetOffsetAndLength(count);
        return TrueForAll(match, ol.Offset, ol.Length);
    }
    public bool TrueForAll(Predicate<T> match, int startIndex = 0, int length = -1) {
        ArgumentNullException.ThrowIfNull(match);
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        for (int i = startIndex; i < startIndex + length; i++)
            if (!match(values[i]))
                return false;
        return true;
    }

    public IEnumerator<T> GetEnumerator(Range range) {
        var ol = range.GetOffsetAndLength(count);
        return GetEnumerator(ol.Offset, ol.Length);
    }
    public IEnumerator<T> GetEnumerator(int startIndex, int length = -1) {
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        for (int i = startIndex; i < startIndex + length; i++)
            yield return values[i];
    }
    
    public IEnumerator<T> GetEnumerator() {
        for (int i = 0; i < count; i++)
            yield return values[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public List<T> GetRange(Range range) {
        var ol = range.GetOffsetAndLength(count);
        return GetRange(ol.Offset, ol.Length);
    }
    public List<T> GetRange(int startIndex = 0, int length = -1) {
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        List<T> list = new(length);
        list.AddRange(values.AsSpan(startIndex, length));
        return list;
    }

    public List<T> Slice(Range range) => GetRange(range);
    public List<T> Slice(int startIndex = 0, int length = -1) => GetRange(startIndex, length);

    public RentedList<T> GetRangeRented(Range range) {
        var ol = range.GetOffsetAndLength(count);
        return GetRangeRented(ol.Offset, ol.Length);
    }
    public RentedList<T> GetRangeRented(int startIndex = 0, int length = -1) {
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        RentedList<T> list = new(length);
        list.AddRange(values.AsSpan(startIndex, length));
        return list;
    }

    public RentedList<T> SliceRented(Range range) => GetRangeRented(range);
    public RentedList<T> SliceRented(int startIndex = 0, int length = -1) => GetRangeRented(startIndex, length);

    public bool Remove(T item) {
        int index = IndexOf(item);
        bool found = index >= 0;
        if (found) RemoveAt(index);
        return found;
    }

    public void RemoveAt(Index index) => RemoveAt(index.GetOffset(count));
    public void RemoveAt(int index) {
        if (index < 0) index += count;
        CheckIndex(index);
        Array.Copy(values, index + 1, values, index, count - index - 1);
        count--;
        if (typeof(T).IsClass) values[count] = default;
    }

    public void RemoveRange(Range range) {
        var ol = range.GetOffsetAndLength(count);
        RemoveRange(ol.Offset, ol.Length);
    }
    public void RemoveRange(int startIndex = 0, int length = -1) {
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        count -= length;
        Array.Copy(values, startIndex + length, values, startIndex, count - startIndex);
        if (typeof(T).IsClass) Array.Clear(values, count, length);
    }

    public void Reverse(Range range) {
        var ol = range.GetOffsetAndLength(count);
        Reverse(ol.Offset, ol.Length);
    }
    public void Reverse(int startIndex = 0, int length = -1) {
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        Array.Reverse(values, startIndex, length);
    }

    public void Sort(Range range) {
        var ol = range.GetOffsetAndLength(count);
        Sort(ol.Offset, ol.Length);
    }
    public void Sort(int startIndex = 0, int length = -1, IComparer<T> comparer = null) {
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        Array.Sort(values, startIndex, length, comparer ?? Comparer<T>.Default);
    }

    [OverloadResolutionPriority(1)]
    public void Sort(IComparer<T> comparer = null) => Sort(0, count, comparer);

    public void CopyTo(T[] array, Index arrayIndex) => CopyTo(array, arrayIndex.GetOffset(count));

    public void CopyTo(T[] array, int arrayIndex) {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length - count)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        Array.Copy(values, 0, array, arrayIndex, count);
    }

    public T[] ToArray(Range range) {
        var ol = range.GetOffsetAndLength(count);
        return ToArray(ol.Offset, ol.Length);
    }
    public T[] ToArray(int startIndex = 0, int length = -1) {
        if (startIndex < 0) startIndex += count;
        if (length < 0) length += count + 1;
        CheckIndex(startIndex, length);
        T[] dest = new T[length];
        values.AsSpan(startIndex, length).CopyTo(dest);
        return dest;
    }
}

[TestClass]
public class RentedListTests {
    private class TrackingPool<T> : ArrayPool<T> {
        public List<T[]> Renting { get; } = [];
        public List<T[]> Rented { get; } = [];

        public override T[] Rent(int minimumLength) {
            while (!int.IsPow2(minimumLength)) minimumLength++;
            var a = new T[minimumLength];
            Renting.Add(a);
            Rented.Add(a);
            return a;
        }

        public override void Return(T[] array, bool clearArray = false) {
            Assert.IsFalse(clearArray);
            Assert.IsTrue(Renting.Remove(array));
            if (typeof(T).IsClass) Assert.IsTrue(array.All(v => v is null));
        }
    }

    private class MirrorList<T> : IList<T>, IReadOnlyCollection<T>, IDisposable {
        private readonly List<T> list;
        private readonly RentedList<T> list2;
        private readonly ArrayPool<T> arrayPool;

        public RentedList<T> List => list2;

        public MirrorList(ArrayPool<T> arrayPool = null) {
            list = new();
            list2 = new(this.arrayPool = arrayPool);
        }

        public MirrorList(int capacity, ArrayPool<T> arrayPool = null) {
            list = new(capacity);
            list2 = new(capacity, this.arrayPool = arrayPool);
        }

        public MirrorList(IEnumerable<T> collection, ArrayPool<T> arrayPool = null) {
            list = new(collection);
            list2 = new(list, this.arrayPool = arrayPool);
        }

        public void Dispose() {
            list2.Dispose();
            if (arrayPool is TrackingPool<T> tp) {
                if (typeof(T).IsClass)
                    Assert.IsTrue(tp.Rented.All(a => a.All(i => i is null)));
                Assert.HasCount(0, tp.Renting);
            }
        }

        void IDisposable.Dispose() => Dispose();

        public int Capacity => list2.Capacity;
        public bool IsReadOnly => list2.IsReadOnly;

        public int Count {
            get {
                Assert.AreEqual(list.Count, list2.Count);
                return list.Count;
            }
        }

        public ReadOnlyCollection<T> AsReadOnly() {
            ReadOnlyCollection<T> c1 = list.AsReadOnly(), c2 = list2.AsReadOnly();
            CollectionAssert.AreEqual(c1, c2);
            return c1;
        }

        public T this[int index] {
            get {
                T v1 = list[index], v2 = list2[index];
                Assert.AreEqual(v1, v2);
                return v1;
            }
            set => list[index] = list2[index] = value;
        }

        public void TrimExcess() {
            list.TrimExcess();
            list2.TrimExcess();
            _ = AsReadOnly();
        }

        public void Add(T item) {
            list.Add(item);
            list2.Add(item);
            _ = AsReadOnly();
        }

        public void Insert(int startIndex, T item) {
            list.Insert(startIndex, item);
            list2.Insert(startIndex, item);
            _ = AsReadOnly();
        }

        public void AddRange(ReadOnlySpan<T> collection) {
            list.AddRange(collection);
            list2.AddRange(collection);
            _ = AsReadOnly();
        }

        public void AddRange(IEnumerable<T> collection) {
            list.AddRange(collection);
            list2.AddRange(collection);
            _ = AsReadOnly();
        }

        public void InsertRange(int startIndex, ReadOnlySpan<T> collection) {
            list.InsertRange(startIndex, collection);
            list2.InsertRange(startIndex, collection);
            _ = AsReadOnly();
        }

        public void InsertRange(int startIndex, IEnumerable<T> collection) {
            list.InsertRange(startIndex, collection);
            list2.InsertRange(startIndex, collection);
            _ = AsReadOnly();
        }

        public int BinarySearch(T item, IComparer<T> comparer = null) {
            int v1 = list.BinarySearch(item, comparer), v2 = list2.BinarySearch(item, comparer);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int BinarySearch(int startIndex, int length, T item, IComparer<T> comparer = null) {
            int v1 = list.BinarySearch(startIndex, length, item, comparer),
                v2 = list2.BinarySearch(startIndex, length, item, comparer);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public void Clear() {
            list.Clear();
            list2.Clear();
        }

        public bool Contains(T item) {
            bool v1 = list.Contains(item), v2 = list2.Contains(item);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public bool Exists(Predicate<T> match) {
            bool v1 = list.Exists(match), v2 = list2.Exists(match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int IndexOf(T item) {
            int v1 = list.IndexOf(item), v2 = list2.IndexOf(item);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int IndexOf(T item, int startIndex) {
            int v1 = list.IndexOf(item, startIndex), v2 = list2.IndexOf(item, startIndex);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int LastIndexOf(T item, int startIndex = -1) {
            int i = startIndex < 0 ? list.Count + startIndex : startIndex;
            int v1 = list.LastIndexOf(item, i), v2 = list2.LastIndexOf(item, startIndex);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public T Find(Predicate<T> match) {
            T v1 = list.Find(match), v2 = list2.Find(match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int FindIndex(Predicate<T> match) {
            int v1 = list.FindIndex(match), v2 = list2.FindIndex(match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int FindIndex(int startIndex, Predicate<T> match) {
            int v1 = list.FindIndex(startIndex, match), v2 = list2.FindIndex(startIndex, match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int FindIndex(int startIndex, int length, Predicate<T> match) {
            int v1 = list.FindIndex(startIndex, length, match), v2 = list2.FindIndex(startIndex, length, match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public T FindLast(Predicate<T> match) {
            T v1 = list.FindLast(match), v2 = list2.FindLast(match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int FindLastIndex(Predicate<T> match) {
            int v1 = list.FindLastIndex(match), v2 = list2.FindLastIndex(match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int FindLastIndex(int startIndex, Predicate<T> match) {
            int v1 = list.FindLastIndex(startIndex, match), v2 = list2.FindLastIndex(startIndex, match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int FindLastIndex(int startIndex, int length, Predicate<T> match) {
            int v1 = list.FindLastIndex(startIndex, length, match), v2 = list2.FindLastIndex(startIndex, length, match);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public int EnsureCapacity(int capacity) {
            int v1 = list.EnsureCapacity(capacity), v2 = list2.EnsureCapacity(capacity);
            return v2;
        }

        public List<T> FindAll(Predicate<T> match, int startIndex = 0, int length = -1) {
            int l = length < 0 ? list.Count + length + 1 : length;
            List<T> v1 = list.Slice(startIndex, l).FindAll(match);
            List<T> v2 = list2.FindAll(match, startIndex, length);
            CollectionAssert.AreEqual(v1, v2);
            return v1;
        }

        public RentedList<T> FindAllRented(Predicate<T> match, int startIndex = 0, int length = -1) =>
            list2.FindAllRented(match, startIndex, length);

        public void ForEach(Action<T> action, int startIndex = 0, int length = -1) =>
            list2.ForEach(action, startIndex, length);

        public void ForEach(Action<T, int> action, int startIndex = 0, int length = -1) =>
            list2.ForEach(action, startIndex, length);

        public bool TrueForAll(Predicate<T> match, int startIndex = 0, int length = -1) {
            bool v1 = list.Slice(startIndex, length < 0 ? list.Count + length + 1 : length).TrueForAll(match);
            bool v2 = list2.TrueForAll(match, startIndex, length);
            Assert.AreEqual(v1, v2);
            return v1;
        }

        public IEnumerator<T> GetEnumerator() {
            Assert.AreEqual(list.Count, list2.Count);
            IEnumerator<T> e1 = list.GetEnumerator(), e2 = list2.GetEnumerator();
            for (;;) {
                bool b1 = e1.MoveNext(), b2 = e2.MoveNext();
                Assert.AreEqual(b1, b2);
                if (!b1) break;
                T v1 = e1.Current, v2 = e2.Current;
                Assert.AreEqual(v1, v2);
                yield return v1;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public List<T> GetRange(int startIndex = 0, int length = -1) {
            List<T> v1 = list.GetRange(startIndex, length < 0 ? length + list.Count + 1 : length);
            List<T> v2 = list2.GetRange(startIndex, length);
            CollectionAssert.AreEqual(v1, v2);
            return v1;
        }

        public List<T> Slice(int startIndex = 0, int length = -1) {
            List<T> v1 = list.Slice(startIndex, length < 0 ? length + list.Count + 1 : length);
            List<T> v2 = list2.Slice(startIndex, length);
            CollectionAssert.AreEqual(v1, v2);
            return v1;
        }

        public RentedList<T> GetRangeRented(int startIndex = 0, int length = -1) {
            var x = list2.GetRangeRented(startIndex, length);
            if (length < 0) length += list.Count + 1;
            CollectionAssert.AreEqual(x.AsReadOnly(), list.GetRange(startIndex, length));
            return x;
        }

        public RentedList<T> SliceRented(int startIndex = 0, int length = -1) {
            var x = list2.SliceRented(startIndex, length);
            if (length < 0) length += list.Count + 1;
            CollectionAssert.AreEqual(x.AsReadOnly(), list.Slice(startIndex, length));
            return x;
        }

        public bool Remove(T item) {
            bool v1 = list.Remove(item), v2 = list2.Remove(item);
            Assert.AreEqual(v1, v2);
            _ = AsReadOnly();
            return v1;
        }

        public void RemoveAt(int index) {
            list.RemoveAt(index);
            list2.RemoveAt(index);
            _ = AsReadOnly();
        }

        public void RemoveRange(int startIndex = 0, int length = -1) {
            list.RemoveRange(startIndex, length < 0 ? length + list.Count + 1 : length);
            list2.RemoveRange(startIndex, length);
            _ = AsReadOnly();
        }

        public void Reverse(int startIndex = 0, int length = -1) {
            list.Reverse(startIndex, length < 0 ? length + list.Count + 1 : length);
            list2.Reverse(startIndex, length);
            _ = AsReadOnly();
        }

        public void Sort(int startIndex = 0, int length = -1, IComparer<T> comparer = null) {
            list.Sort(startIndex, length < 0 ? length + list.Count+1 : length, comparer);
            list2.Sort(startIndex, length, comparer);
            _ = AsReadOnly();
        }

        [OverloadResolutionPriority(1)]
        public void Sort(IComparer<T> comparer = null) {
            list.Sort(comparer);
            list2.Sort(comparer);
            _ = AsReadOnly();
        }

        public void CopyTo(T[] array, int arrayIndex) {
            list.CopyTo(array, arrayIndex);
            var v1 = array.ToArray();
            list2.CopyTo(array, arrayIndex);
            CollectionAssert.AreEqual(v1, array);
        }

        public T[] ToArray(int startIndex = 0, int length = -1) {
            T[] v1 = list.Slice(startIndex, length < 0 ? length + list.Count + 1 : length).ToArray();
            T[] v2 = list2.ToArray(startIndex, length);
            CollectionAssert.AreEqual(v1, v2);
            return v1;
        }
    }

    private record RefObj(int Id) : IComparable<RefObj> {
        public int CompareTo(RefObj other) =>
            ReferenceEquals(this, other) ? 0 : other is null ? 1 : Id.CompareTo(other.Id);
    }

    private readonly TrackingPool<int> tpInt = new();
    private readonly TrackingPool<RefObj> tpRefObj = new();

    [TestCleanup]
    public void TestCleanup() {
        Assert.IsEmpty(tpInt.Renting);
        Assert.IsEmpty(tpRefObj.Renting);
    } 
    
    [TestMethod]
    public void AddRange_FromCollection_InsertsCorrectly() {
        using (MirrorList<int> list = new(Enumerable.Range(1, 100), tpInt)) {
            list.EnsureCapacity(400);
            list.AddRange(Enumerable.Range(101, 100));
            list.InsertRange(100, Enumerable.Range(201, 100));
            list.AddRange(Enumerable.Range(301, 10).ToArray().AsSpan());
            list.InsertRange(50, Enumerable.Range(311, 10).ToArray().AsSpan());
            list.Insert(2, 301);
            list.Add(302);
            (list[255], list[256]) = (list[256], list[256]);
            list.Reverse();
            list.FindIndex(x => x == 256);
            list.FindIndex(1, x => x == 256);
            list.FindIndex(1, list.Count - 2, x => x == 256);
            list.FindLastIndex(x => x == 256);
            list.FindLastIndex(list.Count - 2, x => x == 256);
            list.FindLastIndex(list.Count - 2, list.Count - 2, x => x == 256);
            var array = new int[list.Count + 1];
            list.CopyTo(array, 1);
            list.Find(x => x == 256);
            list.FindLast(x => x == 256);
            list.FindAll(x => x > 256);
            _ = list.IndexOf(256);
            _ = list.IndexOf(256, 1);
            _ = list.LastIndexOf(256);
            _ = list.LastIndexOf(256, list.Count - 2);
            CollectionAssert.AreEqual(list.Slice(100, 100), list.GetRange(100, 100));
            _ = list.ToArray();
            _ = list.Contains(256);
            _ = list.Exists(x => x == 256);
            list.TrueForAll(x => x != 256);
            list.TrueForAll(x => x != 0);
            list.ForEach(_ => { });
            foreach (var _ in Enumerate(list)) ;
            list.Remove(255);
            list.RemoveAt(256);
            list.RemoveRange(256, 2);
            list.TrimExcess();
            Assert.IsGreaterThanOrEqualTo(list.Count, list.Capacity);
            list.FindAllRented(n => n % 2 == 0).Dispose();
            list.GetRangeRented(1, list.Count - 2).Dispose();
            list.SliceRented(2, list.Count - 4).Dispose();
            list.AddRange([52, 53, 54, 145, 255]);
            list.Remove(146);
            list.Remove(301);
            list.Remove(302);
            list.Sort(50, list.Count - 100);
            list.Sort();
            list.BinarySearch(256);
            list.BinarySearch(1, list.Count - 2, 256);
            int shouldBe = 1;
            list.ForEach(v => Assert.AreEqual(shouldBe++, v));
            int shouldBe2 = 0;
            list.ForEach((v, i) => {
                Assert.AreEqual(shouldBe2++, i);
                Assert.AreEqual(shouldBe2, v);
            });
            list.Clear();
        }
        Assert.HasCount(3, tpInt.Rented);
    }
    
    [TestMethod]
    public void AddRange_FromCollection_InsertsCorrectly_RefObj() {
        using (MirrorList<RefObj> list = new(Enumerable.Range(1, 100).Select(i => new RefObj(i)), tpRefObj)) {
            list.EnsureCapacity(400);
            list.AddRange(Enumerable.Range(101, 100).Select(i => new RefObj(i)));
            list.InsertRange(100, Enumerable.Range(201, 100).Select(i => new RefObj(i)));
            list.AddRange(Enumerable.Range(301, 10).Select(x => new RefObj(x)).ToArray().AsSpan());
            list.InsertRange(50, Enumerable.Range(311, 10).Select(x => new RefObj(x)).ToArray().AsSpan());
            list.Insert(2, new(301));
            list.Add(new(302));
            (list[255], list[256]) = (list[256], list[256]);
            list.Reverse();
            list.FindIndex(x => x.Id == 256);
            list.FindIndex(1, x => x.Id == 256);
            list.FindIndex(1, list.Count - 2, x => x.Id == 256);
            list.FindLastIndex(x => x.Id == 256);
            list.FindLastIndex(list.Count - 2, x => x.Id == 256);
            list.FindLastIndex(list.Count - 2, list.Count - 2, x => x.Id == 256);
            var array = new RefObj[list.Count + 1];
            list.CopyTo(array, 1);
            list.Find(x => x.Id == 256);
            list.FindLast(x => x.Id == 256);
            list.FindAll(x => x.Id > 256);
            _ = list.IndexOf(new(256));
            _ = list.IndexOf(new(256), 1);
            _ = list.LastIndexOf(new(256));
            _ = list.LastIndexOf(new(256), list.Count - 2);
            CollectionAssert.AreEqual(list.Slice(100, 100), list.GetRange(100, 100));
            _ = list.ToArray();
            _ = list.Contains(new(256));
            _ = list.Exists(x => x.Id == 256);
            list.TrueForAll(x => x.Id != 256);
            list.TrueForAll(x => x.Id != 0);
            list.ForEach(_ => { });
            foreach (var _ in Enumerate(list)) ;
            list.Remove(new(255));
            list.RemoveAt(256);
            list.RemoveRange(256, 2);
            list.TrimExcess();
            Assert.IsGreaterThanOrEqualTo(list.Count, list.Capacity);
            list.FindAllRented(n => n.Id % 2 == 0).Dispose();
            list.GetRangeRented(1, list.Count - 2).Dispose();
            list.SliceRented(2, list.Count - 4).Dispose();
            list.Sort(50, list.Count - 100);
            list.Sort();
            list.BinarySearch(new(256));
            list.BinarySearch(1, list.Count - 2, new(256));
            list.Clear();
        }
        Assert.HasCount(4, tpRefObj.Rented);
    }

    private IEnumerable<T> Enumerate<T>(IList<T> list) {
        foreach (var item in list)
            yield return item;
    }

    [TestMethod]
    public void NewWithThrowingEnumerable_ReturnsArrayToPool() {
        Assert.Throws<ApplicationException>(() => {
            using (RentedList<int> list = new(Source())) {
                Assert.Fail("Should not have made it here.");
            }
        });

        IEnumerable<int> Source() {
            yield return 1;
            throw new ApplicationException();
        }
    }

    [TestMethod]
    public void Validation_ThrowsWhenCalledWithInvalidParameters() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RentedList<int>(-1));
        Assert.Throws<ArgumentNullException>(() => new RentedList<int>(collection: null));
        using (RentedList<int> list = new()) {
            Assert.Throws<ArgumentNullException>(() => list.AddRange((IEnumerable<int>)null));
            Assert.Throws<ArgumentNullException>(() => list.InsertRange(0, (IEnumerable<int>)null));
            Assert.Throws<ArgumentNullException>(() => list.Find(null));
            Assert.Throws<ArgumentNullException>(() => list.FindLast(null));
            Assert.Throws<ArgumentNullException>(() => list.FindIndex(null));
            Assert.Throws<ArgumentNullException>(() => list.FindIndex(0, null));
            Assert.Throws<ArgumentNullException>(() => list.FindIndex(0, 0, null));
            Assert.Throws<ArgumentNullException>(() => list.FindLastIndex(null));
            Assert.Throws<ArgumentNullException>(() => list.FindLastIndex(0, null));
            Assert.Throws<ArgumentNullException>(() => list.FindLastIndex(0, 0, null));
            Assert.Throws<ArgumentNullException>(() => list.Exists(null));
            Assert.Throws<ArgumentNullException>(() => list.CopyTo(null, 0));
            Assert.Throws<ArgumentNullException>(() => list.FindAll(null));
            Assert.Throws<ArgumentNullException>(() => list.FindAllRented(null));
            Assert.Throws<ArgumentNullException>(() => list.TrueForAll(null));
            Assert.Throws<ArgumentNullException>(() => list.ForEach(null));

            Assert.Throws<ArgumentOutOfRangeException>(() => list[-1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => list[0]);
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Sort(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Sort(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Sort(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Sort(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Reverse(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Reverse(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Reverse(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Reverse(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.BinarySearch(-1, 0, 999));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.BinarySearch(1, 0, 999));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.BinarySearch(0, 1, 999));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.BinarySearch(0, -2, 999));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.CopyTo(new int[0], 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.CopyTo(new int[0], -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindAll(_ => true, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindAll(_ => true, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindAll(_ => true, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindAll(_ => true, 0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindAllRented(_ => true, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindAllRented(_ => true, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindAllRented(_ => true, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindAllRented(_ => true, 0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindIndex(-1, _ => true));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindIndex(1, _ => true));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindIndex(0, 1, _ => true));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindIndex(0, -2, _ => true));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(-1, _ => true));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(1, _ => true));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(0, 1, _ => true));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.FindLastIndex(0, -2, _ => true));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.ForEach(_ => { }, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.ForEach(_ => { }, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.ForEach(_ => { }, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.ForEach(_ => { }, 0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.GetRange(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.GetRange(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.GetRange(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.GetRange(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.GetRangeRented(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.GetRangeRented(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.GetRangeRented(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.GetRangeRented(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Slice(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Slice(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Slice(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Slice(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.SliceRented(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.SliceRented(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.SliceRented(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.SliceRented(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.ToArray(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.ToArray(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.ToArray(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.ToArray(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.IndexOf(99, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.IndexOf(99, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.LastIndexOf(99, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.LastIndexOf(99, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Insert(-(list.Count + 2), 99));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Insert(1, 99));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveRange(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveRange(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveRange(0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveRange(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.TrueForAll(_ => true, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.TrueForAll(_ => true, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.TrueForAll(_ => true, 0, -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.TrueForAll(_ => true, 0, 1));
        }
    }

    [TestMethod]
    public void Validation_ThresholdTesting() {
        using (RentedList<int> list = new(Enumerable.Range(0, 10))) {
            Assert.Throws<ArgumentOutOfRangeException>(() => list[-11]);
            Assert.AreEqual(0, list[-10]);
            Assert.AreEqual(9, list[-1]);
            Assert.AreEqual(0, list[0]);
            Assert.AreEqual(9, list[9]);
            Assert.Throws<ArgumentOutOfRangeException>(() => list[10]);
            
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Reverse(-11, 0));
            list.Reverse(0, 10);
            Assert.AreEqual(9, list[0]);
            Assert.AreEqual(0, list[9]);
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Reverse(0, 11));
            list.Reverse(10, 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Reverse(10, 1));
            list.Reverse(1, -3);
            (list[0], list[9]) = (list[9], list[0]);
            CollectionAssert.AreEqual(Enumerable.Range(0, 10).ToArray(), list.AsReadOnly());
        }
    }
}