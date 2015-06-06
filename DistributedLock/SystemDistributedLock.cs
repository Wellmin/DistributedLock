﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Threading
{
    // TODO abandonment: wait for shorter times and reconstitute the event at that point

    public sealed class SystemDistributedLock : IDistributedLock
    {
        private const string GlobalPrefix = @"Global\";

        private readonly string lockName;

        public SystemDistributedLock(string lockName)
        {
            // note that just Global\ is not a valid name
            if (string.IsNullOrEmpty(lockName))
                throw new ArgumentNullException("lockName is required");
            if (lockName.Length > MaxLockNameLength)
                throw new FormatException("lockName: must be at most " + MaxLockNameLength + " characters");
            // from http://stackoverflow.com/questions/18392175/net-system-wide-eventwaithandle-name-allowed-characters
            if (lockName.IndexOf('\\') >= 0)
                throw new FormatException(@"lockName: must not contain '\'");

            this.lockName = GlobalPrefix + lockName;
        }

        #region ---- Public API ----
        public static int MaxLockNameLength { get { return 260 - GlobalPrefix.Length; } }

        public IDisposable TryAcquire(TimeSpan timeout = default(TimeSpan), CancellationToken cancellationToken = default(CancellationToken))
        {
            var timeoutMillis = timeout.ToInt32Timeout(); 

            var @event = this.CreateEvent();
            var cleanup = true;
            try
            {
                // cancellation case
                if (cancellationToken.CanBeCanceled)
                {
                    // cancellable wait based on
                    // http://www.thomaslevesque.com/2015/06/04/async-and-cancellation-support-for-wait-handles/
                    var index = WaitHandle.WaitAny(new[] { @event, cancellationToken.WaitHandle }, timeoutMillis);
                    switch (index)
                    {
                        case WaitHandle.WaitTimeout: // timeout
                            @event.Dispose();
                            return null;
                        case 0: // event
                            cleanup = false;
                            return new EventScope(@event);
                        default: // canceled
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new InvalidOperationException("Should never get here");
                    }
                }

                // normal case
                if (@event.WaitOne(timeoutMillis))
                {
                    cleanup = false;
                    return new EventScope(@event);
                }

                return null;
            }
            catch
            {
                // just in case we fail to create a scope or something
                cleanup = true;
                throw;
            }
            finally
            {
                if (cleanup)
                {
                    @event.Dispose();
                }
            }
        }

        public IDisposable Acquire(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return DistributedLockHelpers.Acquire(this, timeout, cancellationToken);
        }

        public Task<IDisposable> TryAcquireAsync(TimeSpan timeout = default(TimeSpan), CancellationToken cancellationToken = default(CancellationToken))
        {
            timeout.ToInt32Timeout(); // validate

            return this.InternalTryAcquireAsync(timeout, cancellationToken);
        }

        public Task<IDisposable> AcquireAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return DistributedLockHelpers.AcquireAsync(this, timeout, cancellationToken);
        }
        #endregion

        private async Task<IDisposable> InternalTryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var @event = this.CreateEvent();
            var cleanup = true;
            try
            {
                if (await @event.WaitOneAsync(timeout, cancellationToken).ConfigureAwait(false))
                {
                    cleanup = false;
                    return new EventScope(@event);
                }

                return null;
            }
            catch
            {
                // just in case we fail to create a scope or something
                cleanup = true;
                throw;
            }
            finally
            {
                if (cleanup)
                {
                    @event.Dispose();
                }
            }
        }

        private EventWaitHandle CreateEvent()
        {
            // based on http://stackoverflow.com/questions/2590334/creating-a-cross-process-eventwaithandle
            var security = new EventWaitHandleSecurity();
            // allow anyone to wait on and signal this lock
            security.AddAccessRule(
                new EventWaitHandleAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null), 
                    EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, 
                    AccessControlType.Allow
                )
            );

            bool ignored;
            var @event = new EventWaitHandle(
                // if we create, start as unlocked
                initialState: true,
                // allow only one thread to hold the lock
                mode: EventResetMode.AutoReset,
                name: this.lockName,
                createdNew: out ignored,
                eventSecurity: security
            );

            return @event;
        }

        private sealed class EventScope : IDisposable
        {
            private EventWaitHandle @event;

            public EventScope(EventWaitHandle @event) 
            {
                this.@event = @event;
            }

            void IDisposable.Dispose()
            {
                var @event = Interlocked.Exchange(ref this.@event, null);
                if (@event != null)
                {
                    @event.Set(); // signal
                    @event.Dispose();
                }
            }
        }
    }
}