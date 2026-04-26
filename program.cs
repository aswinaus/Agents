using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
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

builder.Services.Configure<AzureOpenAiOptions>(builder.Configuration.GetSection("AzureOpenAI"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));


builder.Services.AddSingleton<IChatClient>(_ =>
    ChatClientFactory.CreateInstrumentedChatClient());


//builder.Services.AddSingleton(sp =>
//{
//    var options = sp.GetRequiredService<IOptions<AzureOpenAiOptions>>().Value;
//    if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.DeploymentName))
//    {
//        throw new InvalidOperationException("AzureOpenAI configuration is missing.");
//    }

//    var chatClientFactory = sp.GetRequiredService<IChatClientFactory>();
//    IChatClient instrumentedChatClient = chatClientFactory.Create(
//        new Uri(options.Endpoint),
//        new ManagedIdentityCredential(),
//        options.DeploymentName);

//    return instrumentedChatClient;
//});

builder.Services.AddSingleton<IArtifactStore, FileArtifactStore>();
builder.Services.AddSingleton<IWorkflowStateStore, FileWorkflowStateStore>();
builder.Services.AddSingleton<IHumanReviewStore, FileHumanReviewStore>();
builder.Services.AddSingleton<IMemoryService, InMemoryFoundryMemoryService>();
builder.Services.AddSingleton<IDocxMemoBuilder, OpenXmlDocxMemoBuilder>();
builder.Services.AddSingleton<IAgentStepRunner, AgentFrameworkStepRunner>();
builder.Services.AddSingleton<ILevel3WorkflowRunner, Level3WorkflowRunner>();

builder.Services.AddSingleton<IEvaluationStore>(sp =>
{
    var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    return new BlobJsonEvaluationStore(Path.Combine(storage.RootPath, "evaluations"));
});
builder.Services.AddSingleton<TaxAgent.Level4.Evaluation.Interfaces.IEvaluator, WeightedScoreEvaluator>();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("Microsoft.Agents.*");
        tracing.AddSource("Microsoft.Extensions.AI");
    })
    .UseAzureMonitor(options =>
    {
        options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    });

builder.AddAIAgent(
    "tax_advisor",
    instructions: PromptCatalog.TaxAdvisorPrompt)
    //.WithDescription("Assesses if the tax package is sufficient and emits bounded JSON.")
    .WithInMemorySessionStore();

builder.AddAIAgent(
    "tax_deliverable",
    instructions: PromptCatalog.DeliverablePrompt)
    //.WithDescription("Builds the memo JSON.")
    .WithInMemorySessionStore();

builder.AddAIAgent(
    "tax_reviewer",
    instructions: PromptCatalog.ReviewPrompt)
    //.WithDescription("Approves, rejects, or requests critical missing information.")
    .WithInMemorySessionStore();

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

var app = builder.Build();

app.MapLevel3Endpoints();
app.Run();
