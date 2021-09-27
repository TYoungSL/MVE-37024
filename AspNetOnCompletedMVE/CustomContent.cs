using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetOnCompletedMVE
{
    public delegate ValueTask WriteHttpMessageContentAsyncDelegate(Stream stream, TransportContext? context,
        CancellationToken cancellationToken);

    public sealed class CustomContent : HttpContent
    {
        private readonly BlockingCollection<WriteHttpMessageContentAsyncDelegate> _writers = new();

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => SerializeToStreamAsync(stream, context, cancellationToken).GetAwaiter().GetResult();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, default);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            do
            {
                while (_writers.TryTake(out var writer))
                    await writer(stream, context, cancellationToken);

                await Task.Delay(1, cancellationToken);
            } while (!_writers.IsCompleted);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        public void AddWriter(WriteHttpMessageContentAsyncDelegate dlg)
            => _writers.Add(dlg);

        public void CompleteAdding()
            => _writers.CompleteAdding();
    }
}
