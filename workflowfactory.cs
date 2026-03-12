using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Agents;
using Microsoft.Agents.Storage;
using System.Net.Http;
using EngineAgent;
using Microsoft.Extensions.Logging;

namespace Tax365Agent.Orchestration;

public interface IWorkflowFactory
{
    Workflow Build();
}

public class WorkflowFactory : IWorkflowFactory
{
    private readonly IStorage _storage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WorkflowFactory> _logger;

    public WorkflowFactory(IStorage storage, IHttpClientFactory httpClientFactory, ILogger<WorkflowFactory> logger)
    {
        _storage = storage;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Workflow Build()
    {
        // Simple demo prompts for the group chat pattern
        var taxAdvisorPrompt = @"Analyze the incoming text (tax alert and client info). Decide if more information is needed or if a deliverable should be created.
Return ONLY one JSON object with either:
{ ""action"": ""RequestInfo"", ""questions"": [ ""question1"", ""question2"" ] }
or
{ ""action"": ""GenerateDeliverable"", ""deliverableInstructions"": ""<text to send to deliverable agent>"" }
or
{ ""action"": ""Finalize"", ""deliverable"": ""<final deliverable as JSON or text>"" }
Do not include any other text.";

        var deliverablePrompt = @"Produce a comprehensive advisory memo summarizing incoming text (tax alert and client info).Return ONLY a single JSON object with the exact shape below (no extra prose outside the JSON):

{
 ""title"": ""string"",
 ""executiveSummary"": ""string (2-4 concise paragraphs summarizing key findings)"",
 ""background"": ""string (context on OECD profiles and why the update matters)"",
 ""oecdUpdates"": {
 ""summary"": ""string (high-level summary of the update)"",
 ""keyChanges"": [ ""string (each key change)"" ]
 },
 ""implications"": ""string (implications for multinationals, compliance, documentation, risk)"",
 ""recommendations"": [
 { ""recommendation"": ""string"", ""rationale"": ""string"", ""priority"": ""High|Medium|Low"" }
 ],
 ""implementationNotes"": ""string (practical steps, timelines, stakeholders)"",
 ""conclusion"": ""string (brief closing)"",
 ""references"": [ ""string (URLs or citation lines)"" ]
}

Requirements:
- Fill every field with complete, client-ready sentences.
- Keep JSON syntactically valid; use arrays/objects as shown.
- Provide concrete, actionable recommendations and implementation notes.
- Do not include any text outside the JSON object.";

        var reviewPrompt = @"Review the provided deliverable (JSON). Decide one of: Approved, Reject, NeedMoreInfo.
Return ONLY a JSON object like:
{ ""introduction"": ""I am your Reviewer Agent."", ""decision"": ""Approved|Reject|NeedMoreInfo"", ""comments"": ""explain briefly"", ""reviewerRole"": ""Manager"" }
If additional info is needed, set decision to 'NeedMoreInfo' and list required items in comments.";

        var httpClient = _httpClientFactory.CreateClient("WebClient");

        // Create and register an orchestrator to perform group-chat between roles
        var orchestrator = new GroupChatOrchestrator();
        orchestrator.RegisterRoles(taxAdvisorPrompt, deliverablePrompt, reviewPrompt);

        // Pass the orchestrator into Workflow and let it manage agent interactions
        return new Workflow(_storage, httpClient, taxAdvisorPrompt, deliverablePrompt, reviewPrompt,
            orchestrator: orchestrator,
            taxAdvisorAgent: null,
            deliverableAgent: null,
            reviewAgent: null);
    }
}
