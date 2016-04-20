﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Helios.Buffers;
using Helios.Concurrency;
using Helios.Logging;
using Helios.Util;

namespace Helios.Channels
{
    /// <summary>
    /// Outbound buffer used to store messages queued for outbound delivery
    /// on a given channel. These messages will be dequed and flushed to the
    /// underlying socket or transport.
    /// </summary>
    public sealed class ChannelOutboundBuffer
    {
        private static readonly ILogger Logger = LoggingFactory.GetLogger<ChannelOutboundBuffer>();

        private volatile int _unwritable;
        private long _totalPendingSize;

        private readonly int _writeBufferHighWaterMark;
        private readonly int _writeBufferLowWaterMark;

        private bool _inFail;

        /// <summary>
        /// Callback used to indicate that the channel is going to become writeable or unwriteable
        /// </summary>
        private Action _fireChannelWritabilityChanged;

        // Entry(flushedEntry) --> ... Entry(unflushedEntry) --> ... Entry(tailEntry)
        //
        // The Entry that is the first in the linked-list structure that was flushed
        private Entry _flushedEntry;
        // The Entry which is the first unflushed in the linked-list structure
        private Entry _unflushedEntry;
        // The Entry which represents the tail of the buffer
        private Entry _tailEntry;

        /// <summary>
        /// Number of flushed entries not yet written
        /// </summary>
        private int _flushed;

        public ChannelOutboundBuffer(int writeBufferHighWaterMark, int writeBufferLowWaterMark, Action fireChannelWritabilityChanged)
        {
            _writeBufferHighWaterMark = writeBufferHighWaterMark;
            _writeBufferLowWaterMark = writeBufferLowWaterMark;
            _fireChannelWritabilityChanged = fireChannelWritabilityChanged;
        }

        /// <summary>
        /// Return the current message to write or <c>null</c> if nothing was flushed before and so is ready to be written.
        /// </summary>
        public object Current
        {
            get
            {
                var entry = _flushedEntry;
                return entry?.Message;
            }
        }

        /// <summary>
        /// Returns <c>true</c> if 
        /// </summary>
        public bool IsWritable => _unwritable == 0;

        /// <summary>
        /// Returns the number of flushed messages
        /// </summary>
        public int Count => _flushed;

        /// <summary>
        /// Returns <c>true</c> if there are flushed messages in this buffer. <c>false</c> otherwise.
        /// </summary>
        public bool IsEmpty => _flushed == 0;

        /// <summary>
        /// The total number of bytes waiting to be written
        /// </summary>
        public long TotalPendingWriteBytes => Thread.VolatileRead(ref _totalPendingSize);

        /// <summary>
        /// Number of bytes we can write before we become unwriteable
        /// </summary>
        public long BytesBeforeUnwritable
        {
            get
            {
                var bytes = _writeBufferHighWaterMark - TotalPendingWriteBytes;
                if (bytes > 0)
                    return IsWritable ? bytes : 0;
                return 0;
            }
        }

        public long BytesBeforeWritable
        {
            get
            {
                var bytes = TotalPendingWriteBytes - _writeBufferLowWaterMark;
                if (bytes > 0)
                    return IsWritable ? bytes : 0;
                return 0;
            }
        }

        /// <summary>
        /// Add a given message to this <see cref="ChannelOutboundBuffer"/>.
        /// </summary>
        /// <param name="message">The message that will be written.</param>
        /// <param name="size">The size of the message.</param>
        /// <param name="promise">A <see cref="TaskCompletionSource"/> that will be set once message was written.</param>
        public void AddMessage(object message, int size, TaskCompletionSource promise)
        {
            var entry = Entry.NewInstance(message, size, Total(message), promise);
            if (_tailEntry == null)
            {
                _flushedEntry = null;
                _tailEntry = entry;
            }
            else
            {
                var tail = _tailEntry;
                tail.Next = entry;
                _tailEntry = entry;
            }

            if (_unflushedEntry == null)
            {
                _unflushedEntry = entry;
            }

            IncrementPendingOutboundBytes(size);
        }

        public bool Remove()
        {
            var e = _flushedEntry;
            if (e == null)
            {
                return false;
            }
            var msg = e.Message;
            var promise = e.Promise;
            var size = e.PendingSize;
            
            RemoveEntry(e);

            if (!e.Cancelled)
            {
                // todo: reference counting
                PromiseUtil.SafeSetSuccess(promise, Logger);
                DecrementPendingOutboundBytes(size, true);
            }

            e.Recycle();
            return true;
        }

        public bool Remove(Exception cause)
        {
            return Remove(cause, true);
        }

