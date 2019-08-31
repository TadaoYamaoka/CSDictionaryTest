// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;

using System;
using System.Collections.Generic;
using MyCollections;

namespace MyDictionary2
{
    public class GenericEqualityComparer<T> : IEqualityComparer<T>
    {
        private Func<T, T, bool> _predicate;
        private Func<T, int> _gethash;

        public GenericEqualityComparer(Func<T, T, bool> predicate)
            : this(predicate, obj => obj.GetHashCode())
        {
        }
        public GenericEqualityComparer(Func<T, T, bool> predicate, Func<T, int> gethash)
        {
            _predicate = predicate;
            _gethash = gethash;
        }

        public bool Equals(T x, T y)
        {
            return _predicate(x, y);
        }
        public int GetHashCode(T obj)
        {
            return _gethash(obj);
        }
    }

    // NonRandomizedStringEqualityComparer is the comparer used by default with the Dictionary<string,...> 
    // We use NonRandomizedStringEqualityComparer as default comparer as it doesnt use the randomized string hashing which 
    // keeps the performance not affected till we hit collision threshold and then we switch to the comparer which is using 
    // randomized string hashing.
    [Serializable] // Required for compatibility with .NET Core 2.0 as we exposed the NonRandomizedStringEqualityComparer inside the serialization blob
    // Needs to be public to support binary serialization compatibility
    public sealed class NonRandomizedStringEqualityComparer : EqualityComparer<string>, ISerializable
    {
        internal static new IEqualityComparer<string> Default { get; } = new NonRandomizedStringEqualityComparer();

        private NonRandomizedStringEqualityComparer() { }

        // This is used by the serialization engine.
        private NonRandomizedStringEqualityComparer(SerializationInfo information, StreamingContext context) { }

        public sealed override bool Equals(string x, string y) => string.Equals(x, y);

