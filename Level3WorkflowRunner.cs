using System.Text.Json;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;
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
    IEvaluationStore evaluationStore)
    : ILevel3WorkflowRunner
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

        using var rootScope = StartInvokeScope(
            invokedAgentId: Level3AgentId,
            invokedAgentName: Level3AgentName,
            tenantId: input.TenantId,
            conversationId: input.ConversationId,
            requestContent: input.Title,
            endpoint: "/api/level3/run",
            sourceName: CopilotSourceName,
            callerAgentId: null,
            callerAgentName: null);

        var state = new CaseState
        {
            CaseId = input.CaseId,
            TenantId = input.TenantId,
            Status = "Running",
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        await stateStore.SaveAsync(state, cancellationToken);

        var recalledMemory = await memoryService.RecallAsync(
            new MemoryQuery
            {
                TenantId = input.TenantId,
                ClientId = input.ClientId,
                CaseId = input.CaseId,
                Topic = input.Title
            },
            cancellationToken);

        var inputEnvelope = new
        {
            Source = input,
            Memory = recalledMemory
        };

        var inputArtifactPath = await artifactStore.SaveJsonAsync(
            $"runs/{runId}/input.json",
            inputEnvelope,
            cancellationToken);

        var advisorInputJson = JsonSerializer.Serialize(inputEnvelope, JsonOptions);

        using var advisorScope = StartInvokeScope(
            invokedAgentId: TaxAdvisorAgentId,
            invokedAgentName: TaxAdvisorAgentName,
            tenantId: input.TenantId,
            conversationId: input.ConversationId,
            requestContent: advisorInputJson,
            endpoint: $"internal://{TaxAdvisorAgentId}",
            sourceName: WorkflowSourceName,
            callerAgentId: Level3AgentId,
            callerAgentName: Level3AgentName);

        var advisorText = await stepRunner.RunAgentAsync(
            TaxAdvisorAgentId,
            advisorInputJson,
            cancellationToken);

        var advisorArtifactPath = await artifactStore.SaveTextAsync(
            $"runs/{runId}/advisor-output.json",
            advisorText,
            cancellationToken);

        var advisorDecision = ParseJson<AdvisorDecision>(advisorText, TaxAdvisorAgentId);

        if (advisorDecision.Action.Equals("NeedMoreInfo", StringComparison.OrdinalIgnoreCase))
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

        var deliverableInput = new
        {
            Source = input,
            Advisor = advisorDecision,
            Memory = recalledMemory
        };

        var deliverableInputJson = JsonSerializer.Serialize(deliverableInput, JsonOptions);

        using var deliverableScope = StartInvokeScope(
            invokedAgentId: TaxDeliverableAgentId,
            invokedAgentName: TaxDeliverableAgentName,
            tenantId: input.TenantId,
            conversationId: input.ConversationId,
            requestContent: deliverableInputJson,
            endpoint: $"internal://{TaxDeliverableAgentId}",
            sourceName: WorkflowSourceName,
            callerAgentId: Level3AgentId,
            callerAgentName: Level3AgentName);

        var deliverableText = await stepRunner.RunAgentAsync(
            TaxDeliverableAgentId,
            deliverableInputJson,
            cancellationToken);

        var deliverableArtifactPath = await artifactStore.SaveTextAsync(
            $"runs/{runId}/deliverable-output.json",
            deliverableText,
            cancellationToken);

        var deliverableMemo = ParseJson<DeliverableMemo>(deliverableText, TaxDeliverableAgentId);

        var reviewInput = new
        {
            Source = input,
            Memo = deliverableMemo,
            Advisor = advisorDecision,
            Memory = recalledMemory
        };

        var reviewInputJson = JsonSerializer.Serialize(reviewInput, JsonOptions);

        using var reviewerScope = StartInvokeScope(
            invokedAgentId: TaxReviewerAgentId,
            invokedAgentName: TaxReviewerAgentName,
            tenantId: input.TenantId,
            conversationId: input.ConversationId,
            requestContent: reviewInputJson,
            endpoint: $"internal://{TaxReviewerAgentId}",
            sourceName: WorkflowSourceName,
            callerAgentId: Level3AgentId,
            callerAgentName: Level3AgentName);

        var reviewText = await stepRunner.RunAgentAsync(
            TaxReviewerAgentId,
            reviewInputJson,
            cancellationToken);

        var reviewArtifactPath = await artifactStore.SaveTextAsync(
            $"runs/{runId}/review-output.json",
            reviewText,
            cancellationToken);

        var reviewDecision = ParseJson<ReviewDecision>(reviewText, TaxReviewerAgentId);

        string? docxPath = null;
        string? reviewTaskPath = null;

        if (reviewDecision.Decision.Equals("Approved", StringComparison.OrdinalIgnoreCase))
        {
            using (var docxScope = StartToolScope(
                       toolName: "OpenXmlDocxMemoBuilder",
                       toolType: "document-generation",
                       tenantId: input.TenantId,
                       conversationId: input.ConversationId,
                       agentId: Level3AgentId,
                       agentName: Level3AgentName,
                       arguments: JsonSerializer.Serialize(
                           new
                           {
                               input.CaseId,
                               MemoTitle = input.Title
                           },
                           JsonOptions)))
            {
                docxPath = await docxMemoBuilder.BuildAsync(
                    input.CaseId,
                    deliverableMemo,
                    cancellationToken);
            }

            state.Status = "Approved";
            state.FinalDocxPath = docxPath;

            using (var memoryWriteScope = StartToolScope(
                       toolName: "FoundryMemoryWrite",
                       toolType: "memory-write",
                       tenantId: input.TenantId,
                       conversationId: input.ConversationId,
                       agentId: Level3AgentId,
                       agentName: Level3AgentName,
                       arguments: JsonSerializer.Serialize(
                           new
                           {
                               input.TenantId,
                               input.ClientId,
                               input.CaseId,
                               Category = "ApprovedMemoPattern",
                               input.Title,
                               Source = "Level3Workflow",
                               runId
                           },
                           JsonOptions)))
            {
                await memoryService.WriteApprovedLearningAsync(
                    new MemoryWriteRequest
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
                    },
                    cancellationToken);
            }
        }
        else if (reviewDecision.Decision.Equals("NeedMoreInfo", StringComparison.OrdinalIgnoreCase))
        {
            state.Status = "NeedMoreInfo";
            state.NeedMoreInfoCount++;

            reviewTaskPath = await humanReviewStore.CreateReviewTaskAsync(
                input,
                deliverableMemo,
                reviewDecision,
                cancellationToken);
        }
        else
        {
            state.Status = "Rejected";
            state.RevisionCount++;

            reviewTaskPath = await humanReviewStore.CreateEscalationTaskAsync(
                input,
                deliverableMemo,
                reviewDecision,
                cancellationToken);
        }

        state.CurrentMemoJsonPath = deliverableArtifactPath;
        state.CurrentReviewJsonPath = reviewArtifactPath;
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await stateStore.SaveAsync(state, cancellationToken);

        await SaveEvaluationStubAsync(
            input,
            runId,
            inputArtifactPath,
            advisorArtifactPath,
            deliverableArtifactPath,
            reviewArtifactPath,
            docxPath,
            state,
            cancellationToken);

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

    private static InvokeAgentScope StartInvokeScope(
        string invokedAgentId,
        string invokedAgentName,
        string tenantId,
        string? conversationId,
        string requestContent,
        string endpoint,
        string sourceName,
        string? callerAgentId,
        string? callerAgentName)
    {
        var invokeAgentDetails = new InvokeAgentDetails
        {
            AgentId = invokedAgentId,
            AgentName = invokedAgentName,
            TenantId = tenantId,
            ConversationId = conversationId,
            Endpoint = endpoint
        };

        var tenantDetails = new TenantDetails
        {
            TenantId = tenantId
        };

        var request = new Request
        {
            Content = requestContent,
            SourceMetadata = new SourceMetadata
            {
                Name = sourceName
            }
        };

        AgentDetails? callerAgentDetails = null;

        if (!string.IsNullOrWhiteSpace(callerAgentId))
        {
            callerAgentDetails = new AgentDetails
            {
                AgentId = callerAgentId,
                AgentName = callerAgentName,
                ConversationId = conversationId,
                TenantId = tenantId
            };
        }

        return InvokeAgentScope.Start(
            invokeAgentDetails,
            tenantDetails,
            request,
            callerAgentDetails,
            callerDetails: null,
            conversationId: conversationId);
    }

    private static ExecuteToolScope StartToolScope(
        string toolName,
        string toolType,
        string tenantId,
        string? conversationId,
        string agentId,
        string agentName,
        string? arguments = null)
    {
        var toolCallDetails = new ToolCallDetails
        {
            ToolCallId = Guid.NewGuid().ToString("N"),
            ToolName = toolName,
            ToolType = toolType,
            Arguments = arguments
        };

        var agentDetails = new AgentDetails
        {
            AgentId = agentId,
            AgentName = agentName,
            ConversationId = conversationId,
            TenantId = tenantId
        };

        var tenantDetails = new TenantDetails
        {
            TenantId = tenantId
        };

        return ExecuteToolScope.Start(
            toolCallDetails,
            agentDetails,
            tenantDetails,
            parentId: conversationId);
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
