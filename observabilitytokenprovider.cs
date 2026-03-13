using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace TaxAgent.Level3.Api.Services;

public sealed class ObservabilityTokenProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ObservabilityTokenProvider> _logger;

    public ObservabilityTokenProvider(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<ObservabilityTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GetObservabilityTokenAsync(string agentId, string tenantId)
    {
        var cacheKey = $"a365::{agentId}::{tenantId}";
        if (_cache.TryGetValue(cacheKey, out string? cachedToken) && !string.IsNullOrWhiteSpace(cachedToken))
        {
            return cachedToken;
        }

        var endpoint = _configuration["Agent365:TokenEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("Agent365:TokenEndpoint is missing.");
        }

        var client = _httpClientFactory.CreateClient("a365-token");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                agentId,
                tenantId
            })
        };

        _logger.LogInformation(
            "Resolving Agent365 token for agentId={AgentId}, tenantId={TenantId}",
            agentId,
            tenantId);

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>()
            ?? throw new InvalidOperationException("Empty Agent365 token response.");

        _cache.Set(cacheKey, payload.AccessToken, TimeSpan.FromMinutes(50));

        return payload.AccessToken;
    }

    private sealed class TokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
    }
}
