using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Runtime;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace AspNetOnCompletedMVE
{
    public class Tests
    {
        [Conditional("DEBUG")]
        static void Log(string msg) => TestContext.WriteLine(msg);

        [Conditional("DEBUG")]
        static void Log(string fmt, params object[] args) => TestContext.WriteLine(fmt, args);

        [Test]
        //[Timeout(2000)]
        [Theory]
        [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
        [NonParallelizable]
        public async Task Example(
            [Range(1, 3)] int run,
            [Values(
                "2.0",
#if NET6_0_OR_GREATER
                "3.0",
#endif
                "1.1"
            )]
            string httpVersion,
            [Values("Development", "Production")] string env
        )
        {
            Log($"HTTP/{httpVersion}, Env: {env}");

            var testTimeoutMs = 2000;
            var testCts = Debugger.IsAttached ? new() : new CancellationTokenSource(testTimeoutMs);
            var testCt = testCts.Token;

#if !NET6_0_OR_GREATER
            if (httpVersion == "3.0") Assert.Inconclusive(".NET 6 is required for HTTP/3 support.");
#endif

            var whb = WebHost.CreateDefaultBuilder();
            whb.UseEnvironment(env);
            whb.SuppressStatusMessages(true);
            whb.UseSockets();
            whb.UseKestrel();
            whb.ConfigureKestrel(k => {
                k.ListenAnyIP(0, l => {
#if NET6_0_OR_GREATER
                    l.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
#else
                    l.Protocols = HttpProtocols.Http1AndHttp2;
#endif
                    l.UseHttps(s => {
                        s.SslProtocols = SslProtocols.Tls13;
                    });
                });
            });
            whb.ConfigureLogging(l => {
                l.ClearProviders();
            });
            whb.UseShutdownTimeout(TimeSpan.FromMilliseconds(5));

            var onCompletedCalled = 0;
            var asyncDisposeCalled = 0;

            var svc = new DelegateRouter(async context => {
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(testCt, context.RequestAborted);
                var linkedCt = linkedCts.Token;
                var rsp = context.Response;
                rsp.OnCompleted(() => {
                    Log("OnCompleted callback");
                    Interlocked.Increment(ref onCompletedCalled);
                    return Task.CompletedTask;
                });
                rsp.RegisterForDispose(new AsyncDisposeDelegateWrapper(() => {
                    Log("RegisterForDispose callback");
                    Interlocked.Increment(ref asyncDisposeCalled);
                    return ValueTask.CompletedTask;
                }));

                rsp.StatusCode = 200;

                await rsp.StartAsync(linkedCt);

                await rsp.Body.WriteAsync(new byte[] { 4, 3, 2, 1 }, linkedCt);
                Log("writing response response");

                await rsp.CompleteAsync();
                Log("completed response");
            });

            whb.Configure(ab => {
                ab.UseRouter(svc);
            });

            using var wh = whb.Build();
            try
            {

                using (var started = new SemaphoreSlim(0, 1))
                {
                    await Task.Factory.StartNew(async () => {
                        Log("starting WebHost");
                        await wh.StartAsync(testCt);
                        started.Release();
                        Log("waiting for WebHost to shut down");
                        await wh.WaitForShutdownAsync(testCt);
                    }, testCt);

                    await started.WaitAsync(testCt);
                    Log("saw that WebHost started");
                }

                var saf = wh.ServerFeatures.Get<IServerAddressesFeature>()!;

                saf.Should().NotBeNull();
                saf.Addresses.Count.Should().NotBe(0);

                var uri = saf.Addresses.First(u => u.StartsWith("https:"));
                if (uri.StartsWith("https://[::]"))
                    uri = "https://[::1]" + uri.Substring(12);
                else if (uri.StartsWith("https://0.0.0.0"))
                    uri = "https://localhost" + uri.Substring(15);
                else if (uri.StartsWith("https://+"))
                    uri = "https://localhost" + uri.Substring(9);

                Log($"URI: {uri}");

                if (httpVersion == "3.0")
                    AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http3Support", true);

#if NET6_0_OR_GREATER
                var clientHttpVersion = httpVersion == "1.1" ? HttpVersion.Version11
                    : httpVersion == "2.0" ? HttpVersion.Version20
                    : httpVersion == "3.0" ? HttpVersion.Version30
                    : throw new NotSupportedException();
#else
                var clientHttpVersion = httpVersion == "1.1" ? HttpVersion.Version11
                    : httpVersion == "2.0" ? HttpVersion.Version20
                    : throw new NotSupportedException();
#endif

                // NOTE: probably should verify cert; verify local machine dev cert or create a temp cert
                using (var handler = new SocketsHttpHandler
                    { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } })
                using (var client = new HttpClient(handler)
                {
                    BaseAddress = new(uri),
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    DefaultRequestVersion = clientHttpVersion
                })
                {
                    var content = new CustomContent();

                    Log("adding writer for first bit of content");
                    content.AddWriter((stream, _, ct) => stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct));

                    HttpRequestMessage req = new(HttpMethod.Post, "/Example")
                    {
                        Headers = { { "Transfer-Encoding", "chunked" } },
                        Content = content,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Version = clientHttpVersion
                    };

                    /*
                    if (httpVersion == "1.1")
                    {
                        req.Headers.Add("Connection", "close");
                        req.Headers.ConnectionClose.Should().BeTrue();
                    }
                    */

                    req.Headers.TransferEncodingChunked.Should().BeTrue();

                    Log("adding writer for second bit of content");
                    content.AddWriter((stream, _, ct) => stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct));

                    var sendTask = client.SendAsync(req, testCt);
                    
                    using var delayedWrites = new SemaphoreSlim(0, 1);
                    
                    await Task.Factory.StartNew(async () => {

                        await Task.Delay(1, testCt);

                        Log("adding writer for third bit of content");
                        content.AddWriter((stream, _, ct) => stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct));

                        await Task.Delay(1, testCt);

                        Log("adding writer for fourth bit of content");
                        content.AddWriter((stream, _, ct) => stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct));

                        await Task.Delay(1, testCt);

                        Log("completing content");
                        content.CompleteAdding();

                        delayedWrites.Release();
                    }, testCt);

                    Log("awaiting response");
                    var response = await sendTask;
                    Log("response received");

                    await delayedWrites.WaitAsync(testCt);

                    response.EnsureSuccessStatusCode();

                    Log("checking for RegisterForDispose callback called");
                    while (Interlocked.CompareExchange(ref asyncDisposeCalled, 0, 0) == 0 && !testCt.IsCancellationRequested)
                        await Task.Delay(1, testCt);
                    Log("checking for OnCompleted callback called");
                    while (Interlocked.CompareExchange(ref onCompletedCalled, 0, 0) == 0 && !testCt.IsCancellationRequested)
                        await Task.Delay(1, testCt);

                    Log("end of test body");
                    asyncDisposeCalled.Should().Be(1);
                    onCompletedCalled.Should().Be(1);
                }
            }
            finally
            {

                Log("stopping WebHost");
                await wh.StopAsync(testCt);

                try
                {
                    await wh.WaitForShutdownAsync(testCt);
                }
                catch (OperationCanceledException)
                {
                    // timeout
                }
                Log("WebHost stopped");

                testCts.Cancel();
                Log("end of test finally");
            }
        }
    }
}
