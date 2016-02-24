﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Lime.Protocol.Util;

namespace Lime.Protocol.Network.Modules
{
    /// <summary>
    /// Defines a module that resend messages that doesn't have <see cref="Event.Received"/> receipts from the destination.
    /// </summary>
    public class ResendMessagesChannelModule : IChannelModule<Message>, IChannelModule<Notification>
    {        
        private readonly int _resendMessageTryCount;
        private readonly TimeSpan _resendMessageInterval;
        private readonly bool _filterByDestination;
        private readonly ConcurrentDictionary<MessageIdDestination, SentMessage> _sentMessageDictionary;
        private readonly object _syncRoot = new object();        
        private readonly BufferBlock<SentMessage> _inputBlock;
        private readonly TransformBlock<SentMessage, SentMessage> _waitForRetryBlock;
        private readonly ActionBlock<SentMessage> _resendBlock;

        private IChannel _channel;
        private bool _unbindWhenClosed;
        private IDisposable _link;

        /// <summary>
        /// Initializes a new instance of the <see cref="ResendMessagesChannelModule"/> class.
        /// </summary>
        /// <param name="resendMessageTryCount">The resend message try count.</param>
        /// <param name="resendMessageInterval">The resend message interval.</param>
        /// <param name="filterByDestination">if set to <c>true</c> [filter by destination].</param>
        /// <exception cref="System.ArgumentOutOfRangeException"></exception>
        public ResendMessagesChannelModule(int resendMessageTryCount, TimeSpan resendMessageInterval, bool filterByDestination = false)
        {            
            if (resendMessageTryCount <= 0) throw new ArgumentOutOfRangeException(nameof(resendMessageTryCount));            
            _resendMessageTryCount = resendMessageTryCount;
            _resendMessageInterval = resendMessageInterval;
            _filterByDestination = filterByDestination;
        
            _sentMessageDictionary = new ConcurrentDictionary<MessageIdDestination, SentMessage>();            
            _inputBlock = new BufferBlock<SentMessage>();
            _waitForRetryBlock =  new TransformBlock<SentMessage, SentMessage>(
                m => WaitForRetryAsync(m),
                new ExecutionDataflowBlockOptions()
                {
                    BoundedCapacity = DataflowBlockOptions.Unbounded
                });
            _resendBlock = new ActionBlock<SentMessage>(
                ResendMessageAsync, 
                new ExecutionDataflowBlockOptions()
                {
                    MaxDegreeOfParallelism = 1
                });
            _inputBlock.LinkTo(_waitForRetryBlock);
        }

        public virtual void OnStateChanged(SessionState state)
        {
            lock (_syncRoot)
            {
                if (state == SessionState.Established)
                {
                    _link = _waitForRetryBlock.LinkTo(_resendBlock);
                }
                else if (state > SessionState.Established &&
                         IsBound &&
                         _unbindWhenClosed)
                {
                    Unbind();
                }
            }
        }

        Task<Notification> IChannelModule<Notification>.OnReceivingAsync(Notification envelope, CancellationToken cancellationToken)
        {            
            if (envelope.Event == Event.Received || envelope.Event == Event.Failed)
            {
                var key = CreateKey(envelope);
                SentMessage sentMessage;                  
                if (_sentMessageDictionary.TryRemove(key, out sentMessage))
                {                            
                    sentMessage.Dispose();                            
                }
            }

            return envelope.AsCompletedTask();
        }

        Task<Notification> IChannelModule<Notification>.OnSendingAsync(Notification envelope, CancellationToken cancellationToken)
        {
            return envelope.AsCompletedTask();
        }

        Task<Message> IChannelModule<Message>.OnReceivingAsync(Message envelope, CancellationToken cancellationToken)
        {
            return envelope.AsCompletedTask();
        }

        async Task<Message> IChannelModule<Message>.OnSendingAsync(Message envelope, CancellationToken cancellationToken)
        {
            if (envelope.Id != Guid.Empty)
            {
                var key = CreateKey(envelope);
                SentMessage sentMessage;
                if (_sentMessageDictionary.TryGetValue(key, out sentMessage))
                {
                    sentMessage.IncrementResentCount();
                }
                else
                {
                    sentMessage = new SentMessage(envelope);
                    _sentMessageDictionary.TryAdd(key, sentMessage);
                }

                if (sentMessage.ResentCount <= _resendMessageTryCount)
                {
                    await _inputBlock.SendAsync(sentMessage, cancellationToken);
                }
                else if (_sentMessageDictionary.TryRemove(key, out sentMessage))
                {
                    sentMessage.Dispose();
                }
            }

            return envelope;
        }

