using Helio.Application.Answering;
using Helio.Domain;
using Microsoft.Extensions.AI;

namespace Helio.Infrastructure.Ai;

/// <summary>
/// Production generator over any <see cref="IChatClient"/> (Azure OpenAI in production, but the
/// abstraction also accepts Semantic Kernel or Microsoft Agent Framework clients). Temperature is
/// pinned low and the JSON reply format is requested so the parser sees well-formed output.
/// </summary>
public sealed class ChatClientAnswerGenerator(IChatClient chatClient, string modelVersion, int maxOutputTokens = 600) : IAnswerGenerator
{
    public string ProviderName => "azure-openai";

    public string ModelVersion { get; } = modelVersion;

    public Task<GenerationResult> GenerateAsync(string question, IReadOnlyList<RetrievedChunk> context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(context);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, PromptBuilder.SystemInstruction),
            new(ChatRole.User, PromptBuilder.BuildUserMessage(question, context)),
        };
        return CompleteAsync(messages, maxOutputTokens, cancellationToken);
    }

    /// <summary>
    /// A real repair, not a verbatim retry: at temperature 0.1 the same prompt reproduces the same malformed
    /// or truncated output. The model is shown its previous reply, told to return only the JSON object, and
    /// given twice the output budget, which covers the dominant failure (an answer cut off mid-object).
    /// </summary>
    public Task<GenerationResult> RepairAsync(string question, IReadOnlyList<RetrievedChunk> context, string previousRawText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(context);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, PromptBuilder.SystemInstruction),
            new(ChatRole.User, PromptBuilder.BuildUserMessage(question, context)),
            new(ChatRole.Assistant, previousRawText ?? string.Empty),
            new(ChatRole.User, "Your previous reply was not a single valid JSON object of the required shape. Reply again with only the JSON object, keeping the answer concise so it is not cut off."),
        };
        return CompleteAsync(messages, maxOutputTokens * 2, cancellationToken);
    }

    private async Task<GenerationResult> CompleteAsync(List<ChatMessage> messages, int outputBudget, CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            Temperature = 0.1f,
            MaxOutputTokens = outputBudget,
            ResponseFormat = ChatResponseFormat.Json,
        };

        var response = await chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var raw = response.Text ?? string.Empty;
        var draft = DraftParser.TryParse(raw, out var error);
        if (draft is null && response.FinishReason == ChatFinishReason.Length)
        {
            error = "output_truncated";
        }

        var usage = response.Usage;
        var inputTokens = (int)(usage?.InputTokenCount ?? EstimateTokens(messages));
        var outputTokens = (int)(usage?.OutputTokenCount ?? raw.Length / 4);
        return new GenerationResult(raw, draft, inputTokens, outputTokens, error);
    }

    private static long EstimateTokens(IEnumerable<ChatMessage> messages) => messages.Sum(m => (m.Text?.Length ?? 0) / 4);
}

/// <summary>Production embedder over any <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, batching requests to the provider's limits.</summary>
public sealed class EmbeddingGeneratorEmbedder(IEmbeddingGenerator<string, Embedding<float>> generator, string modelVersion, int dimensions, int batchSize = 64) : ITextEmbedder
{
    public string ModelVersion { get; } = modelVersion;

    public int Dimensions { get; } = dimensions;

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var result = new List<ReadOnlyMemory<float>>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += batchSize)
        {
            var batch = texts.Skip(offset).Take(batchSize).ToArray();
            var embeddings = await generator.GenerateAsync(batch, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var embedding in embeddings)
            {
                result.Add(embedding.Vector);
            }
        }

        return result;
    }
}
