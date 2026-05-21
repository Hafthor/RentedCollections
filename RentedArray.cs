using System.Buffers;
using System.Collections;

namespace com.hafthor.RentedCollections;

public class RentedArray<T> : IList<T>, IDisposable {
    private T[] values;
    private readonly ArrayPool<T> arrayPool;
    public RentedArray(int length, ArrayPool<T> arrayPool = null) => 
        values = (this.arrayPool = arrayPool ?? ArrayPool<T>.Shared).Rent(length);

    public T[] AsArray() => values;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(T item) => ((IList<T>)values).Add(item);
    public void Clear() => ((IList<T>)values).Clear();
    public bool Contains(T item) => ((IList<T>)values).Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => ((IList<T>)values).CopyTo(array, arrayIndex);
    public bool Remove(T item) => ((IList<T>)values).Remove(item);
    public int Count => ((ICollection<T>)values).Count;
    public int Length => values.Length;
    public bool IsReadOnly => ((ICollection<T>)values).IsReadOnly;
    public int IndexOf(T item) => ((IList<T>)values).IndexOf(item);
    public void Insert(int index, T item) => ((IList<T>)values).Insert(index, item);
    public void RemoveAt(int index) => ((IList<T>)values).RemoveAt(index);
    public T this[int index] {
        get => values[index];
        set => values[index] = value;
    }
    public void Dispose() {
        if (values is not null) {
            arrayPool.Return(values, typeof(T).IsClass);
            values = null;
        }
    }
}

[TestClass]
public class RentedArrayTests {
    private class TrackingPool<T> : ArrayPool<T> {
        public List<T[]> Renting { get; } = [];

        public override T[] Rent(int minimumLength) {
            var a = new T[minimumLength];
            Renting.Add(a);
            return a;
        }

        public override void Return(T[] array, bool clearArray = false) {
            Assert.IsTrue(Renting.Remove(array));
            if (typeof(T).IsClass) Assert.IsTrue(clearArray);
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
    public void Validation_ThrowsWhenCalledWithInvalidParameters() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RentedArray<int>(-1));
        using (RentedArray<int> array = new(10, tpInt)) {
            Assert.AreEqual(10, array.Length);
            for (int i = 0; i < 10; i++) array[i] = i;
            CollectionAssert.AreEqual(Enumerable.Range(0, 10).ToArray(), array.AsArray());
            
            Assert.Throws<NotSupportedException>(() => array.Add(0));
            Assert.Throws<NotSupportedException>(() => array.Insert(0, 0));
            Assert.Throws<NotSupportedException>(() => array.Remove(0));
            Assert.Throws<NotSupportedException>(() => array.RemoveAt(0));

            Assert.Throws<ArgumentNullException>(() => array.CopyTo(null, 0));

            Assert.Throws<ArgumentException>(() => array.CopyTo(new int[10], 1));
            Assert.Throws<ArgumentException>(() => array.CopyTo(new int[10], -1));
            Assert.Throws<IndexOutOfRangeException>(() => array[-1]);
            Assert.Throws<IndexOutOfRangeException>(() => array[10]);
        }

        using (RentedArray<RefObj> array = new(10, tpRefObj)) {
            for (int i = 0; i < 10; i++) array[i] = new RefObj(i);
            CollectionAssert.AreEqual(Enumerable.Range(0, 10).Select(v => new RefObj(v)).ToArray(), array.AsArray());
        }
    }
}