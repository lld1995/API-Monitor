using ApiMonitor.Api;

namespace ApiMonitor.Tests;
public class UnitTest1
{
    [Theory]
    [InlineData(Provider.OpenAIOfficial, "sk-valid-test-key")]
    [InlineData(Provider.Tavily, "tvly-valid-test-key")]
    public void Public_key_policy_accepts_bounded_keys(Provider provider, string key) => Assert.True(PublicCheckPolicy.IsAllowed(provider, key));

    [Fact] public void Public_key_policy_rejects_newline() => Assert.False(PublicCheckPolicy.IsAllowed(Provider.OpenAIOfficial, "sk-valid\nkey"));
    [Theory]
    [InlineData(Provider.OpenAIOfficial, "https://api.openai.com/v1", LlmProtocol.OpenAI)]
    [InlineData(Provider.Claude, "https://api.anthropic.com/v1", LlmProtocol.Anthropic)]
    [InlineData(Provider.Ark, "https://ark.cn-beijing.volces.com/api/v3", LlmProtocol.OpenAI)]
    public void Llm_provider_has_own_base_endpoint_and_protocol(Provider provider, string endpoint, LlmProtocol protocol) { var item = ProviderCatalog.Get(provider); Assert.Equal(endpoint, item.Endpoint); Assert.Equal(protocol, item.Protocol); Assert.True(item.IsLlm); }

    [Fact] public void Llm_base_endpoint_rejects_protocol_path() { Assert.True(PublicCheckPolicy.IsLlmBaseEndpoint("https://example.com/v1")); Assert.False(PublicCheckPolicy.IsLlmBaseEndpoint("https://example.com/v1/chat/completions")); Assert.False(PublicCheckPolicy.IsLlmBaseEndpoint("https://example.com/v1/messages")); }

    [Fact] public void Regular_api_has_endpoint_without_llm_protocol() { var item = ProviderCatalog.Get(Provider.Brave); Assert.Equal("https://api.search.brave.com/res/v1/web/search", item.Endpoint); Assert.Null(item.Protocol); Assert.False(item.IsLlm); }
    [Fact] public void Custom_provider_requires_endpoint_and_selectable_protocol() { var item = ProviderCatalog.Get(Provider.Custom); Assert.Null(item.Endpoint); Assert.Null(item.Protocol); Assert.True(item.IsLlm); }

    [Fact] public void Llm_base_endpoint_allows_private_http_service() { Assert.True(PublicCheckPolicy.IsLlmBaseEndpoint("http://192.168.191.4:5050/v1")); Assert.True(PublicCheckPolicy.IsSafeEndpoint("http://192.168.191.4:5050/v1/chat/completions", true)); }
    [Fact] public void Model_list_parser_reads_distinct_ids() { var models = ProviderModelService.ParseModels("{\"data\":[{\"id\":\"model-b\"},{\"id\":\"model-a\"},{\"id\":\"model-a\"}]}"); Assert.Equal(new[] { "model-a", "model-b" }, models); }
    [Fact] public void Model_list_parser_returns_empty_for_unsupported_shape() { Assert.Empty(ProviderModelService.ParseModels("{\"models\":[]}")); }
    [Theory]
    [InlineData("https://example.com/health")]
    [InlineData("http://127.0.0.1:8080/health")]
    public void Http_check_accepts_http_urls_without_credentials(string url) => Assert.True(HttpUrlChecker.IsValidUrl(url));
    [Theory]
    [InlineData("ftp://example.com/health")]
    [InlineData("https://user:pass@example.com/health")]
    [InlineData("https://example.com/health#fragment")]
    public void Http_check_rejects_unsafe_urls(string url) => Assert.False(HttpUrlChecker.IsValidUrl(url));
}