        public sealed override int GetHashCode(string obj) => obj?.GetHashCode() ?? 0;

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // We are doing this to stay compatible with .NET Framework.
            info.SetType(typeof(GenericEqualityComparer<string>));
        }
    }

    /// <summary>
    /// Used internally to control behavior of insertion into a <see cref="Dictionary{TKey, TValue}"/>.
    /// </summary>
    internal enum InsertionBehavior : byte
    {
        /// <summary>
        /// The default insertion behavior.
        /// </summary>
        None = 0,

        /// <summary>
        /// Specifies that an existing entry with the same key should be overwritten if encountered.
        /// </summary>
        OverwriteExisting = 1,

        /// <summary>
        /// Specifies that if an existing entry with the same key is encountered, an exception should be thrown.
        /// </summary>
        ThrowOnExisting = 2
    }

    [DebuggerDisplay("Count = {Count}")]
    [Serializable]
    public class Dictionary<TKey, TValue> : ISerializable, IDeserializationCallback
    {
        private struct Entry
        {
            public int hashCode;    // Lower 31 bits of hash code, -1 if unused
            public int next;        // Index of next entry, -1 if last
            public TKey key;           // Key of entry
            public TValue value;         // Value of entry
        }

        private int[] _buckets;
        private Entry[] _entries;
        private int _count;
        private int _freeList;
        private int _freeCount;
        private int _version;
        private IEqualityComparer<TKey> _comparer;

        // constants for serialization
        private const string VersionName = "Version"; // Do not rename (binary serialization)
        private const string HashSizeName = "HashSize"; // Do not rename (binary serialization). Must save buckets.Length
        private const string KeyValuePairsName = "KeyValuePairs"; // Do not rename (binary serialization)
        private const string ComparerName = "Comparer"; // Do not rename (binary serialization)

        public Dictionary() : this(0, null) { }

        public Dictionary(int capacity) : this(capacity, null) { }

        public Dictionary(IEqualityComparer<TKey> comparer) : this(0, comparer) { }

        public Dictionary(int capacity, IEqualityComparer<TKey> comparer)
        {
            if (capacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            if (capacity > 0) Initialize(capacity);
            if (comparer != EqualityComparer<TKey>.Default)
            {
                _comparer = comparer;
            }

            if (typeof(TKey) == typeof(string) && _comparer == null)
            {
                // To start, move off default comparer for string which is randomised
                _comparer = (IEqualityComparer<TKey>)NonRandomizedStringEqualityComparer.Default;
            }
        }

        public Dictionary(IDictionary<TKey, TValue> dictionary) : this(dictionary, null) { }

        public Dictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer) :
            this(dictionary != null ? dictionary.Count : 0, comparer)
        {
            if (dictionary == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
            }

            // It is likely that the passed-in dictionary is Dictionary<TKey,TValue>. When this is the case,
            // avoid the enumerator allocation and overhead by looping through the entries array directly.
            // We only do this when dictionary is Dictionary<TKey,TValue> and not a subclass, to maintain
            // back-compat with subclasses that may have overridden the enumerator behavior.
            if (dictionary.GetType() == typeof(Dictionary<TKey, TValue>))
            {
                Dictionary<TKey, TValue> d = (Dictionary<TKey, TValue>)dictionary;
                int count = d._count;
                Entry[] entries = d._entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries[i].hashCode >= 0)
                    {
                        Add(entries[i].key, entries[i].value);
                    }
                }
                return;
            }

            foreach (KeyValuePair<TKey, TValue> pair in dictionary)
            {
                Add(pair.Key, pair.Value);
            }
        }

        public Dictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection) : this(collection, null) { }

        public Dictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey> comparer) :
            this((collection as ICollection<KeyValuePair<TKey, TValue>>)?.Count ?? 0, comparer)
        {
            if (collection == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.collection);
            }

            foreach (KeyValuePair<TKey, TValue> pair in collection)
            {
                Add(pair.Key, pair.Value);
            }
        }

        protected Dictionary(SerializationInfo info, StreamingContext context)
        {
            // We can't do anything with the keys and values until the entire graph has been deserialized
            // and we have a resonable estimate that GetHashCode is not going to fail.  For the time being,
            // we'll just cache this.  The graph is not valid until OnDeserialization has been called.
            HashHelpers.SerializationInfoTable.Add(this, info);
        }

        public IEqualityComparer<TKey> Comparer
        {
            get
            {
                return (_comparer == null || _comparer is NonRandomizedStringEqualityComparer) ? EqualityComparer<TKey>.Default : _comparer;
            }
        }

        public int Count
        {
            get { return _count - _freeCount; }
        }


        public TValue this[TKey key]
        {
            get
            {
                int i = FindEntry(key);
                if (i >= 0) return _entries[i].value;
                ThrowHelper.ThrowKeyNotFoundException();
                return default(TValue);
            }
            set
            {
                bool modified = TryInsert(key, value, InsertionBehavior.OverwriteExisting);
                Debug.Assert(modified);
            }
        }

        public void Add(TKey key, TValue value)
        {
            bool modified = TryInsert(key, value, InsertionBehavior.ThrowOnExisting);
            Debug.Assert(modified); // If there was an existing key and the Add failed, an exception will already have been thrown.
        }

        public void Clear()
        {
            int count = _count;
            if (count > 0)
            {
                Array.Clear(_buckets, 0, _buckets.Length);

                _count = 0;
                _freeList = -1;
                _freeCount = 0;
                Array.Clear(_entries, 0, count);
            }
            _version++;
        }

        public bool ContainsKey(TKey key)
            => FindEntry(key) >= 0;

        public bool ContainsValue(TValue value)
        {
            Entry[] entries = _entries;
            if (value == null)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (entries[i].hashCode >= 0 && entries[i].value == null) return true;
                }
            }
            else
            {
                if (default(TValue) != null)
                {
                    // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                    for (int i = 0; i < _count; i++)
                    {
                        if (entries[i].hashCode >= 0 && EqualityComparer<TValue>.Default.Equals(entries[i].value, value)) return true;
                    }
                }
                else
                {
                    // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                    // https://github.com/dotnet/coreclr/issues/17273
                    // So cache in a local rather than get EqualityComparer per loop iteration
                    EqualityComparer<TValue> defaultComparer = EqualityComparer<TValue>.Default;
                    for (int i = 0; i < _count; i++)
                    {
                        if (entries[i].hashCode >= 0 && defaultComparer.Equals(entries[i].value, value)) return true;
                    }
                }
            }
            return false;
        }

        private void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            if (array == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
            }

            if ((uint)index > (uint)array.Length)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException();
            }

            if (array.Length - index < Count)
            {
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
            }

            int count = _count;
            Entry[] entries = _entries;
            for (int i = 0; i < count; i++)
            {
                if (entries[i].hashCode >= 0)
                {
                    array[index++] = new KeyValuePair<TKey, TValue>(entries[i].key, entries[i].value);
                }
            }
        }

        public virtual void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.info);
            }

            info.AddValue(VersionName, _version);
            info.AddValue(ComparerName, _comparer ?? EqualityComparer<TKey>.Default, typeof(IEqualityComparer<TKey>));
            info.AddValue(HashSizeName, _buckets == null ? 0 : _buckets.Length); // This is the length of the bucket array

            if (_buckets != null)
            {
                var array = new KeyValuePair<TKey, TValue>[Count];
                CopyTo(array, 0);
                info.AddValue(KeyValuePairsName, array, typeof(KeyValuePair<TKey, TValue>[]));
            }
        }

        private int FindEntry(TKey key)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            int i = -1;
            int[] buckets = _buckets;
            Entry[] entries = _entries;
            int collisionCount = 0;
            if (buckets != null)
            {
                IEqualityComparer<TKey> comparer = _comparer;
                if (comparer == null)
                {
                    int hashCode = key.GetHashCode() & 0x7FFFFFFF;
                    // Value in _buckets is 1-based
                    i = buckets[hashCode % buckets.Length] - 1;
                    if (default(TKey) != null)
                    {
                        // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                        do
                        {
                            // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                            // Test in if to drop range check for following array access
                            if ((uint)i >= (uint)entries.Length || (entries[i].hashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].key, key)))
                            {
                                break;
                            }

                            i = entries[i].next;
                            if (collisionCount >= entries.Length)
                            {
                                // The chain of entries forms a loop; which means a concurrent update has happened.
                                // Break out of the loop and throw, rather than looping forever.
                                ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_NoValue);
                            }
                            collisionCount++;
                        } while (true);
                    }
                    else
                    {
                        // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                        // https://github.com/dotnet/coreclr/issues/17273
                        // So cache in a local rather than get EqualityComparer per loop iteration
                        EqualityComparer<TKey> defaultComparer = EqualityComparer<TKey>.Default;
                        do
                        {
                            // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                            // Test in if to drop range check for following array access
                            if ((uint)i >= (uint)entries.Length || (entries[i].hashCode == hashCode && defaultComparer.Equals(entries[i].key, key)))
                            {
                                break;
                            }

                            i = entries[i].next;
                            if (collisionCount >= entries.Length)
                            {
                                // The chain of entries forms a loop; which means a concurrent update has happened.
                                // Break out of the loop and throw, rather than looping forever.
                                ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_NoValue);
                            }
                            collisionCount++;
                        } while (true);
                    }
                }
                else
                {
                    int hashCode = comparer.GetHashCode(key) & 0x7FFFFFFF;
                    // Value in _buckets is 1-based
                    i = buckets[hashCode % buckets.Length] - 1;
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                        // Test in if to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length ||
                            (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key)))
                        {
                            break;
                        }

                        i = entries[i].next;
                        if (collisionCount >= entries.Length)
                        {
                            // The chain of entries forms a loop; which means a concurrent update has happened.
                            // Break out of the loop and throw, rather than looping forever.
                            ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_NoValue);
                        }
                        collisionCount++;
                    } while (true);
                }
            }

            return i;
        }

        private int Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);

            _freeList = -1;
            _buckets = new int[size];
            _entries = new Entry[size];

            return size;
        }

        private bool TryInsert(TKey key, TValue value, InsertionBehavior behavior)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            _version++;
            if (_buckets == null)
            {
                Initialize(0);
            }

            Entry[] entries = _entries;
            IEqualityComparer<TKey> comparer = _comparer;

            int hashCode = ((comparer == null) ? key.GetHashCode() : comparer.GetHashCode(key)) & 0x7FFFFFFF;

            int collisionCount = 0;
            ref int bucket = ref _buckets[hashCode % _buckets.Length];
            // Value in _buckets is 1-based
            int i = bucket - 1;

            if (comparer == null)
            {
                if (default(TKey) != null)
                {
                    // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                        // Test uint in if rather than loop condition to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            break;
                        }

                        if (entries[i].hashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].key, key))
                        {
                            if (behavior == InsertionBehavior.OverwriteExisting)
                            {
                                entries[i].value = value;
                                return true;
                            }

                            if (behavior == InsertionBehavior.ThrowOnExisting)
                            {
                                ThrowHelper.ThrowArgumentException(ExceptionResource.Argument_AddingDuplicate);
                            }

                            return false;
                        }

                        i = entries[i].next;
                        if (collisionCount >= entries.Length)
                        {
                            // The chain of entries forms a loop; which means a concurrent update has happened.
                            // Break out of the loop and throw, rather than looping forever.
                            ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_NoValue);
                        }
                        collisionCount++;
                    } while (true);
                }
                else
                {
                    // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                    // https://github.com/dotnet/coreclr/issues/17273
                    // So cache in a local rather than get EqualityComparer per loop iteration
                    EqualityComparer<TKey> defaultComparer = EqualityComparer<TKey>.Default;
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                        // Test uint in if rather than loop condition to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            break;
                        }

                        if (entries[i].hashCode == hashCode && defaultComparer.Equals(entries[i].key, key))
                        {
                            if (behavior == InsertionBehavior.OverwriteExisting)
                            {
                                entries[i].value = value;
                                return true;
                            }

                            if (behavior == InsertionBehavior.ThrowOnExisting)
                            {
                                ThrowHelper.ThrowArgumentException(ExceptionResource.Argument_AddingDuplicate);
                            }

                            return false;
                        }

                        i = entries[i].next;
                        if (collisionCount >= entries.Length)
                        {
                            // The chain of entries forms a loop; which means a concurrent update has happened.
                            // Break out of the loop and throw, rather than looping forever.
                            ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_NoValue);
                        }
                        collisionCount++;
                    } while (true);
                }
            }
            else
            {
                do
                {
                    // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                    // Test uint in if rather than loop condition to drop range check for following array access
                    if ((uint)i >= (uint)entries.Length)
                    {
                        break;
                    }

                    if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                    {
                        if (behavior == InsertionBehavior.OverwriteExisting)
                        {
                            entries[i].value = value;
                            return true;
                        }

                        if (behavior == InsertionBehavior.ThrowOnExisting)
                        {
                            ThrowHelper.ThrowArgumentException(ExceptionResource.Argument_AddingDuplicate);
                        }

                        return false;
                    }

                    i = entries[i].next;
                    if (collisionCount >= entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_NoValue);
                    }
                    collisionCount++;
                } while (true);

            }

            // Can be improved with "Ref Local Reassignment"
            // https://github.com/dotnet/csharplang/blob/master/proposals/ref-local-reassignment.md
            bool resized = false;
            bool updateFreeList = false;
            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                updateFreeList = true;
                _freeCount--;
            }
            else
            {
                int count = _count;
                if (count == entries.Length)
                {
                    Resize();
                    resized = true;
                }
                index = count;
                _count = count + 1;
                entries = _entries;
            }

            ref int targetBucket = ref resized ? ref _buckets[hashCode % _buckets.Length] : ref bucket;
            ref Entry entry = ref entries[index];

            if (updateFreeList)
            {
                _freeList = entry.next;
            }
            entry.hashCode = hashCode;
            // Value in _buckets is 1-based
            entry.next = targetBucket - 1;
            entry.key = key;
            entry.value = value;
            // Value in _buckets is 1-based
            targetBucket = index + 1;

            // Value types never rehash
            if (default(TKey) == null && collisionCount > HashHelpers.HashCollisionThreshold && comparer is NonRandomizedStringEqualityComparer)
            {
                // If we hit the collision threshold we'll need to switch to the comparer which is using randomized string hashing
                // i.e. EqualityComparer<string>.Default.
                _comparer = null;
                Resize(entries.Length, true);
            }

            return true;
        }

        public virtual void OnDeserialization(object sender)
        {
            HashHelpers.SerializationInfoTable.TryGetValue(this, out SerializationInfo siInfo);

            if (siInfo == null)
            {
                // We can return immediately if this function is called twice. 
                // Note we remove the serialization info from the table at the end of this method.
                return;
            }

            int realVersion = siInfo.GetInt32(VersionName);
            int hashsize = siInfo.GetInt32(HashSizeName);
            _comparer = (IEqualityComparer<TKey>)siInfo.GetValue(ComparerName, typeof(IEqualityComparer<TKey>));

            if (hashsize != 0)
            {
                Initialize(hashsize);

                KeyValuePair<TKey, TValue>[] array = (KeyValuePair<TKey, TValue>[])
                    siInfo.GetValue(KeyValuePairsName, typeof(KeyValuePair<TKey, TValue>[]));

                if (array == null)
                {
                    ThrowHelper.ThrowSerializationException(ExceptionResource.Serialization_MissingKeys);
                }

                for (int i = 0; i < array.Length; i++)
                {
                    if (array[i].Key == null)
                    {
                        ThrowHelper.ThrowSerializationException(ExceptionResource.Serialization_NullKey);
                    }
                    Add(array[i].Key, array[i].Value);
                }
            }
            else
            {
                _buckets = null;
            }

            _version = realVersion;
            HashHelpers.SerializationInfoTable.Remove(this);
        }

        private void Resize()
            => Resize(HashHelpers.ExpandPrime(_count), false);

        private void Resize(int newSize, bool forceNewHashCodes)
        {
            // Value types never rehash
            Debug.Assert(!forceNewHashCodes || default(TKey) == null);
            Debug.Assert(newSize >= _entries.Length);

            int[] buckets = new int[newSize];
            Entry[] entries = new Entry[newSize];

            int count = _count;
            Array.Copy(_entries, 0, entries, 0, count);

            if (default(TKey) == null && forceNewHashCodes)
            {
                for (int i = 0; i < count; i++)
                {
                    if (entries[i].hashCode >= 0)
                    {
                        Debug.Assert(_comparer == null);
                        entries[i].hashCode = (entries[i].key.GetHashCode() & 0x7FFFFFFF);
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (entries[i].hashCode >= 0)
                {
                    int bucket = entries[i].hashCode % newSize;
                    // Value in _buckets is 1-based
                    entries[i].next = buckets[bucket] - 1;
                    // Value in _buckets is 1-based
                    buckets[bucket] = i + 1;
                }
            }

            _buckets = buckets;
            _entries = entries;
        }

        // The overload Remove(TKey key, out TValue value) is a copy of this method with one additional
        // statement to copy the value for entry being removed into the output parameter.
        // Code has been intentionally duplicated for performance reasons.
        public bool Remove(TKey key)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (_buckets != null)
            {
                int hashCode = (_comparer?.GetHashCode(key) ?? key.GetHashCode()) & 0x7FFFFFFF;
                int bucket = hashCode % _buckets.Length;
                int last = -1;
                // Value in _buckets is 1-based
                int i = _buckets[bucket] - 1;
                while (i >= 0)
                {
                    ref Entry entry = ref _entries[i];

                    if (entry.hashCode == hashCode && (_comparer?.Equals(entry.key, key) ?? EqualityComparer<TKey>.Default.Equals(entry.key, key)))
                    {
                        if (last < 0)
                        {
                            // Value in _buckets is 1-based
                            _buckets[bucket] = entry.next + 1;
                        }
                        else
                        {
                            _entries[last].next = entry.next;
                        }
                        entry.hashCode = -1;
                        entry.next = _freeList;

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                        {
                            entry.key = default(TKey);
                        }
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                        {
                            entry.value = default(TValue);
                        }
                        _freeList = i;
                        _freeCount++;
                        _version++;
                        return true;
                    }

                    last = i;
                    i = entry.next;
                }
            }
            return false;
        }

        // This overload is a copy of the overload Remove(TKey key) with one additional
        // statement to copy the value for entry being removed into the output parameter.
        // Code has been intentionally duplicated for performance reasons.
        public bool Remove(TKey key, out TValue value)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (_buckets != null)
            {
                int hashCode = (_comparer?.GetHashCode(key) ?? key.GetHashCode()) & 0x7FFFFFFF;
                int bucket = hashCode % _buckets.Length;
                int last = -1;
                // Value in _buckets is 1-based
                int i = _buckets[bucket] - 1;
                while (i >= 0)
                {
                    ref Entry entry = ref _entries[i];

                    if (entry.hashCode == hashCode && (_comparer?.Equals(entry.key, key) ?? EqualityComparer<TKey>.Default.Equals(entry.key, key)))
                    {
                        if (last < 0)
                        {
                            // Value in _buckets is 1-based
                            _buckets[bucket] = entry.next + 1;
                        }
                        else
                        {
                            _entries[last].next = entry.next;
                        }

                        value = entry.value;

                        entry.hashCode = -1;
                        entry.next = _freeList;

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                        {
                            entry.key = default(TKey);
                        }
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                        {
                            entry.value = default(TValue);
                        }
                        _freeList = i;
                        _freeCount++;
                        _version++;
                        return true;
                    }

                    last = i;
                    i = entry.next;
                }
            }
            value = default(TValue);
            return false;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            int i = FindEntry(key);
            if (i >= 0)
            {
                value = _entries[i].value;
                return true;
            }
            value = default(TValue);
            return false;
        }

        public bool TryAdd(TKey key, TValue value)
            => TryInsert(key, value, InsertionBehavior.None);



    }
}