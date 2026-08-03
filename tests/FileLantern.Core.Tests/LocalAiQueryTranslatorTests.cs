using System.Net;
using System.Net.Http;
using System.Text;
using FileLantern.Core.LocalAi;
using Xunit;

namespace FileLantern.Core.Tests;

public sealed class LocalAiQueryTranslatorTests
{
    [Fact]
    public async Task TryTranslateAsync_ConvertsNaturalLanguageIntoStructuredQuery()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("localhost", request.RequestUri?.Host);
            Assert.NotNull(request.RequestUri);
            Assert.EndsWith("/v1/chat/completions", request.RequestUri!.AbsolutePath);

            const string responseJson = """
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"keywords\":[\"invoice\"],\"ext\":\"pdf\",\"modified\":\"<180d\",\"content\":\"quarterly revenue\"}"
                      }
                    }
                  ]
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var settings = new LocalAiQuerySettings
        {
            Enabled = true,
            EndpointUrl = "http://localhost:11434",
            Model = "qwen2.5:1.5b-instruct",
            TimeoutSeconds = 3
        };

        var translator = new LocalAiQueryTranslator(client, settings);

        var translated = await translator.TryTranslateAsync("the invoice pdf from last spring about quarterly revenue");

        Assert.NotNull(translated);
        Assert.Contains("invoice", translated, StringComparison.Ordinal);
        Assert.Contains("ext:pdf", translated, StringComparison.Ordinal);
        Assert.Contains("modified:<180d", translated, StringComparison.Ordinal);
        Assert.Contains("content:\"quarterly revenue\"", translated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryTranslateAsync_FallsBackToOriginalQueryWhenEndpointFails()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));

        using var client = new HttpClient(handler);
        var settings = new LocalAiQuerySettings
        {
            Enabled = true,
            EndpointUrl = "http://localhost:11434",
            Model = "qwen2.5:1.5b-instruct",
            TimeoutSeconds = 1
        };

        var translator = new LocalAiQueryTranslator(client, settings);
        const string originalQuery = "invoice from last spring";

        var translated = await translator.TryTranslateAsync(originalQuery);
        var effectiveQuery = translated ?? originalQuery;

        Assert.Equal(originalQuery, effectiveQuery);
    }

    [Fact]
    public async Task TryTranslateAsync_DoesNotCallNonLocalEndpoint()
    {
        var called = false;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new HttpClient(handler);
        var settings = new LocalAiQuerySettings
        {
            Enabled = true,
            EndpointUrl = "https://api.openai.com",
            Model = "irrelevant",
            TimeoutSeconds = 1
        };

        var translator = new LocalAiQueryTranslator(client, settings);

        var translated = await translator.TryTranslateAsync("find my invoice from spring");

        Assert.Null(translated);
        Assert.False(called);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request, cancellationToken));
    }
}
