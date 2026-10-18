using System.Net;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Client;
using Dhole.Dynamics.Contracts;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase10HttpClientTests
{
    private sealed class Token : IDholeM2mTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("test-token");
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> next)
        : HttpMessageHandler
    {
        public int Calls;
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Methods.Add(request.Method);
            return Task.FromResult(next(request, Calls));
        }
    }

    private static DholeDynamicsClient Client(Handler handler) =>
        new(new HttpClient(handler), new Token(),
            DholeDynamicsClientOptions.Create("https://connector.example/"));

    [Fact]
    public async Task UsesDirectHttpsOAuthAndDeterministicRateKeyWithoutPostRetries()
    {
        var rate = Guid.NewGuid();
        var handler = new Handler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("/api/v1/dhole/quotes", req.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", req.Headers.Authorization.Parameter);
            Assert.Equal("dhole-rate-" + rate.ToString("N"),
                req.Headers.GetValues("Idempotency-Key").Single());
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(
                    new DholeQuoteTemplateResult(rate, Guid.NewGuid(), "succeeded",
                        false, Guid.NewGuid())), Encoding.UTF8, "application/json")
            };
        });
        var result = await Client(handler).CreateConfirmedQuoteAsync("sandbox", rate,
            new Dictionary<string, JsonElement> {
                ["name"] = JsonSerializer.SerializeToElement("Demo"),
                ["customerAccountId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
                ["currencyId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
                ["freight"] = JsonSerializer.SerializeToElement<decimal?>(null)
            }, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal("succeeded", result.Status);
        Assert.Single(handler.Methods);
    }

    [Fact]
    public async Task AmbiguousPostNeverResendsAndExposesOperationId()
    {
        var op = Guid.NewGuid();
        var rate = Guid.NewGuid();
        var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(JsonSerializer.Serialize(
                new DholeQuoteTemplateResult(rate, null, "requires_reconciliation",
                    false, op)), Encoding.UTF8, "application/json")
        });
        var result = await Client(handler).CreateConfirmedQuoteAsync("sandbox", rate,
            new Dictionary<string, JsonElement> {
                ["name"] = JsonSerializer.SerializeToElement("Demo"),
                ["customerAccountId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
                ["currencyId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString())
            }, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(op, result.OperationId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetRetriesTransient429OnlyAndNeverSwitchesHost()
    {
        var handler = new Handler((req, count) =>
        {
            Assert.Equal("connector.example", req.RequestUri!.Host);
            if (count == 1) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var result = await Client(handler).FindQuoteByRateAsync("sandbox",
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Methods, x => Assert.Equal(HttpMethod.Get, x));
    }

    [Fact]
    public void RejectsInsecureHostAndInvalidConnection()
    {
        Assert.Throws<ArgumentException>(()=>DholeDynamicsClientOptions.Create("http://evil.test/").Validate());
        var client = Client(new Handler((_, _) => throw new Exception("Should never send")));
        Assert.ThrowsAsync<ArgumentException>(()=>client.FindQuoteByRateAsync("../admin",
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).GetAwaiter().GetResult();
    }
}
