using System.ComponentModel.DataAnnotations;

namespace Helio.Application;

/// <summary>Tunable retrieval and generation settings, bound from configuration and validated at start-up.</summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Chunks passed to the generator. 4 was the measured sweet spot: 2 missed comparison questions, 8 doubled input tokens for no accuracy gain.</summary>
    [Range(1, 20)]
    public int TopK { get; init; } = 4;

    /// <summary>Retrieve more than TopK before reranking so the fused ranking has candidates to reorder.</summary>
    [Range(1, 100)]
    public int CandidateK { get; init; } = 12;

    /// <summary>Drop retrieved chunks whose fused score is below this fraction of the best score. Guards against answering from noise.</summary>
    [Range(0, 1)]
    public double MinRelativeScore { get; init; } = 0.25;

    /// <summary>Maximum question length accepted by the API.</summary>
    [Range(8, 4000)]
    public int MaxQuestionChars { get; init; } = 1000;

    /// <summary>Prompt template identifier recorded on every answer so prompt changes are auditable.</summary>
    [Required]
    public string PromptVersion { get; init; } = "helio-grounded-v4";

    /// <summary>Cache validated answers for identical questions for this long. Zero disables.</summary>
    public TimeSpan AnswerCacheTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Upper bound on embedding plus generation time for one question. A hung model call becomes an abstention, not a stuck request.</summary>
    public TimeSpan GenerationTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Maximum golden cases accepted by the HTTP evaluate endpoint. Larger sets belong to the offline CLI.</summary>
    [Range(1, 500)]
    public int MaxEvaluationCasesPerRequest { get; init; } = 50;
}
