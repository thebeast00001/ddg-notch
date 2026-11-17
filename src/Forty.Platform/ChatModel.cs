using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Forty.Core;

namespace Forty.Platform;

/// <summary>
/// A model behind any service that speaks the chat-completions format (F140): Sarvam, a local
/// runtime such as Ollama or LM Studio, or another hosted service. One request, one reply, no
/// streaming: the router's answers are a line of JSON. The key is asked for at the moment of each call
/// and goes only into the request's headers; it is never logged, stored here or put in an error.
/// </summary>
public sealed class ChatCompletionsModel : IChatModel
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };   // each call sets its own limit

    readonly string _baseUrl, _model;
    readonly Func<string?> _key;
    readonly bool _sarvam;

    public string Name { get; }
    public bool OnThisPc { get; }

    public ChatCompletionsModel(string name, string baseUrl, string model, Func<string?> key, bool sarvam, bool onThisPc)
    {
        Name = name;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _key = key;
        _sarvam = sarvam;
        OnThisPc = onThisPc;
    }

    public async Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancel)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _model);
            writer.WriteNumber("temperature", 0);   // the same request should be understood the same way each time
            writer.WriteNumber("max_tokens", options.MaxTokens);
            if (options.Json) { writer.WriteStartObject("response_format"); writer.WriteString("type", "json_object"); writer.WriteEndObject(); }
            writer.WriteStartArray("messages");
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions") { Content = new ByteArrayContent(body.ToArray()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (_key() is { Length: > 0 } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (_sarvam) request.Headers.Add("api-subscription-key", key);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        long started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await Http.SendAsync(request, limit.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ModelException(ModelTrouble.Refused, Name);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) throw new ModelException(ModelTrouble.Busy, Name);
            if (!response.IsSuccessStatusCode) throw new ModelException(ModelTrouble.Unreadable, Name);
            using var json = JsonDocument.Parse(text);
            string? content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw new ModelException(ModelTrouble.Unreadable, Name);
            return new ChatReply(content, Stopwatch.GetElapsedTime(started).TotalSeconds, Name);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new ModelException(ModelTrouble.TooSlow, Name); }
        catch (HttpRequestException) { throw new ModelException(ModelTrouble.Unreachable, Name); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { throw new ModelException(ModelTrouble.Unreadable, Name); }
    }
}

/// <summary>
/// Which model does which job. Agents ask for a role ("the router"); this maps the role to a provider,
/// so swapping providers never touches an agent (F140). Order: a service you name yourself with
/// FORTY_LLM_URL, FORTY_LLM_MODEL and (if it needs one) FORTY_LLM_KEY, which can be a model on this PC;
/// then Sarvam, if its key is stored. With neither, there is no model and the island says so.
/// </summary>
public static class ModelCatalog
{
    public const string SarvamKey = "SARVAM_API_KEY", OwnKey = "FORTY_LLM_KEY";

    public static IChatModel? Router(SecretStore keys)
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && v.Trim() != "your-key-here" ? v.Trim() : null;

        if (Env("FORTY_LLM_URL") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            bool local = address.IsLoopback;
            string model = Env("FORTY_LLM_MODEL") ?? "default";
            return new ChatCompletionsModel(local ? "A model on this PC" : address.Host, url, model, () => Env(OwnKey) ?? keys.Get(OwnKey), sarvam: false, onThisPc: local);
        }
        if (keys.Has(SarvamKey) || Env(SarvamKey) != null)
            return new ChatCompletionsModel("Sarvam", "https://api.sarvam.ai/v1", "sarvam-105b-conversations", () => keys.Get(SarvamKey) ?? Env(SarvamKey), sarvam: true, onThisPc: false);
        return null;
    }
}
