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

namespace MyDictionary3
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
    /// Used internally to control behavior of insertion into a <see cref="Dictionary{int, double}"/>.
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
    public class Dictionary
    {
        unsafe private struct Entry
        {
            public int hashCode;    // Lower 31 bits of hash code, -1 if unused
            public Entry* next;        // Index of next entry, -1 if last
            public int key;           // Key of entry
            public double value;         // Value of entry
        }

        unsafe private Entry*[] _buckets;
        private Entry[] _entries;
        private int _count;
        unsafe private Entry* _freeList;
        private int _freeCount;
        private int _version;
        private IEqualityComparer<int> _comparer;

        // constants for serialization
        private const string VersionName = "Version"; // Do not rename (binary serialization)
        private const string HashSizeName = "HashSize"; // Do not rename (binary serialization). Must save buckets.Length
        private const string KeyValuePairsName = "KeyValuePairs"; // Do not rename (binary serialization)
        private const string ComparerName = "Comparer"; // Do not rename (binary serialization)

        public Dictionary() : this(0, null) { }

        public Dictionary(int capacity) : this(capacity, null) { }

        public Dictionary(IEqualityComparer<int> comparer) : this(0, comparer) { }

        public Dictionary(int capacity, IEqualityComparer<int> comparer)
        {
            if (capacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            if (capacity > 0) Initialize(capacity);
            if (comparer != EqualityComparer<int>.Default)
            {
                _comparer = comparer;
            }

            if (typeof(int) == typeof(string) && _comparer == null)
            {
                // To start, move off default comparer for string which is randomised
                _comparer = (IEqualityComparer<int>)NonRandomizedStringEqualityComparer.Default;
            }
        }

        protected Dictionary(SerializationInfo info, StreamingContext context)
        {
            // We can't do anything with the keys and values until the entire graph has been deserialized
            // and we have a resonable estimate that GetHashCode is not going to fail.  For the time being,
            // we'll just cache this.  The graph is not valid until OnDeserialization has been called.
            HashHelpers.SerializationInfoTable.Add(this, info);
        }

        public IEqualityComparer<int> Comparer
        {
            get
            {
                return (_comparer == null || _comparer is NonRandomizedStringEqualityComparer) ? EqualityComparer<int>.Default : _comparer;
            }
        }

        public int Count
        {
            get { return _count - _freeCount; }
        }


        unsafe public double this[int key]
        {
            get
            {
                Entry* i = FindEntry(key);
                if (i != null) return i->value;
                ThrowHelper.ThrowKeyNotFoundException();
                return default(double);
            }
            set
            {
                bool modified = TryInsert(key, value, InsertionBehavior.OverwriteExisting);
                Debug.Assert(modified);
            }
        }

        public void Add(int key, double value)
        {
            bool modified = TryInsert(key, value, InsertionBehavior.ThrowOnExisting);
            Debug.Assert(modified); // If there was an existing key and the Add failed, an exception will already have been thrown.
        }

        unsafe public void Clear()
        {
            int count = _count;
            if (count > 0)
            {
                Array.Clear(_buckets, 0, _buckets.Length);

                _count = 0;
                _freeList = null;
                _freeCount = 0;
                Array.Clear(_entries, 0, count);
            }
            _version++;
        }

        unsafe public bool ContainsKey(int key)
            => FindEntry(key) != null;

        unsafe private Entry* FindEntry(int key)
        {
            Entry* i = null;
            Entry*[] buckets = _buckets;
            Entry[] entries = _entries;
            int collisionCount = 0;
            if (buckets != null)
            {
                IEqualityComparer<int> comparer = _comparer;
                if (comparer == null)
                {
                    int hashCode = key.GetHashCode() & 0x7FFFFFFF;
                    // Value in _buckets is 1-based
                    i = buckets[hashCode % buckets.Length] - 1;
                    // ValueType: Devirtualize with EqualityComparer<double>.Default intrinsic
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                        // Test in if to drop range check for following array access
                        fixed (Entry* p_entries = entries)
                        if (i - p_entries >= entries.Length || (i->hashCode == hashCode && EqualityComparer<int>.Default.Equals(i->key, key)))
                        {
                            break;
                        }

                        i = i->next;
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
                    int hashCode = comparer.GetHashCode(key) & 0x7FFFFFFF;
                    // Value in _buckets is 1-based
                    i = buckets[hashCode % buckets.Length] - 1;
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                        // Test in if to drop range check for following array access
                        fixed (Entry* p_entries = entries)
                        if (i - p_entries >= entries.Length ||
                            (i->hashCode == hashCode && comparer.Equals(i->key, key)))
                        {
                            break;
                        }

                        i = i->next;
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

        unsafe private int Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);

            _freeList = null;
            _buckets = new Entry*[size];
            _entries = new Entry[size];

            return size;
        }

        unsafe private bool TryInsert(int key, double value, InsertionBehavior behavior)
        {
            _version++;
            if (_buckets == null)
            {
                Initialize(0);
            }

            Entry[] entries = _entries;
            IEqualityComparer<int> comparer = _comparer;

            int hashCode = ((comparer == null) ? key.GetHashCode() : comparer.GetHashCode(key)) & 0x7FFFFFFF;

            int collisionCount = 0;
            Entry* bucket = _buckets[hashCode % _buckets.Length];
            // Value in _buckets is 1-based
            Entry* i = bucket - 1;

            if (comparer == null)
            {
                // ValueType: Devirtualize with EqualityComparer<double>.Default intrinsic
                do
                {
                    // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                    // Test uint in if rather than loop condition to drop range check for following array access
                    if (i + 1 == null)
                    {
                        break;
                    }

                    if (i->hashCode == hashCode && EqualityComparer<int>.Default.Equals(i->key, key))
                    {
                        if (behavior == InsertionBehavior.OverwriteExisting)
                        {
                            i->value = value;
                            return true;
                        }

                        if (behavior == InsertionBehavior.ThrowOnExisting)
                        {
                            ThrowHelper.ThrowArgumentException(ExceptionResource.Argument_AddingDuplicate);
                        }

                        return false;
                    }

                    i = i->next;
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
                do
                {
                    // Should be a while loop https://github.com/dotnet/coreclr/issues/15476
                    // Test uint in if rather than loop condition to drop range check for following array access
                    if (i + 1 == null)
                    {
                        break;
                    }

                    if (i->hashCode == hashCode && comparer.Equals(i->key, key))
                    {
                        if (behavior == InsertionBehavior.OverwriteExisting)
                        {
                            i->value = value;
                            return true;
                        }

                        if (behavior == InsertionBehavior.ThrowOnExisting)
                        {
                            ThrowHelper.ThrowArgumentException(ExceptionResource.Argument_AddingDuplicate);
                        }

                        return false;
                    }

                    i = i->next;
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
            Entry* index;
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
                fixed (Entry* p_entries = entries)
                index = p_entries + count;
                _count = count + 1;
                entries = _entries;
            }

            Entry* targetBucket = resized ? _buckets[hashCode % _buckets.Length] : bucket;
            ref Entry entry = ref *index;

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
            if (default(int) == null && collisionCount > HashHelpers.HashCollisionThreshold && comparer is NonRandomizedStringEqualityComparer)
            {
                // If we hit the collision threshold we'll need to switch to the comparer which is using randomized string hashing
                // i.e. EqualityComparer<string>.Default.
                _comparer = null;
                Resize(entries.Length, true);
            }

            return true;
        }


        private void Resize()
            => Resize(HashHelpers.ExpandPrime(_count), false);

        unsafe private void Resize(int newSize, bool forceNewHashCodes)
        {
            // Value types never rehash
            Debug.Assert(!forceNewHashCodes || default(int) == null);
            Debug.Assert(newSize >= _entries.Length);

            Entry*[] buckets = new Entry*[newSize];
            Entry[] entries = new Entry[newSize];

            int count = _count;
            Array.Copy(_entries, 0, entries, 0, count);

            if (default(int) == null && forceNewHashCodes)
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
                    fixed (Entry* p_entries = entries)
                    buckets[bucket] = p_entries + i + 1;
                }
            }

            _buckets = buckets;
            _entries = entries;
        }


        unsafe public bool TryGetValue(int key, out double value)
        {
            Entry* i = FindEntry(key);
            if (i != null)
            {
                value = i->value;
                return true;
            }
            value = default(double);
            return false;
        }

        public bool TryAdd(int key, double value)
            => TryInsert(key, value, InsertionBehavior.None);



    }
}