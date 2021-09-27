using System;
using System.Threading.Tasks;

namespace AspNetOnCompletedMVE
{
    public sealed class AsyncDisposeDelegateWrapper : IAsyncDisposable, IDisposable
    {
        private readonly Func<ValueTask> _dlg;
        public AsyncDisposeDelegateWrapper(Func<ValueTask> dlg)
            => _dlg = dlg;

        public async ValueTask DisposeAsync()
            => await _dlg();

        public void Dispose()
            => DisposeAsync().GetAwaiter().GetResult();
    }
}
