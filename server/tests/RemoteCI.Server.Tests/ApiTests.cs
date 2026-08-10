using System.Net;
using System.Net.Http.Json;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ApiTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ApiTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Pair_WithCorrectCode_ReturnsToken()
    {
        var response = await _client.PostAsJsonAsync("/api/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "watch",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PairResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.Token));
        Assert.Equal("watch", body.Role);
    }

    [Fact]
    public async Task Pair_WithWrongCode_Returns409()
    {
        var response = await _client.PostAsJsonAsync("/api/pair", new PairRequest
        {
            PairCode = "wrong",
            Role = "watch",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Pair_WithInvalidRole_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "hacker",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task State_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task State_WithToken_ReturnsNotFoundWhenEmpty()
    {
        var pair = await PairAsync("watch");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Authorization = new("Bearer", pair.Token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<PairResponse> PairAsync(string role)
    {
        var response = await _client.PostAsJsonAsync("/api/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = role,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairResponse>())!;
    }
}
