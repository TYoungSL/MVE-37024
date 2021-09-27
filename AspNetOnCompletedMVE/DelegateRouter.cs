using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AspNetOnCompletedMVE
{
    public sealed class DelegateRouter : INamedRouter
    {
        public DelegateRouter(RequestDelegate handler)
            => Handler = handler;

        public string Name => nameof(DelegateRouter);

        public RequestDelegate Handler { get; }

        public Task RouteAsync(RouteContext context)
        {
            context.Handler = Handler;
            return Task.CompletedTask;
        }

        public VirtualPathData? GetVirtualPath(VirtualPathContext context)
            => null;
    }
}