        private bool Remove(Exception cause, bool notifyWritability)
        {
            var e = _flushedEntry;
            if (e == null)
            {
                return false;
            }
            var msg = e.Message;
            var promise = e.Promise;
            var size = e.PendingSize;

            RemoveEntry(e);

            if (!e.Cancelled)
            {
                // todo: reference counting
                PromiseUtil.SafeSetFailure(promise, cause, Logger);
                DecrementPendingOutboundBytes(size, true);
            }

            e.Recycle();
            return true;
        }

        private void RemoveEntry(Entry e)
        {
            if (--_flushed == 0)
            {
                // processed everything
                _flushedEntry = null;
                if (e == _tailEntry)
                {
                    _tailEntry = null;
                    _unflushedEntry = null;
                }
            }
            else
            {
                _flushedEntry = e.Next;
            }
        }

        private void IncrementPendingOutboundBytes(long size)
        {
            if (size == 0)
                return;

            long newWriteBufferSize = Interlocked.Add(ref _totalPendingSize, size);
            if (newWriteBufferSize >= _writeBufferHighWaterMark)
            {
                SetUnwritable();
            }
        }

        private void DecrementPendingOutboundBytes(long size)
        {
            DecrementPendingOutboundBytes(size, true);
        }

        private void DecrementPendingOutboundBytes(long size, bool notifyWritability)
        {
            if (size == 0)
                return;
            long newWriteBufferSize = Interlocked.Add(ref _totalPendingSize, -size);
            if (notifyWritability && (newWriteBufferSize == 0 || newWriteBufferSize <= _writeBufferLowWaterMark))
            {
                SetWritable();
            }
        }

        private void SetUnwritable()
        {
            while (true)
            {
                var oldValue = _unwritable;
                var newValue = oldValue | 1;
                if (Interlocked.CompareExchange(ref _unwritable, newValue, oldValue) == oldValue)
                {
                    if (oldValue == 0 && newValue != 0)
                    {
                        _fireChannelWritabilityChanged();
                    }
                    break;
                }
            }
        }

        private void SetWritable()
        {
            while (true)
            {
                var oldValue = _unwritable;
                var newValue = oldValue & ~1;
                if (Interlocked.CompareExchange(ref _unwritable, newValue, oldValue) == oldValue)
                {
                    if (oldValue != 0 && newValue == 0)
                    {
                        _fireChannelWritabilityChanged();
                    }
                    break;
                }
            }
        }

        private static long Total(object message)
        {
            var buf = message as IByteBuf;
            if (buf != null)
                return buf.ReadableBytes;
            return -1;
        }

        internal void FailFlushed(Exception cause, bool notify)
        {
            if (_inFail)
            {
                return;
            }

            try
            {
                _inFail = true;
                while (true)
                {
                    if (!Remove(cause, notify))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _inFail = false;
            }
        }
        
        internal void Close(ClosedChannelException cause)
        {
            if (_inFail)
            {
                return;
            }

            if (!IsEmpty)
            {
                throw new InvalidOperationException("close() must be called after all flushed writes are handled.");
            }

            _inFail = true;

            try
            {
                var e = _unflushedEntry;
                while (e != null)
                {
                    // No triggering anymore events, as we are shutting down
                    if (!e.Cancelled)
                    {
                        // todo: referencing counting
                        PromiseUtil.SafeSetFailure(e.Promise, cause, Logger);
                    }
                    e = e.RecycleAndGetNext();
                }
            }
            finally
            {
                _inFail = false;
            }
        }

        /// <summary>
        /// Represents an entry inside the <see cref="ChannelOutboundBuffer"/>
        /// </summary>
        sealed class Entry
        {
            private static readonly ThreadLocal<ObjectPool<Entry>> Pool = new ThreadLocal<ObjectPool<Entry>>(() => new ObjectPool<Entry>(() => new Entry()));

            private Entry() { }

            public long Total;
            public int PendingSize;
            public object Message;
            public Entry Next; //linked list
            public TaskCompletionSource Promise;
            public bool Cancelled;

            public static Entry NewInstance(object message, int size, long total, TaskCompletionSource promise)
            {
                var entry = Pool.Value.Take();
                entry.Message = message;
                entry.PendingSize = size;
                entry.Total = total;
                entry.Promise = promise;
                return entry;
            }

            public int Cancel()
            {
                if (!Cancelled)
                {
                    Cancelled = true;
                    int pSize = PendingSize;

                    // TODO: message reference counting (optional)
                    Message = Unpooled.Empty;
                    PendingSize = 0;
                    Total = 0;
                    return pSize;
                }
                return 0;
            }

            public void Recycle()
            {
                Total = 0;
                PendingSize = 0;
                Message = null;
                Next = null;
                Promise = null;
                Cancelled = false;
                Pool.Value.Free(this);
            }

            public Entry RecycleAndGetNext()
            {
                var next = Next;
                Recycle();
                return next;
            }
        }
    }
}
