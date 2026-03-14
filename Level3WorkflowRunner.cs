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
    private const string Level3AgentId = "tax-level3-agent";
    private const string Level3AgentName = "Tax Level 3 Agent";
    private const string WorkflowSourceName = "TaxLevel3Workflow";
    private const string CopilotSourceName = "CopilotStudio";

    private const string TaxAdvisorAgentId = "tax_advisor";
    private const string TaxAdvisorAgentName = "Tax Advisor";

    private const string TaxDeliverableAgentId = "tax_deliverable";
    private const string TaxDeliverableAgentName = "Tax Deliverable";

    private const string TaxReviewerAgentId = "tax_reviewer";
    private const string TaxReviewerAgentName = "Tax Reviewer";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<WorkflowRunResult> RunAsync(KnowledgeInput input, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");

        using var baggage = new BaggageBuilder()
            .AgentId(Level3AgentId)
            .TenantId(input.TenantId)
            .ConversationId(input.ConversationId)
            .CorrelationId(input.CorrelationId)
            .Build();

        using var rootScope = StartAgentScope(
            agentId: Level3AgentId,
            agentName: Level3AgentName,
            tenantId: input.TenantId,
            conversationId: input.ConversationId,
            requestContent: input.Title,
            endpoint: "/api/level3/run",
            sourceName: CopilotSourceName);

        var state = new CaseState
        {
            CaseId = input.CaseId,
            TenantId = input.TenantId,
            Status = "Running",
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        await stateStore.SaveAsync(state, cancellationToken);

        var recalledMemory = await RecallMemoryAsync(input, cancellationToken);

        var inputEnvelope = new
        {
            Source = input,
            Memory = recalledMemory
        };

        var inputArtifactPath = await artifactStore.SaveJsonAsync(
            $"runs/{runId}/input.json",
            inputEnvelope,
            cancellationToken);

        var advisorDecision = await RunAdvisorAsync(
            input,
            runId,
            inputEnvelope,
            cancellationToken);

        if (advisorDecision.Decision.Action.Equals("NeedMoreInfo", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleAdvisorNeedMoreInfoAsync(
                input,
                runId,
                state,
                inputArtifactPath,
                advisorDecision.ArtifactPath,
                advisorDecision.Decision,
                cancellationToken);
        }

        var deliverableResult = await RunDeliverableAsync(
            input,
            runId,
            advisorDecision.Decision,
            recalledMemory,
            cancellationToken);

        var reviewResult = await RunReviewerAsync(
            input,
            runId,
            advisorDecision.Decision,
            deliverableResult.Memo,
            recalledMemory,
            cancellationToken);

        string? docxPath = null;
        string? reviewTaskPath = null;

        switch (reviewResult.Decision.Decision)
        {
            case var decision when decision.Equals("Approved", StringComparison.OrdinalIgnoreCase):
                docxPath = await BuildDocxAsync(input.CaseId, deliverableResult.Memo, cancellationToken);

                state.Status = "Approved";
                state.FinalDocxPath = docxPath;
                state.UpdatedUtc = DateTimeOffset.UtcNow;

                await WriteApprovedLearningAsync(input, runId, deliverableResult.Memo, cancellationToken);
                break;

            case var decision when decision.Equals("NeedMoreInfo", StringComparison.OrdinalIgnoreCase):
                state.Status = "NeedMoreInfo";
                state.NeedMoreInfoCount++;
                state.UpdatedUtc = DateTimeOffset.UtcNow;

                reviewTaskPath = await humanReviewStore.CreateReviewTaskAsync(
                    input,
                    deliverableResult.Memo,
                    reviewResult.Decision,
                    cancellationToken);
                break;

            default:
                state.Status = "Rejected";
                state.RevisionCount++;
                state.UpdatedUtc = DateTimeOffset.UtcNow;

                reviewTaskPath = await humanReviewStore.CreateEscalationTaskAsync(
                    input,
                    deliverableResult.Memo,
                    reviewResult.Decision,
                    cancellationToken);
                break;
        }

        state.CurrentMemoJsonPath = deliverableResult.ArtifactPath;
        state.CurrentReviewJsonPath = reviewResult.ArtifactPath;

        await stateStore.SaveAsync(state, cancellationToken);

        await SaveEvaluationStubAsync(
            input,
            runId,
            inputArtifactPath,
            advisorDecision.ArtifactPath,
            deliverableResult.ArtifactPath,
            reviewResult.ArtifactPath,
            docxPath,
            state,
            cancellationToken);

        return new WorkflowRunResult
        {
            CaseId = input.CaseId,
            RunId = runId,
            Status = state.Status,
            AdvisorArtifactPath = advisorDecision.ArtifactPath,
            DeliverableArtifactPath = deliverableResult.ArtifactPath,
            ReviewArtifactPath = reviewResult.ArtifactPath,
            MemoDocxPath = docxPath,
            ReviewTaskId = reviewTaskPath
        };
    }

    private async Task<object?> RecallMemoryAsync(KnowledgeInput input, CancellationToken cancellationToken)
    {
        return await memoryService.RecallAsync(new MemoryQuery
        {
            TenantId = input.TenantId,
            ClientId = input.ClientId,
            CaseId = input.CaseId,
            Topic = input.Title
        }, cancellationToken);
    }

    private async Task<(AdvisorDecision Decision, string ArtifactPath)> RunAdvisorAsync(
        KnowledgeInput input,
        string runId,
        object inputEnvelope,
        CancellationToken cancellationToken)
    {
        var advisorInputJson = JsonSerializer.Serialize(inputEnvelope, JsonOptions);

        using var advisorScope = StartAgentScope(
            agentId: TaxAdvisorAgentId,
            agentName: TaxAdvisorAgentName,
            tenantId: input.TenantId,
            conversationId: input.ConversationId,
            requestContent: advisorInputJson,
            endpoint: $"internal://{TaxAdvisorAgentId}",
            sourceName: WorkflowSourceName);

        var advisorText = await stepRunner.RunAgentAsync(
            TaxAdvisorAgentId,
            advisorInputJson,
            cancellationToken);

        var advisorArtifactPath = await artifactStore.SaveTextAsync(
            $"runs/{runId}/advisor-output.json",
            advisorText,
            cancellationToken);

        var advisorDecision = ParseJson<AdvisorDecision>(advisorText, TaxAdvisorAgentId);

        return (advisorDecision, advisorArtifactPath);
    }

    private async Task<(DeliverableMemo Memo, string ArtifactPath)> RunDeliverableAsync(
        KnowledgeInput input,
        string runId,
        AdvisorDecision advisorDecision,
        object? recalledMemory,
        CancellationToken cancellationToken)
    {
        var deliverableInput = new
        {
            Source = input,
            Advisor = advisorDecision,
            Memory = recalledMemory
        };

        var deliverableInputJson = JsonSerializer.Serialize(deliverableInput, JsonOptions);

        using var deliverableScope = StartAgentScope(
            agentId: TaxDeliverableAgentId,
            agentName: TaxDeliverableAgentName,
            tenantId: input.TenantId,
            conversationId: input.ConversationId,
            requestContent: deliverableInputJson,
            endpoint: $"internal://{TaxDeliverableAgentId}",
            sourceName: WorkflowSourceName);

        var deliverableText = await stepRunner.RunAgentAsync(
            TaxDeliverableAgentId,
            deliverableInputJson,
            cancellationToken);

        var deliverableArtifactPath = await artifactStore.SaveTextAsync(
            $"runs/{runId}/deliverable-output.json",
            deliverableText,
            cancellationToken);

        var deliverableMemo = ParseJson<DeliverableMemo>(deliverableText, TaxDeliverableAgentId);

        return (deliverableMemo, deliverableArtifactPath);
    }

    private async Task<(ReviewDecision Decision, string ArtifactPath)> RunReviewerAsync(
        KnowledgeInput input,
        string runId,
        AdvisorDecision advisorDecision,
        DeliverableMemo deliverableMemo,
        object? recalledMemory,
        CancellationToken cancellationToken)
    {
        var reviewInput = new
        {
            Source = input,
            Memo = deliverableMemo,
            Advisor = advisorDecision,
            Memory = recalledMemory
        };

        var reviewInputJson = JsonSerializer.Serialize(reviewInput, JsonOptions);

        using var reviewerScope = StartAgentScope(
            agentId: TaxReviewerAgentId,
            agentName: TaxReviewerAgentName,
            tenantId: input.TenantId,
            conversationId: input.ConversationId,
            requestContent: reviewInputJson,
            endpoint: $"internal://{TaxReviewerAgentId}",
            sourceName: WorkflowSourceName);

        var reviewText = await stepRunner.RunAgentAsync(
            TaxReviewerAgentId,
            reviewInputJson,
            cancellationToken);

        var reviewArtifactPath = await artifactStore.SaveTextAsync(
            $"runs/{runId}/review-output.json",
            reviewText,
            cancellationToken);

        var reviewDecision = ParseJson<ReviewDecision>(reviewText, TaxReviewerAgentId);

        return (reviewDecision, reviewArtifactPath);
    }

    private async Task<WorkflowRunResult> HandleAdvisorNeedMoreInfoAsync(
        KnowledgeInput input,
        string runId,
        CaseState state,
        string inputArtifactPath,
        string advisorArtifactPath,
        AdvisorDecision advisorDecision,
        CancellationToken cancellationToken)
    {
        state.Status = "NeedMoreInfo";
        state.NeedMoreInfoCount++;
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await stateStore.SaveAsync(state, cancellationToken);

        var reviewTaskId = await humanReviewStore.CreateNeedMoreInfoTaskAsync(
            input,
            advisorDecision,
            cancellationToken);

        await SaveEvaluationStubAsync(
            input,
            runId,
            inputArtifactPath,
            advisorArtifactPath,
            null,
            null,
            null,
            state,
            cancellationToken);

        return new WorkflowRunResult
        {
            CaseId = input.CaseId,
            RunId = runId,
            Status = "NeedMoreInfo",
            AdvisorArtifactPath = advisorArtifactPath,
            ReviewTaskId = reviewTaskId
        };
    }

    private async Task<string> BuildDocxAsync(
        string caseId,
        DeliverableMemo deliverableMemo,
        CancellationToken cancellationToken)
    {
        using var docxScope = ExecuteToolScope.Start(new ExecuteToolOptions
        {
            ToolName = "OpenXmlDocxMemoBuilder",
            ToolType = "document-generation"
        });

        return await docxMemoBuilder.BuildAsync(
            caseId,
            deliverableMemo,
            cancellationToken);
    }

    private async Task WriteApprovedLearningAsync(
        KnowledgeInput input,
        string runId,
        DeliverableMemo deliverableMemo,
        CancellationToken cancellationToken)
    {
        using var memoryWriteScope = ExecuteToolScope.Start(new ExecuteToolOptions
        {
            ToolName = "FoundryMemoryWrite",
            ToolType = "memory-write"
        });

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

    private static IDisposable StartAgentScope(
        string agentId,
        string agentName,
        string tenantId,
        string? conversationId,
        string requestContent,
        string endpoint,
        string sourceName)
    {
        return InvokeAgentScope.Start(new InvokeAgentOptions
        {
            AgentId = agentId,
            AgentName = agentName,
            TenantId = tenantId,
            ConversationId = conversationId,
            RequestContent = requestContent,
            Endpoint = endpoint,
            SourceName = sourceName
        });
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
