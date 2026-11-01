using System.Diagnostics.Metrics;
using Helio.Domain;

namespace Helio.Api;

/// <summary>Domain metrics exported through OpenTelemetry. These are the numbers worth alerting on, not just HTTP counts.</summary>
public sealed class HelioMetrics
{
    public const string MeterName = "Helio";

    private readonly Counter<long> _answers;
    private readonly Counter<long> _abstentions;
    private readonly Histogram<double> _latency;
    private readonly Histogram<long> _inputTokens;
    private readonly Counter<long> _costMicroUsd;
    private readonly Counter<long> _ingests;

    public HelioMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _answers = meter.CreateCounter<long>("helio.answers", description: "Answers served, by outcome.");
        _abstentions = meter.CreateCounter<long>("helio.abstentions", description: "Abstentions, by reason.");
        _latency = meter.CreateHistogram<double>("helio.ask.duration", unit: "ms", description: "End-to-end ask latency.");
        _inputTokens = meter.CreateHistogram<long>("helio.ask.input_tokens", description: "Prompt tokens per ask.");
        _costMicroUsd = meter.CreateCounter<long>("helio.cost.micro_usd", description: "Estimated model spend in micro-dollars.");
        _ingests = meter.CreateCounter<long>("helio.ingests", description: "Documents ingested, by result.");
    }

    public void RecordAsk(Answer answer, int totalMs, int inputTokens, long costMicroUsd, bool fromCache)
    {
        var outcome = fromCache ? "cached" : answer.Abstained ? "abstained" : "answered";
        _answers.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (answer.Abstained)
        {
            _abstentions.Add(1, new KeyValuePair<string, object?>("reason", answer.Reason ?? "unknown"));
        }

        _latency.Record(totalMs);
        if (!fromCache)
        {
            // A cache hit spent no tokens; counting its original request's usage again would inflate the spend alert.
            _inputTokens.Record(inputTokens);
            _costMicroUsd.Add(costMicroUsd);
        }
    }

    public void RecordIngest(bool unchanged) => _ingests.Add(1, new KeyValuePair<string, object?>("result", unchanged ? "unchanged" : "indexed"));
}
