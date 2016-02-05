﻿using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lime.Protocol.Listeners
{
    public static class ProducerConsumer
    {
        /// <summary>
        /// Creates and starts a long running task that listens the provided producer and call the consumer until the cancellation is requested.
        /// The cancellation occurs silently.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="producer">The producer func.</param>
        /// <param name="consumer">The consumer func.</param>
        /// <param name="cancellationToken">The cancellation token to stop the listener. It must be a valid token.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">
        /// </exception>
        /// <exception cref="System.ArgumentException">A valid cancellation token must be provided</exception>
        public static Task CreateAsync<T>(Func<CancellationToken, Task<T>> producer, Func<T, Task<bool>> consumer, CancellationToken cancellationToken)
        {
            if (producer == null) throw new ArgumentNullException(nameof(producer));
            if (consumer == null) throw new ArgumentNullException(nameof(consumer));            
            if (cancellationToken == CancellationToken.None) throw new ArgumentException("A valid cancellation token must be provided", nameof(cancellationToken));
            return Task.Factory.StartNew(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var item = await producer(cancellationToken);
                        if (!await consumer(item)) break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            },            
            TaskCreationOptions.LongRunning)
            .Unwrap();
        }
    }
}