        public bool IsBound => _channel != null;

        public virtual void Bind(IChannel channel, bool unbindWhenClosed)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            if (channel.State > SessionState.Established) throw new ArgumentException("The channel has an invalid state");

            lock (_syncRoot)
            {                
                if (IsBound) throw new InvalidOperationException("The module is already bound to a channel. Call Unbind first.");
                _channel = channel;
                _unbindWhenClosed = unbindWhenClosed;
                _channel.MessageModules.Add(this);
                _channel.NotificationModules.Add(this);
                if (channel.State != SessionState.New)
                {
                    OnStateChanged(_channel.State);
                }
            }
        }

        public virtual void Unbind()
        {
            lock (_syncRoot)
            {
                if (!IsBound) throw new InvalidOperationException("The module is not bound to a channel.");
                _channel.MessageModules.Remove(this);
                _channel.NotificationModules.Remove(this);
                _channel = null;
                _link.Dispose();
            }
        }

        protected virtual MessageIdDestination CreateKey(Message message)
        {
            return new MessageIdDestination(
                message.Id, 
                _filterByDestination ? (message.To ?? _channel.RemoteNode).ToIdentity() : null);
        }

        protected virtual MessageIdDestination CreateKey(Notification notification)
        {
            return new MessageIdDestination(
                notification.Id, 
                _filterByDestination ? (notification.GetSender() ?? _channel.RemoteNode).ToIdentity() : null);
        }

        private async Task<SentMessage> WaitForRetryAsync(SentMessage sentMessage)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var resendDate = sentMessage.LastSentDate + _resendMessageInterval;
                if (resendDate > now)
                {
                    var waitInterval = resendDate - now;
                    await Task.Delay(waitInterval, sentMessage.CancellationToken);
                }
                return sentMessage;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Trace.TraceError("DestinationResendMessagesChannelModule.WaitForRetryAsync: {0}", ex.Message);
            }

            return null;
        }

        private async Task ResendMessageAsync(SentMessage sentMessage)
        {
            if (sentMessage != null &&
                !sentMessage.CancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _channel.SendMessageAsync(sentMessage.Message);
                }
                catch (Exception ex)
                {
                    Trace.TraceError("DestinationResendMessagesChannelModule.ResendMessageAsync: {0}", ex.Message);
                    Unbind();
                }
            }
        }

        protected sealed class MessageIdDestination
        {
            private readonly Guid _messageId;
            private readonly Identity _destination;
            
            public MessageIdDestination(Guid messageId, Identity destination = null)
            {
                _messageId = messageId;
                _destination = destination;                
            }

            public override bool Equals(object obj)
            {
                return ToString().Equals(obj.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            public override int GetHashCode()
            {
                return ToString().GetHashCode();
            }            

            public override string ToString()
            {
                return _destination == null ? 
                    _messageId.ToString() : 
                    $"{_messageId}:{_destination}";
            }            
        }

        private sealed class SentMessage : IDisposable
        {
            const string RESENT_COUNT_KEY = "#resentCount";

            private readonly Message _message;            
            private readonly CancellationTokenSource _cts;

            public SentMessage(Message message)
                : this(message, 1)
            {

            }

            private SentMessage(Message message, int resentCount)
            {
                if (message == null) throw new ArgumentNullException(nameof(Message));
                _message = message;                
                ResentCount = resentCount;
                LastSentDate = DateTimeOffset.UtcNow;
                _cts = new CancellationTokenSource();
            }

            public Message Message
            {
                get
                {
                    if (_message.Metadata == null) _message.Metadata = new Dictionary<string, string>();
                    _message.Metadata.Remove(RESENT_COUNT_KEY);
                    _message.Metadata.Add(RESENT_COUNT_KEY, ResentCount.ToString());
                    return _message;
                }
            }            

            public int ResentCount { get; private set; }

            public DateTimeOffset LastSentDate { get; private set; }

            public CancellationToken CancellationToken => _cts.Token;

            public void IncrementResentCount()
            {
                ResentCount++;
                LastSentDate = DateTimeOffset.UtcNow;
            }

            public void Dispose()
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }    
    }
}