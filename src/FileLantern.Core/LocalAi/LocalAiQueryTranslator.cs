using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileLantern.Core.LocalAi;

public sealed class LocalAiQuerySettings
{
    public bool Enabled { get; set; }

    public string EndpointUrl { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "qwen2.5:1.5b-instruct";

    public int TimeoutSeconds { get; set; } = 3;
}

public sealed class LocalAiQueryTranslator
{
    private static readonly Regex StructuredTokenRegex = new(@"\b(ext|size|modified|content):", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtensionRegex = new(@"^[a-z0-9][a-z0-9_\-]{0,15}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SizeRegex = new(@"^(>=|<=|>|<|=)\s*\d+(?:\.\d+)?\s*(?:b|kb|mb|gb|tb)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ModifiedRegex = new(@"^(>=|<=|>|<|=)\s*\d+\s*(?:s|m|h|d|w)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonFenceRegex = new("```(?:json)?\\s*(?<json>\\{[\\s\\S]*\\})\\s*```", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly LocalAiQuerySettings _settings;

    public LocalAiQueryTranslator(HttpClient httpClient, LocalAiQuerySettings settings)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<string?> TryTranslateAsync(string query, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmedQuery = query.Trim();
        if (StructuredTokenRegex.IsMatch(trimmedQuery))
        {
            return null;
        }

        if (!TryBuildRequestUri(_settings.EndpointUrl, out var requestUri) || !IsLocalEndpoint(requestUri))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_settings.Model))
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 1, 30)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = BuildRequestBody(trimmedQuery)
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            var assistantContent = TryExtractAssistantContent(payload);
            if (string.IsNullOrWhiteSpace(assistantContent))
            {
                return null;
            }

            if (!TryExtractStructuredQuery(assistantContent, out var structuredQuery))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(structuredQuery) ? null : structuredQuery;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Local timeout should degrade gracefully to non-AI search.
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fail closed: if local AI is unavailable or malformed, caller should use plain search.
            return null;
        }
    }

    private StringContent BuildRequestBody(string query)
    {
        var body = new
        {
            model = _settings.Model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You convert user file-search requests into JSON with keys: keywords (array of strings), ext, size, modified, content. Return ONLY JSON. Use null for unknown fields. size format: <op><value><unit> (e.g. >10mb). modified format: <op><value><unit> with s/m/h/d/w (e.g. <7d)."
                },
                new
                {
                    role = "user",
                    content = query
                }
            },
            temperature = 0
        };

        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    private static bool TryBuildRequestUri(string endpointUrl, out Uri requestUri)
    {
        requestUri = null!;

        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri))
        {
            return false;
        }

        var baseUrl = endpointUri.ToString().TrimEnd('/');
        var requestUrl = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/chat/completions"
            : $"{baseUrl}/v1/chat/completions";

        return Uri.TryCreate(requestUrl, UriKind.Absolute, out requestUri);
    }

    private static bool IsLocalEndpoint(Uri endpoint)
    {
        if (endpoint is null)
        {
            return false;
        }

        if (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(endpoint.Host, out var ipAddress))
        {
            return false;
        }

        return IPAddress.IsLoopback(ipAddress)
            || ipAddress.Equals(IPAddress.Any)
            || ipAddress.Equals(IPAddress.IPv6Any);
    }

    private static string? TryExtractAssistantContent(string rawResponse)
    {
        using var document = JsonDocument.Parse(rawResponse);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message))
        {
            return null;
        }

        if (!message.TryGetProperty("content", out var contentNode))
        {
            return null;
        }

        return contentNode.ValueKind switch
        {
            JsonValueKind.String => contentNode.GetString(),
            JsonValueKind.Array => string.Join(
                string.Empty,
                contentNode
                    .EnumerateArray()
                    .Where(part => part.TryGetProperty("type", out var typeNode)
                                   && typeNode.GetString() == "text"
                                   && part.TryGetProperty("text", out _))
                    .Select(part => part.GetProperty("text").GetString() ?? string.Empty)),
            _ => null
        };
    }

    private static bool TryExtractStructuredQuery(string assistantContent, out string? structuredQuery)
    {
        structuredQuery = null;

        var jsonPayload = ExtractJsonPayload(assistantContent);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return false;
        }

        using var document = JsonDocument.Parse(jsonPayload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var tokens = new List<string>();

        if (root.TryGetProperty("keywords", out var keywordsNode) && keywordsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var keyword in keywordsNode.EnumerateArray())
            {
                if (keyword.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                AddKeywordToken(tokens, keyword.GetString());
            }
        }

        if (root.TryGetProperty("ext", out var extNode) && extNode.ValueKind == JsonValueKind.String)
        {
            var ext = extNode.GetString()?.Trim().TrimStart('.');
            if (!string.IsNullOrWhiteSpace(ext) && ExtensionRegex.IsMatch(ext))
            {
                tokens.Add($"ext:{ext.ToLowerInvariant()}");
            }
        }

        if (root.TryGetProperty("size", out var sizeNode) && sizeNode.ValueKind == JsonValueKind.String)
        {
            var size = NormalizeCompactedFilter(sizeNode.GetString());
            if (!string.IsNullOrWhiteSpace(size) && SizeRegex.IsMatch(size))
            {
                tokens.Add($"size:{size.ToLowerInvariant()}");
            }
        }

        if (root.TryGetProperty("modified", out var modifiedNode) && modifiedNode.ValueKind == JsonValueKind.String)
        {
            var modified = NormalizeCompactedFilter(modifiedNode.GetString());
            if (!string.IsNullOrWhiteSpace(modified) && ModifiedRegex.IsMatch(modified))
            {
                tokens.Add($"modified:{modified.ToLowerInvariant()}");
            }
        }

        if (root.TryGetProperty("content", out var contentNode) && contentNode.ValueKind == JsonValueKind.String)
        {
            AddContentToken(tokens, contentNode.GetString());
        }

        if (tokens.Count == 0)
        {
            return false;
        }

        structuredQuery = string.Join(' ', tokens);
        return true;
    }

    private static string ExtractJsonPayload(string content)
    {
        var trimmed = content.Trim();

        var fenceMatch = JsonFenceRegex.Match(trimmed);
        if (fenceMatch.Success)
        {
            return fenceMatch.Groups["json"].Value;
        }

        var objectStart = trimmed.IndexOf('{');
        var objectEnd = trimmed.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart)
        {
            return trimmed.Substring(objectStart, objectEnd - objectStart + 1);
        }

        return trimmed;
    }

    private static void AddKeywordToken(ICollection<string> tokens, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        var cleaned = keyword.Trim();
        if (cleaned.Contains(':', StringComparison.Ordinal))
        {
            return;
        }

        tokens.Add(QuoteIfNeeded(cleaned));
    }

    private static void AddContentToken(ICollection<string> tokens, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var cleaned = content.Trim();
        if (cleaned.Length > 160)
        {
            cleaned = cleaned[..160];
        }

        var escaped = cleaned.Replace("\"", "");
        tokens.Add($"content:{QuoteIfNeeded(escaped)}");
    }

    private static string NormalizeCompactedFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static string QuoteIfNeeded(string value)
    {
        if (value.Any(char.IsWhiteSpace))
        {
            return $"\"{value}\"";
        }

        return value;
    }
}
