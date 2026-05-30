using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using SimpleZipDrive.Services;

namespace SimpleZipDrive.Tests;

public class StatsServiceTests
{
    [Fact]
    public void Constructor_CreatesInstance()
    {
        using var service = new StatsService();
        Assert.NotNull(service);
    }

    [Fact]
    public async Task ReportStatsAsync_Disposed_ReturnsWithoutCall()
    {
        var handler = new TestHandler();
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        service.Dispose();
        await service.ReportStatsAsync();

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ReportStatsAsync_CancellationToken_CancelsBeforeCall()
    {
        var handler = new TestHandler { Delay = TimeSpan.FromMilliseconds(500) };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReportStatsAsync(cts.Token));

        service.Dispose();
    }

    [Fact]
    public async Task ReportStatsAsync_SendsCorrectMethodAndHeaders()
    {
        var handler = new TestHandler();
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        await service.ReportStatsAsync();

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.NotNull(handler.LastAuthHeader);
        Assert.Equal("hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e",
            handler.LastAuthHeader!.Parameter);
        Assert.Equal("application/json", handler.LastContent?.Headers.ContentType?.MediaType);

        service.Dispose();
    }

    [Fact]
    public async Task ReportStatsAsync_SuccessResponse_Completes()
    {
        var handler = new TestHandler
        {
            StatusCode = HttpStatusCode.OK,
            ResponseContent = "{}"
        };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        var exception = await Record.ExceptionAsync(() => service.ReportStatsAsync());

        Assert.Null(exception);
        Assert.Equal(1, handler.CallCount);

        service.Dispose();
    }

    [Fact]
    public async Task ReportStatsAsync_TooManyRequests_CompletesSilently()
    {
        var handler = new TestHandler
        {
            StatusCode = HttpStatusCode.TooManyRequests
        };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        var exception = await Record.ExceptionAsync(() => service.ReportStatsAsync());

        Assert.Null(exception);
        Assert.Equal(1, handler.CallCount);

        service.Dispose();
    }

    [Fact]
    public async Task ReportStatsAsync_ServerError_CompletesSilently()
    {
        var handler = new TestHandler
        {
            StatusCode = HttpStatusCode.InternalServerError
        };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        var exception = await Record.ExceptionAsync(() => service.ReportStatsAsync());

        Assert.Null(exception);
        Assert.Equal(1, handler.CallCount);

        service.Dispose();
    }

    [Fact]
    public async Task ReportStatsAsync_NetworkError_CompletesWithoutThrow()
    {
        var handler = new TestHandler { ThrowException = true };
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        var exception = await Record.ExceptionAsync(() => service.ReportStatsAsync());

        Assert.Null(exception);
        Assert.Equal(1, handler.CallCount);

        service.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var service = new StatsService();
        service.Dispose();
        var exception = Record.Exception(service.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public async Task ReportStatsAsync_AfterDispose_DoesNotCallHandler()
    {
        var handler = new TestHandler();
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        service.Dispose();
        await service.ReportStatsAsync();
        await service.ReportStatsAsync();

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ReportStatsAsync_IncludesApplicationId()
    {
        var handler = new TestHandler();
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var service = CreateStatsServiceWithClient(client);

        await service.ReportStatsAsync();

        Assert.NotNull(handler.LastContentBody);
        Assert.Contains("SimpleZipDrive", handler.LastContentBody);
        Assert.Contains("applicationId", handler.LastContentBody);
        Assert.Contains("version", handler.LastContentBody);

        service.Dispose();
    }

    private static StatsService CreateStatsServiceWithClient(HttpClient client)
    {
        var service = new StatsService();
        var clientField = typeof(StatsService).GetField("_httpClient",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (clientField != null)
        {
            var oldClient = clientField.GetValue(service) as HttpClient;
            oldClient?.Dispose();
            clientField.SetValue(service, client);
        }

        return service;
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public AuthenticationHeaderValue? LastAuthHeader { get; private set; }
        public HttpContent? LastContent { get; private set; }
        public string? LastContentBody { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseContent { get; set; } = "{}";
        public bool ThrowException { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastMethod = request.Method;
            LastAuthHeader = request.Headers.Authorization;
            LastContent = request.Content;

            if (request.Content != null)
            {
                LastContentBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (Delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(Delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Propagate cancellation
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (ThrowException)
            {
                throw new HttpRequestException("Simulated network failure");
            }

            return new HttpResponseMessage
            {
                StatusCode = StatusCode,
                Content = new StringContent(ResponseContent)
            };
        }
    }
}
