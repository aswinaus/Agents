using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.A365.Observability.Runtime;
using TaxAgent.Level3.Api.Contracts;
using TaxAgent.Level3.Api.Endpoints;
using TaxAgent.Level3.Api.Memory;
using TaxAgent.Level3.Api.Options;
using TaxAgent.Level3.Api.Prompts;
using TaxAgent.Level3.Api.Services;
using TaxAgent.Level3.Api.Storage;
using TaxAgent.Level4.Evaluation.Interfaces;
using TaxAgent.Level4.Evaluation.Services;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// Configuration
// -----------------------------------------------------------------------------
builder.Services.Configure<AzureOpenAiOptions>(
    builder.Configuration.GetSection("AzureOpenAI"));

builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection("Storage"));

// -----------------------------------------------------------------------------
// Infra for Agent 365 token resolution
// -----------------------------------------------------------------------------
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("a365-token");

// -----------------------------------------------------------------------------
// Chat client
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<IChatClient>(_ =>
    ChatClientFactory.CreateInstrumentedChatClient());

// -----------------------------------------------------------------------------
// Core app services
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<IArtifactStore, FileArtifactStore>();
builder.Services.AddSingleton<IWorkflowStateStore, FileWorkflowStateStore>();
builder.Services.AddSingleton<IHumanReviewStore, FileHumanReviewStore>();
builder.Services.AddSingleton<IMemoryService, InMemoryFoundryMemoryService>();
builder.Services.AddSingleton<IDocxMemoBuilder, OpenXmlDocxMemoBuilder>();
builder.Services.AddSingleton<IAgentStepRunner, AgentFrameworkStepRunner>();
builder.Services.AddSingleton<ILevel3WorkflowRunner, Level3WorkflowRunner>();

// -----------------------------------------------------------------------------
// Level 4 evaluation services
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<IEvaluationStore>(sp =>
{
    var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    return new BlobJsonEvaluationStore(Path.Combine(storage.RootPath, "evaluations"));
});

builder.Services.AddSingleton<IEvaluator, WeightedScoreEvaluator>();

// -----------------------------------------------------------------------------
// Azure Monitor / App Insights OpenTelemetry
// -----------------------------------------------------------------------------
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("Microsoft.Agents.*");
        tracing.AddSource("Microsoft.Extensions.AI");
    })
    .UseAzureMonitor(options =>
    {
        options.ConnectionString =
            builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    });

// -----------------------------------------------------------------------------
// Agent 365 observability
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<ObservabilityTokenProvider>();

builder.Services.AddSingleton(sp =>
{
    var tokenProvider = sp.GetRequiredService<ObservabilityTokenProvider>();

    return new Agent365ExporterOptions
    {
        ClusterCategory = builder.Configuration["Agent365:ClusterCategory"] ?? "prod",
        TokenResolver = async (agentId, tenantId) =>
        {
            return await tokenProvider.GetObservabilityTokenAsync(agentId, tenantId);
        }
    };
});

// Exports telemetry to Agent 365 when ENABLE_A365_OBSERVABILITY_EXPORTER=true
builder.AddA365Tracing();

// Auto-instrument Agent Framework operations for Agent 365
builder.Services.AddTracing(config => config.WithAgentFramework());

// -----------------------------------------------------------------------------
// Agents
// -----------------------------------------------------------------------------
builder.AddAIAgent(
    "tax_advisor",
    instructions: PromptCatalog.TaxAdvisorPrompt)
    .WithInMemorySessionStore();

builder.AddAIAgent(
    "tax_deliverable",
    instructions: PromptCatalog.DeliverablePrompt)
    .WithInMemorySessionStore();

builder.AddAIAgent(
    "tax_reviewer",
    instructions: PromptCatalog.ReviewPrompt)
    .WithInMemorySessionStore();

// -----------------------------------------------------------------------------
// Group chat workflow
// -----------------------------------------------------------------------------
builder.AddWorkflow("tax-level3-groupchat", (sp, key) =>
{
    var advisor = sp.GetRequiredKeyedService<AIAgent>("tax_advisor");
    var deliverable = sp.GetRequiredKeyedService<AIAgent>("tax_deliverable");
    var reviewer = sp.GetRequiredKeyedService<AIAgent>("tax_reviewer");

    return AgentWorkflowBuilder
        .CreateGroupChatBuilderWith(agents => new RoundRobinGroupChatManager(agents)
        {
            MaximumIterationCount = 4
        })
        .WithName("tax-level3-groupchat")
        .AddParticipants(advisor, deliverable, reviewer)
        .Build();
}).AddAsAIAgent();

// -----------------------------------------------------------------------------
// App
// -----------------------------------------------------------------------------
var app = builder.Build();

app.MapLevel3Endpoints();

app.Run();
