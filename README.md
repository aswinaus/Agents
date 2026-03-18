# Agents
Level 3 Agent implementation under the Microsoft Agents “Think & Act” maturity model using [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/overview/?pivots=programming-language-csharp) to showcase Autonmous reasoning, multi-step planning, tool/workflow execution, coordinated multi-agent collaboration, [state management of agents](https://learn.microsoft.com/en-us/agent-framework/workflows/state?pivots=programming-language-csharp) within [GroupChatOrchestrator](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/group-chat?pivots=programming-language-csharp) and [Observability](https://learn.microsoft.com/en-us/agent-framework/workflows/observability?pivots=programming-language-csharp).

Demonstrates Level 3 (Think & Act) agent behavior exhibiting autonomous reasoning, multi step planning, tool/[workflow](https://learn.microsoft.com/en-us/agent-framework/get-started/workflows?pivots=programming-language-csharp) execution and coordinated multi agent collaboration to achieve a business outcome (tax memo creation).

1. Autonomous Trigger → Goal Formation

	•	Trigger: SharePoint List event (tax alert published)
	
	•	Agent: [Copilot Studio Agent](https://learn.microsoft.com/en-us/agent-framework/agents/providers/copilot-studio?pivots=programming-language-csharp)
	
	•	Behavior: Initiates the workflow without human prompting

	This shows autonomous task initiation

2. Multi Step Reasoning & Planning
This flow is not a single prompt → response.

The agent:

	1.	Detects a tax alert
	
	2.	Calls a knowledge acquisition agent to identify impacted internal clients
	
	3.	Delegates execution to a custom code engine agent
	
	4.	Executes a structured, multi agent workflow
    
	This is explicit planning and delegation — core Level 3 behavior

3. Tool & [Workflow](https://learn.microsoft.com/en-us/agent-framework/get-started/workflows?pivots=programming-language-csharp) Execution (“Act”)

	•	The [GroupChatOrchestrator](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/group-chat?pivots=programming-language-csharp) is exposed as a workflow
	
	•	It is invoked programmatically by the agent
	
	•	The agent is not just reasoning — it is executing actions

	Showcases “Act” capability (not Level 2)

4. Multi Agent Collaboration (Key Level 3)

	Inside your GroupChatOrchestrator, you have four specialized agents:
	
	**Agent	Responsibility**
	
	**Tax Advisor**	Domain reasoning & interpretation
	
	**Deliverable**	Drafting the tax memo
	
	**Reviewer**	Quality, accuracy, and compliance checks
	
	**Dispatcher**	Turn taking, coordination, and flow control
	
	This is role based, cooperative agent reasoning, not a single monolithic agent
	
	The Dispatcher acting as a coordinator is especially strong Level 3 evidence

5. Outcome Driven Completion
   
	•	The system produces a final reviewed tax memo

	•	Explicit decision points (e.g., reviewer requests revisions)

	•	Retry or fallback logic

	•	State tracking across agent turns

	•	Audit/logging of agent reasoning steps

	•	Memo is delivered through downstream systems (email, storage, etc.)

**End to end autonomous execution tied to a business outcome**
	Autonomous initiation.

	Multi step planning.
	
	Tool/workflow execution.
	
	Multi agent orchestration.
	
	Business aligned outcome.


**Solution:**








AI Agent implementation with TP and Form990 Tax Form application

# [Routing Agent with Semantic Caching](https://github.com/aswinaus/Agents/blob/main/Routing_Agent_with_Semantic_Caching.ipynb)  
Routing Agent for Transfer Pricing and Form990 tax application scenario with semantic caching. 

Agentic reasoning implementation uses an LLM to reason about the appropriate RAG pipeline to pick based on the users input question. And based on the observation decides whether to call api to fetch relevant data.

This is the most simplest form of agentic reasoning.
# [Routing Agent with Observability](https://github.com/aswinaus/Agents/blob/main/Routing_Agent_Observability.ipynb)
# [Routing Agent with Observability at tools](https://github.com/aswinaus/Agents/blob/main/Routing_Agent_Observability_at_tools.ipynb)

-----------------------------------------------------------------------

**Microsoft Agent Framework**

**Solution 1**

•	WorkflowFactory builds true Semantic Kernel ChatCompletionAgent instances (SK agents) and passes them into Workflow.
•	GroupChatOrchestrator.RegisterRoles currently constructs EngineAgent.EngineAgent instances (a custom HTTP wrapper) — so the orchestrator is using EngineAgent, not SK ChatCompletionAgent.
•	The orchestrator implements a simple think-and-act flow (TaxAdvisor -> Deliverable -> Review) by calling EngineAgent.RunAsync; it does not currently accept or invoke SK ChatCompletionAgent objects.
•	Workflow already has SK agent fields and commented SK loop scaffolding, but that runtime loop is not wired to GroupChatOrchestrator.


•	GroupChatOrchestrator
	•	Simple, synchronous wrapper around three EngineAgent instances.
	•	RegisterRoles(...) creates three EngineAgent objects (TaxAdvisor, Deliverable, Review).
	•	RunAsyncFlow(input) performs one pass: call TaxAdvisor → parse its JSON action → if GenerateDeliverable call Deliverable → then call Review and return the raw review output.
	•	This is a self-contained, non-stateful, single-turn orchestration. It only runs if you instantiate the orchestrator, call RegisterRoles, then call RunAsyncFlow.
	
•	WorkflowFactory + Workflow
	•	WorkflowFactory builds real Semantic Kernel ChatCompletionAgent instances and injects them into Workflow.
	•	Workflow.RunAsync is intended to perform the multi-agent orchestration using SK agents, but the actual SK chat loop and agent invocation are commented out / not implemented. The method currently:
	•	Loads/stores WorkflowState,
	•	Requires SK agent instances (throws if missing),
	•	Fails early unless state.CurrentDeliverableJson is already present (it does not generate deliverable by invoking the agents because the invocation is commented out),
	•	Then converts any existing deliverable JSON to DOCX / email / upload.



**Solution 2**

AIAgent is a primitive provided by Microsoft's Agent framework. The specific instance returned under the "tax-level3-groupchat" key acts as the overall entry point. This could be configured either as an orchestrator that explicitly routes execution across inner sub-agents, or as a single large workflow routine that sequentially triggers those steps.
2.	Does the Tax Advisor, Deliverable Agent, and Review Agent get called? Yes. Although you are only calling RunAsync() on the outer workflowAgent, this agent coordinates the execution of those three roles internally (the "GroupChat" pattern).
•	From your context, there is a GroupChatOrchestrator (src\TaxAgent.Level3.Api\Orchestration\GroupChatOrchestrator.cs) and a Workflow (or WorkflowFactory) setup designed to handle exactly this.
•	You can see it dynamically summarized afterward in your code block: "Agent Activities: - Tax Advisor: Analyzed input context... - Deliverable Agent: Generated professional JSON... - Review Agent: Assessed deliverable constraints..."
•	Inside the agent framework logic (e.g., inside RunAsync on inner orchestrator), it sequentially executes steps: passing the unparsed inputJson to the Tax Advisor first, analyzing its instruction response, passing that instruction to the Deliverable Agent, and finally passing the newly written memo draft to the Review Agent to judge.

When we do runasync how does the context tie the GroupOrchestrator and Workflow setup

The way the context ties the GroupChatOrchestrator and Workflow setup together during RunAsync() depends heavily on how the AIAgent instances are registered in your Dependency Injection (DI) container in ASP.NET Core. However, I can explain the mechanics based on the structure of your application.
Here is how the request flows and how the context links them together:
1. Level3Endpoints.cs creates the Context Object
In your code, you construct an anonymous clientContext object tailored to a single client (e.g. clientId = "CN-38038") and serialize it into inputJson:

var clientContext = new
{
    CaseId = caseId,
    CorrelationId = caseId,
    ClientId = clientId,
    ConversationId = conversationKey, // e.g. "CaseId_ClientId"
    TaxAlert = inputPackage.TaxAlert,
    TaxKnowledge = taxKnowledgeText
};
var inputJson = JsonSerializer.Serialize(clientContext);

This single string (inputJson) serves as both the initial trigger prompt and the identifying state key for the entire workflow.

2. Dependency Injection maps the "tax-level3-groupchat"
You request the agent via:

var workflowAgent = serviceProvider.GetRequiredKeyedService<AIAgent>("tax-level3-groupchat");

Somewhere in your Program.cs or startup configuration, AIAgent has been registered with the key "tax-level3-groupchat". This registration dictates what class actually intercepts the call.
In a typical Microsoft Agent framework setup, this top-level registered agent is mapped directly to a class that overrides InvokeAsync or RunAsync—often corresponding to your Workflow class or GroupChatOrchestrator.

3. State Management via ConversationId
If the registered AIAgent wraps the logic found in your Workflow.cs class, notice how Workflow.cs manages state natively. While we serialize the whole block of context to inputJson, the inner code reads it to maintain flow over iterations:
When Workflow.RunAsync is called, it extracts the ConversationId (which you passed in as conversationKey). Look at Workflow.cs (from code search context):

var key = StorageKey(conversationId);
var state = new WorkflowState();

// Tries to load previous iterations for this exact conversation key!
var stored = await _storage.ReadAsync<WorkflowState>(new[] { key }, cancellationToken);

This is the crucial link. Because you generated a unique combination of CaseId + ClientId as the conversationKey, the Workflow instance uses that key to query IStorage for current memory state.
4. Passing Execution to the internal Orchestrator
Once Workflow.cs establishes state, it takes the userInput (which contains your TaxAlert and TaxKnowledge) and appends it to its state tracker: state.TaxAlertContext += "\n" + userInput;
It then triggers a loop:

while (autonomousIterations < 5)
{
    string taxRaw = await _orchestrator.RunTaxAdvisorAsync(combinedContext, cancellationToken);
    // ... evaluates taxRaw to see if it should generate a deliverable
    
    // ... sends it to deliverable agent
    var deliverableRaw = await _orchestrator.RunDeliverableAsync(instr, cancellationToken);
    
    // ... sends it to the reviewer
    string reviewRawInitial = await _orchestrator.RunReviewAsync(reviewInput, cancellationToken);
}

The _orchestrator here is an instance of GroupChatOrchestrator. So the Workflow class acts as the persistent loop manager and state-saver (saving to Blob Storage/SharePoint/State Store), while it delegates the actual LLM prompt building and querying to GroupChatOrchestrator.
In Summary:
1.	You pass inputJson into the AIAgent wrapper.
2.	The wrapper parses the context (specifically ConversationId) to load client-specific state in the Workflow class.
3.	The Workflow class aggregates the text variables (TaxAlert, TaxKnowledge etc).
4.	The Workflow class iteratively loops, passing that aggregated context into the GroupChatOrchestrator methods (RunTaxAdvisorAsync, RunDeliverableAsync, etc.) which construct instructions for the LLMs.
5.	Workflow consolidates the outputs, applies your telemetry logic, and passes it back up for the endpoint to return.

What happens when you call RunAsync on it?
When you call var agentResult = await workflowAgent.RunAsync(inputJson), you are directly invoking this built-in Microsoft Agent Framework RoundRobinGroupChatManager.

Here is exactly how the flow works:

1.	Top-Level Entry: Your inputJson string containing the client context is fed into the GroupChatManager.
2.	Round-Robin Orchestration: The RoundRobinGroupChatManager is configured with three participants. When execution starts, it automatically feeds your inputJson to the first participant in the list.
3.	Participant 1 (tax_advisor): The tax_advisor agent receives the payload. It analyzes it against PromptCatalog.TaxAdvisorPrompt and yields a result. Because this is a group chat, the manager takes that result and feeds it to the next agent in the circle.
4.	Participant 2 (tax_deliverable): The tax_deliverable agent receives the context + what the advisor just said, and applies PromptCatalog.DeliverablePrompt to generate the custom JSON tax memo structure. It yields its result.
5.	Participant 3 (tax_reviewer): The manager passes the newly drafted deliverable to tax_reviewer. It evaluates the draft using PromptCatalog.ReviewPrompt.
6.	Iteration Bound: The process continues up to the MaximumIterationCount = 4 limit, or until the conversational loop naturally concludes (e.g., if the reviewer approves the document, signaling the chat can end).
Conclusion
Because you are using .AddAsAIAgent() with a correctly configured AgentWorkflowBuilder, the workflowAgent absolutely handles routing the prompt to the Advisor -> Deliverable -> Reviewer internally without you needing to write a completely separate looping orchestrator.

--------------------------------------
What is the difference between solution 1 and solution 2

Solution 1 Since it is not using AIAgent from Microsoft Agent Framework we need to stitch the three agents to execute within the workflow

private readonly IWorkflowFactory _workflowFactory;

public WorkflowController(IWorkflowFactory workflowFactory)
{
    _workflowFactory = workflowFactory;
}

Implementation of IWorkflowFactory

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
   //Constructor
    public Workflow Build()
	{
	        var taxAdvisorPrompt = @"Analyze the incoming text (tax alert and client info). Decide if more information is needed or if a deliverable should be created.
	Return ONLY one JSON object with either:
	{ ""action"": ""Request
	        var deliverablePrompt = @"Produce a comprehensive advisory memo summarizing incoming text (tax alert and client info).Return ONLY a single JSON object with the exact shape below (no extra prose outside the JSON):
	
	{
	 ""title"": ""string"",
	 ""executiveSummary"": ""s
	        var reviewPrompt = @"Review the provided deliverable (JSON). Decide one of: Approved, Reject, NeedMoreInfo.
	Return ONLY a JSON object like:
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


var workflow = _workflowFactory.Build();
var result = await workflow.RunAsync(conversationKey, initialContext, cancellationToken);
The above is custom Run

Solution 2 we us AIAgent from Microsoft Agent Framework to asynchrnously run the workflow

var workflowAgent = serviceProvider.GetRequiredKeyedService<AIAgent>("tax-level3-groupchat");
logger.LogInformation("Executing keyed AIAgent workflow 'tax-level3-groupchat' for CaseId: {CaseId}, Client: {Client}", caseId, clientId);

var agentResult = await workflowAgent.RunAsync(inputJson, cancellationToken: cancellationToken);

So what it means here what is the advantage of using AIAgent in Microsoft Agent Framework?

The Microsoft Agent Framework (using AIAgent and constructs like RoundRobinGroupChatManager) provides several major advantages over manually orchestrating LLM calls (like your GroupChatOrchestrator or Workflow class does).

By adopting the AIAgent primitives directly in Program.cs, you gain:

1. Abstracted State and Memory Management
When you register agents with .WithInMemorySessionStore() (as seen in your Program.cs), the framework natively tracks the conversation history. Instead of manually appending strings (e.g., state.TaxAlertContext += "\n" + userInput;), the framework automatically persists the back-and-forth flow and injects the context into each successive call. This prevents you from having to write and maintain complex conversation tracking logic.

2. Built-in Multi-Agent Orchestration Patterns
Your codebase includes a custom Workflow.cs with an explicit while loop handling iterations, manually evaluating "NeedMoreInfo" or "Approved" decisions, and pushing data to the next agent.

By using AgentWorkflowBuilder.CreateGroupChatBuilderWith(...), the framework handles this routing routing natively. It inherently understands the concept of participants (Tax Advisor -> Deliverable Agent -> Reviewer) and orchestrates the handoffs and iteration limits (MaximumIterationCount) without you writing the glue code.

3. Native Integration with Semantic Kernel / Tools
The AIAgent ecosystem natively interfaces with Microsoft’s broader AI libraries (like Semantic Kernel). This makes it incredibly easy to attach discrete functions or "tools" to specific agents. For mapping complex workflows, if the Deliverable Agent suddenly needed access to a calculator or a database lookup tool, it operates natively within this DI wrapper.

4. OpenTelemetry & Observability Out of the Box
In your Program.cs, you are using builder.AddA365Tracing();. When you use the native AIAgent wrapper instead of raw HTTP clients or direct SDK instantiations, the framework automatically instruments the workflow. Every transition between the Advisor, Deliverable, and Reviewer is traced, emitting execution times, token counts, and step identifiers to Azure Application Insights or Agent 365 natively. Your custom Workflow class had to use manual, simulated telemetry scopes to achieve something similar.

Summary
In your project, the custom GroupChatOrchestrator and Workflow classes represent an older or more manual way of doing things. Using the AIAgent wrapper via tax-level3-groupchat standardizes the execution, removes boilerplate looping/state code, and automatically lights up Microsoft telemetry.

Why you still have then why are we still having custom GroupChatOrchestrator and Workflow classes?

You are preserving the custom classes for two reasons:
1.	Tooling & Side Effects: The custom code (Workflow.cs and Level3WorkflowRunner.cs) contains all the actual business logic for making file outputs, firing off SendGrid emails, talking to SharePoint, and saving Human-In-The-Loop review tickets.
2.	Fallback / Migration: Your endpoint uses the native framework for the initial AI routing but keeps the custom runner as a safety net if the framework throws an exception.

Eventually, if you rewrite your external API calls as Agent Tools (e.g., creating a [Microsoft.SemanticKernel.KernelFunction] that uploads to SharePoint), you could attach those directly to the Delivery Agent or Reviewer Agent and fully decommission the old custom Workflow class entirely.



--------------------------------------------------------------------------------

Notes
this is the architecture for my Orchestrator which gets triggered when an item(tax alert + client impacted list) is added/modified in SharePoint list. Then the declarative agent tax advisor agent calls the datasource to get relevant data based on the incoming sharepoint list item text. Based on the data received a deliverable is generated for each client this is the third devlerable generated agent. And then the deliverable is sent to approval through Review sign off agent as this agent work is mainly to focus the back and forth between the tax advisor agent get additional data and get the deliverable signed off by user.


Why does Azure AI Foundry Agent does match with Microsoft SDK Agent we need: 

	• Event‑driven execution
	• Deterministic orchestration
	• Long‑running workflows
	• Human‑approval checkpoints
• Foundry does not host or manage that kind of workflow control

Agent 365 works with agents built using:
 
	• Microsoft 365 Agents SDK
	• Agent Framework
	• Copilot Studio
	• Azure AI Foundry
	• Semantic Kernel
OpenAI / LangGraph, etc.
