﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.ServiceBus.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public abstract class MessageReceiver : ClientEntity, IMessageReceiver
    {
        readonly TimeSpan operationTimeout;
        readonly object messageReceivePumpSyncLock;
        int prefetchCount;
        long lastPeekedSequenceNumber;
        MessageReceivePump receivePump;
        CancellationTokenSource receivePumpCancellationTokenSource;

        protected MessageReceiver(ReceiveMode receiveMode, TimeSpan operationTimeout)
            : base(nameof(MessageReceiver) + StringUtility.GetRandomString())
        {
            this.ReceiveMode = receiveMode;
            this.operationTimeout = operationTimeout;
            this.lastPeekedSequenceNumber = Constants.DefaultLastPeekedSequenceNumber;
            this.messageReceivePumpSyncLock = new object();
        }

        public abstract string Path { get; }

        public ReceiveMode ReceiveMode { get; protected set; }

        public virtual int PrefetchCount
        {
            get
            {
                return this.prefetchCount;
            }

            set
            {
                if (value < 0)
                {
                    throw Fx.Exception.ArgumentOutOfRange(nameof(this.PrefetchCount), value, "Value must be greater than 0");
                }

                this.prefetchCount = value;
            }
        }

        public virtual long LastPeekedSequenceNumber
        {
            get
            {
                return this.lastPeekedSequenceNumber;
            }

            internal set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(this.LastPeekedSequenceNumber), value.ToString());
                }

                this.lastPeekedSequenceNumber = value;
            }
        }

        internal TimeSpan OperationTimeout
        {
            get { return this.operationTimeout; }
        }

        protected MessagingEntityType? EntityType { get; set; }

        public override Task CloseAsync()
        {
            lock (this.messageReceivePumpSyncLock)
            {
                if (this.receivePump != null)
                {
                    this.receivePumpCancellationTokenSource.Cancel();
                    this.receivePump = null;
                }
            }
            return Task.FromResult(0);
        }

        /// <summary>
        /// Asynchronously receives a message using the <see cref="MessageReceiver" />.
        /// </summary>
        /// <returns>The asynchronous operation.</returns>
        public Task<Message> ReceiveAsync()
        {
            return this.ReceiveAsync(this.OperationTimeout);
        }

        /// <summary>
        /// Asynchronously receives a message. />.
        /// </summary>
        /// <param name="serverWaitTime">The time span the server waits for receiving a message before it times out.</param>
        /// <returns>The asynchronous operation.</returns>
        public async Task<Message> ReceiveAsync(TimeSpan serverWaitTime)
        {
            IList<Message> messages = await this.ReceiveAsync(1, serverWaitTime).ConfigureAwait(false);
            if (messages != null && messages.Count > 0)
            {
                return messages[0];
            }

            return null;
        }

        /// <summary>
        /// Asynchronously receives a message using the <see cref="MessageReceiver" />.
        /// </summary>
        /// <param name="maxMessageCount">The maximum number of messages that will be received.</param>
        /// <returns>The asynchronous operation.</returns>
        public Task<IList<Message>> ReceiveAsync(int maxMessageCount)
        {
            return this.ReceiveAsync(maxMessageCount, this.OperationTimeout);
        }

        /// <summary>
        /// Asynchronously receives a message. />.
        /// </summary>
        /// <param name="maxMessageCount">The maximum number of messages that will be received.</param>
        /// <param name="serverWaitTime">The time span the server waits for receiving a message before it times out.</param>
        /// <returns>The asynchronous operation.</returns>
        public async Task<IList<Message>> ReceiveAsync(int maxMessageCount, TimeSpan serverWaitTime)
        {
            MessagingEventSource.Log.MessageReceiveStart(this.ClientId, maxMessageCount);

            IList<Message> messages;
            try
            {
                messages = await this.OnReceiveAsync(maxMessageCount, serverWaitTime).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessageReceiveException(this.ClientId, exception);
                throw;
            }

            MessagingEventSource.Log.MessageReceiveStop(this.ClientId, messages?.Count ?? 0);
            return messages;
        }

        public async Task<Message> ReceiveBySequenceNumberAsync(long sequenceNumber)
        {
            IList<Message> messages = await this.ReceiveBySequenceNumberAsync(new long[] { sequenceNumber });
            if (messages != null && messages.Count > 0)
            {
                return messages[0];
            }

            return null;
        }

        public async Task<IList<Message>> ReceiveBySequenceNumberAsync(IEnumerable<long> sequenceNumbers)
        {
            this.ThrowIfNotPeekLockMode();
            int count = MessageReceiver.ValidateSequenceNumbers(sequenceNumbers);

            MessagingEventSource.Log.MessageReceiveBySequenceNumberStart(this.ClientId, count, sequenceNumbers);

            IList<Message> messages;
            try
            {
                messages = await this.OnReceiveBySequenceNumberAsync(sequenceNumbers).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessageReceiveBySequenceNumberException(this.ClientId, exception);
                throw;
            }

            MessagingEventSource.Log.MessageReceiveBySequenceNumberStop(this.ClientId, messages?.Count ?? 0);

            return messages;
        }

        public Task CompleteAsync(Guid lockToken)
        {
            return this.CompleteAsync(new[] { lockToken });
        }

        public async Task CompleteAsync(IEnumerable<Guid> lockTokens)
        {
            this.ThrowIfNotPeekLockMode();
            int count = MessageReceiver.ValidateLockTokens(lockTokens);

            MessagingEventSource.Log.MessageCompleteStart(this.ClientId, count, lockTokens);

            try
            {
                await this.OnCompleteAsync(lockTokens).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessageCompleteException(this.ClientId, exception);
                throw;
            }

            MessagingEventSource.Log.MessageCompleteStop(this.ClientId);
        }

        public async Task AbandonAsync(Guid lockToken)
        {
            this.ThrowIfNotPeekLockMode();

            MessagingEventSource.Log.MessageAbandonStart(this.ClientId, 1, lockToken);
            try
            {
                await this.OnAbandonAsync(lockToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessageAbandonException(this.ClientId, exception);
                throw;
            }

            MessagingEventSource.Log.MessageAbandonStop(this.ClientId);
        }

        public async Task DeferAsync(Guid lockToken)
        {
            this.ThrowIfNotPeekLockMode();

            MessagingEventSource.Log.MessageDeferStart(this.ClientId, 1, lockToken);

            try
            {
                await this.OnDeferAsync(lockToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessageDeferException(this.ClientId, exception);
                throw;
            }

            MessagingEventSource.Log.MessageDeferStop(this.ClientId);
        }

        public async Task DeadLetterAsync(Guid lockToken)
        {
            this.ThrowIfNotPeekLockMode();

            MessagingEventSource.Log.MessageDeadLetterStart(this.ClientId, 1, lockToken);

            try
            {
                await this.OnDeadLetterAsync(lockToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessageDeadLetterException(this.ClientId, exception);
                throw;
            }

            MessagingEventSource.Log.MessageDeadLetterStop(this.ClientId);
        }

        public async Task<DateTime> RenewLockAsync(Guid lockToken)
        {
            this.ThrowIfNotPeekLockMode();

            MessagingEventSource.Log.MessageRenewLockStart(this.ClientId, 1, lockToken);

            DateTime lockedUntilUtc;
            try
            {
                lockedUntilUtc = await this.OnRenewLockAsync(lockToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessageRenewLockException(this.ClientId, exception);
                throw;
            }

            MessagingEventSource.Log.MessageRenewLockStop(this.ClientId);
            return lockedUntilUtc;
        }

        /// <summary>
        /// Asynchronously reads the next message without changing the state of the receiver or the message source.
        /// </summary>
        /// <returns>The asynchronous operation that returns the <see cref="Message" /> that represents the next message to be read.</returns>
        public Task<Message> PeekAsync()
        {
            return this.PeekBySequenceNumberAsync(this.lastPeekedSequenceNumber + 1);
        }

        /// <summary>
        /// Asynchronously reads the next batch of message without changing the state of the receiver or the message source.
        /// </summary>
        /// <param name="maxMessageCount">The number of messages.</param>
        /// <returns>The asynchronous operation that returns a list of <see cref="Message" /> to be read.</returns>
        public Task<IList<Message>> PeekAsync(int maxMessageCount)
        {
            return this.PeekBySequenceNumberAsync(this.lastPeekedSequenceNumber + 1, maxMessageCount);
        }

        /// <summary>
        /// Asynchronously reads the next message without changing the state of the receiver or the message source.
        /// </summary>
        /// <param name="fromSequenceNumber">The sequence number from where to read the message.</param>
        /// <returns>The asynchronous operation that returns the <see cref="Message" /> that represents the next message to be read.</returns>
        public async Task<Message> PeekBySequenceNumberAsync(long fromSequenceNumber)
        {
            var messages = await this.PeekBySequenceNumberAsync(fromSequenceNumber, 1).ConfigureAwait(false);
            return messages?.FirstOrDefault();
        }

        /// <summary>Peeks a batch of messages.</summary>
        /// <param name="fromSequenceNumber">The starting point from which to browse a batch of messages.</param>
        /// <param name="messageCount">The number of messages.</param>
        /// <returns>A batch of messages peeked.</returns>
        public async Task<IList<Message>> PeekBySequenceNumberAsync(long fromSequenceNumber, int messageCount)
        {
            IList<Message> messages;

            MessagingEventSource.Log.MessagePeekStart(this.ClientId, fromSequenceNumber, messageCount);
            try
            {
                messages = await this.OnPeekAsync(fromSequenceNumber, messageCount).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessagePeekException(this.ClientId, exception);
                throw;
            }

            MessagingEventSource.Log.MessagePeekStop(this.ClientId, messages?.Count ?? 0);
            return messages;
        }

        public void OnMessageAsync(Func<Message, CancellationToken, Task> callback)
        {
            this.OnMessageAsync(callback, new OnMessageOptions() { ReceiveTimeOut = this.OperationTimeout });
        }

        public void OnMessageAsync(Func<Message, CancellationToken, Task> callback, OnMessageOptions onMessageOptions)
        {
            onMessageOptions.ReceiveTimeOut = this.OperationTimeout;
            this.OnMessageHandlerAsync(onMessageOptions, callback).GetAwaiter().GetResult();
        }

        protected abstract Task<IList<Message>> OnReceiveAsync(int maxMessageCount, TimeSpan serverWaitTime);

        protected abstract Task<IList<Message>> OnReceiveBySequenceNumberAsync(IEnumerable<long> sequenceNumbers);

        protected abstract Task OnCompleteAsync(IEnumerable<Guid> lockTokens);

        protected abstract Task OnAbandonAsync(Guid lockToken);

        protected abstract Task OnDeferAsync(Guid lockToken);

        protected abstract Task OnDeadLetterAsync(Guid lockToken);

        protected abstract Task<DateTime> OnRenewLockAsync(Guid lockToken);

        protected abstract Task<IList<Message>> OnPeekAsync(long fromSequenceNumber, int messageCount = 1);

        static int ValidateLockTokens(IEnumerable<Guid> lockTokens)
        {
            int count;
            if (lockTokens == null || (count = lockTokens.Count()) == 0)
            {
                throw Fx.Exception.ArgumentNull(nameof(lockTokens));
            }

            return count;
        }

        static int ValidateSequenceNumbers(IEnumerable<long> sequenceNumbers)
        {
            int count;
            if (sequenceNumbers == null || (count = sequenceNumbers.Count()) == 0)
            {
                throw Fx.Exception.ArgumentNull(nameof(sequenceNumbers));
            }

            return count;
        }

        void ThrowIfNotPeekLockMode()
        {
            if (this.ReceiveMode != ReceiveMode.PeekLock)
            {
                throw Fx.Exception.AsError(new InvalidOperationException("The operation is only supported in 'PeekLock' receive mode."));
            }
        }

        async Task OnMessageHandlerAsync(
            OnMessageOptions onMessageOptions,
            Func<Message, CancellationToken, Task> callback)
        {
            MessagingEventSource.Log.RegisterOnMessageHandlerStart(this.ClientId, onMessageOptions);

            lock (this.messageReceivePumpSyncLock)
            {
                if (this.receivePump != null)
                {
                    throw new InvalidOperationException(Resources.OnMessageAlreadyCalled);
                }

                this.receivePumpCancellationTokenSource = new CancellationTokenSource();
                this.receivePump = new MessageReceivePump(this, onMessageOptions, callback, this.receivePumpCancellationTokenSource.Token);
            }

            try
            {
                await this.receivePump.StartPumpAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.RegisterOnMessageHandlerException(this.ClientId, exception);

                this.receivePumpCancellationTokenSource.Cancel();
                this.receivePumpCancellationTokenSource.Dispose();
                this.receivePump = null;
                throw;
            }

            MessagingEventSource.Log.RegisterOnMessageHandlerStop(this.ClientId);
        }
    }
}