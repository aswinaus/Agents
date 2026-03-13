using System.Text.Json;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using TaxAgent.Level3.Api.Contracts;
using TaxAgent.Level3.Api.Prompts;
using TaxAgent.Level4.Evaluation.Dtos;
using TaxAgent.Level4.Evaluation.Interfaces;
using TaxAgent.Platform.Contracts.Dtos;

namespace TaxAgent.Level3.Api.Services;

public sealed class Level3WorkflowRunner(
    IAgentStepRunner stepRunner,
    IArtifactStore artifactStore,
    IWorkflowStateStore stateStore,
    IHumanReviewStore humanReviewStore,
    IMemoryService memoryService,
    IDocxMemoBuilder docxMemoBuilder,
    IEvaluationStore evaluationStore) : ILevel3WorkflowRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<WorkflowRunResult> RunAsync(KnowledgeInput input, CancellationToken cancellationToken)
    {
        using var baggage = new BaggageBuilder()
            .AgentId("tax-level3-agent")
            .TenantId(input.TenantId)
            .ConversationId(input.ConversationId)
            .CorrelationId(input.CorrelationId)
            .Build();

        var runId = Guid.NewGuid().ToString("N");
        var state = new CaseState
        {
            CaseId = input.CaseId,
            TenantId = input.TenantId,
            Status = "Running"
        };
        await stateStore.SaveAsync(state, cancellationToken);

        var recalledMemory = await memoryService.RecallAsync(new MemoryQuery
        {
            TenantId = input.TenantId,
            ClientId = input.ClientId,
            CaseId = input.CaseId,
            Topic = input.Title
        }, cancellationToken);

        var inputEnvelope = new
        {
            Source = input,
            Memory = recalledMemory
        };

        var inputArtifactPath = await artifactStore.SaveJsonAsync($"runs/{runId}/input.json", inputEnvelope, cancellationToken);

        var advisorText = await stepRunner.RunAgentAsync("tax_advisor", JsonSerializer.Serialize(inputEnvelope, JsonOptions), cancellationToken);
        var advisorArtifactPath = await artifactStore.SaveTextAsync($"runs/{runId}/advisor-output.json", advisorText, cancellationToken);
        var advisorDecision = ParseJson<AdvisorDecision>(advisorText, "tax_advisor");

        if (advisorDecision.Action.Equals("NeedMoreInfo", StringComparison.OrdinalIgnoreCase))
        {
            state.Status = "NeedMoreInfo";
            state.NeedMoreInfoCount++;
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            await stateStore.SaveAsync(state, cancellationToken);

            var reviewTaskId = await humanReviewStore.CreateNeedMoreInfoTaskAsync(input, advisorDecision, cancellationToken);
            await SaveEvaluationStubAsync(input, runId, inputArtifactPath, advisorArtifactPath, null, null, null, state, cancellationToken);

            return new WorkflowRunResult
            {
                CaseId = input.CaseId,
                RunId = runId,
                Status = "NeedMoreInfo",
                AdvisorArtifactPath = advisorArtifactPath,
                ReviewTaskId = reviewTaskId
            };
        }

        var deliverableInput = new
        {
            Source = input,
            Advisor = advisorDecision,
            Memory = recalledMemory
        };

        var deliverableText = await stepRunner.RunAgentAsync("tax_deliverable", JsonSerializer.Serialize(deliverableInput, JsonOptions), cancellationToken);
        var deliverableArtifactPath = await artifactStore.SaveTextAsync($"runs/{runId}/deliverable-output.json", deliverableText, cancellationToken);
        var deliverableMemo = ParseJson<DeliverableMemo>(deliverableText, "tax_deliverable");

        var reviewInput = new
        {
            Source = input,
            Memo = deliverableMemo,
            Advisor = advisorDecision,
            Memory = recalledMemory
        };

        var reviewText = await stepRunner.RunAgentAsync("tax_reviewer", JsonSerializer.Serialize(reviewInput, JsonOptions), cancellationToken);
        var reviewArtifactPath = await artifactStore.SaveTextAsync($"runs/{runId}/review-output.json", reviewText, cancellationToken);
        var reviewDecision = ParseJson<ReviewDecision>(reviewText, "tax_reviewer");

        string? docxPath = null;
        string? reviewTaskPath = null;

        if (reviewDecision.Decision.Equals("Approved", StringComparison.OrdinalIgnoreCase))
        {
            docxPath = await docxMemoBuilder.BuildAsync(input.CaseId, deliverableMemo, cancellationToken);
            state.Status = "Approved";
            state.FinalDocxPath = docxPath;
            await memoryService.WriteApprovedLearningAsync(new MemoryWriteRequest
            {
                TenantId = input.TenantId,
                ClientId = input.ClientId,
                CaseId = input.CaseId,
                Category = "ApprovedMemoPattern",
                Title = input.Title,
                Content = deliverableMemo.ExecutiveSummary,
                Source = "Level3Workflow",
                Metadata = new Dictionary<string, string>
                {
                    ["runId"] = runId
                }
            }, cancellationToken);
        }
        else if (reviewDecision.Decision.Equals("NeedMoreInfo", StringComparison.OrdinalIgnoreCase))
        {
            state.Status = "NeedMoreInfo";
            state.NeedMoreInfoCount++;
            reviewTaskPath = await humanReviewStore.CreateReviewTaskAsync(input, deliverableMemo, reviewDecision, cancellationToken);
        }
        else
        {
            state.Status = "Rejected";
            state.RevisionCount++;
            reviewTaskPath = await humanReviewStore.CreateEscalationTaskAsync(input, deliverableMemo, reviewDecision, cancellationToken);
        }

        state.CurrentMemoJsonPath = deliverableArtifactPath;
        state.CurrentReviewJsonPath = reviewArtifactPath;
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        await stateStore.SaveAsync(state, cancellationToken);

        await SaveEvaluationStubAsync(input, runId, inputArtifactPath, advisorArtifactPath, deliverableArtifactPath, reviewArtifactPath, docxPath, state, cancellationToken);

        return new WorkflowRunResult
        {
            CaseId = input.CaseId,
            RunId = runId,
            Status = state.Status,
            AdvisorArtifactPath = advisorArtifactPath,
            DeliverableArtifactPath = deliverableArtifactPath,
            ReviewArtifactPath = reviewArtifactPath,
            MemoDocxPath = docxPath,
            ReviewTaskId = reviewTaskPath
        };
    }

    private async Task SaveEvaluationStubAsync(
        KnowledgeInput input,
        string runId,
        string inputArtifactPath,
        string advisorArtifactPath,
        string? deliverableArtifactPath,
        string? reviewArtifactPath,
        string? docxPath,
        CaseState state,
        CancellationToken cancellationToken)
    {
        var evaluation = new RunEvaluation
        {
            CaseId = input.CaseId,
            RunId = runId,
            WorkflowVersion = "level3-v1",
            AdvisorPromptVersion = PromptCatalog.AdvisorPromptVersion,
            DeliverablePromptVersion = PromptCatalog.DeliverablePromptVersion,
            ReviewerPromptVersion = PromptCatalog.ReviewerPromptVersion,
            RoutingPolicyVersion = "routing-v1",
            MemoryPolicyVersion = "memory-v1",
            ReviewDecision = state.Status,
            HumanOutcome = "Pending",
            InputArtifactPath = inputArtifactPath,
            AdvisorArtifactPath = advisorArtifactPath,
            DeliverableArtifactPath = deliverableArtifactPath ?? string.Empty,
            ReviewArtifactPath = reviewArtifactPath ?? string.Empty,
            FinalDocxPath = docxPath,
            RevisionCount = state.RevisionCount,
            NeedMoreInfoCount = state.NeedMoreInfoCount
        };

        await evaluationStore.SaveAsync(evaluation, cancellationToken);
    }

    private static T ParseJson<T>(string raw, string actor)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(raw, JsonOptions);
            if (parsed is null)
            {
                throw new InvalidOperationException($"{actor} returned empty JSON.");
            }
            return parsed;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{actor} returned invalid JSON: {raw}", ex);
        }
    }
}
