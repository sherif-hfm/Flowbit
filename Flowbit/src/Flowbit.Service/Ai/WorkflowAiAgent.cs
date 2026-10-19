using Flowbit.Service.Authoring;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Flowbit.Service.Ai;

/// <summary>The single GLM agent that authors workflows through the file tools, as in Flowbit AI Lab v3 (Agents/WorkflowAuthorAgent.cs).</summary>
public static class WorkflowAiAgent
{
    public const string AgentId = "flowbit_workflow_author";
    public const string AgentName = "FlowbitWorkflowAuthor";
    public const string WorkflowFile = "workflow.json";
    public const string OpenQuestionsFile = "open-questions.md";

    // The lab's two lines verbatim, plus a line for an existing diagram (user decision, 2026-10-16). Joined with "\n" so that a
    // CRLF checkout cannot change the bytes, and with them the prompt fingerprint.
    public static readonly string Instructions = string.Join("\n",
        "Generate a Flowbit workflow based on these requirements. Check the md files and example files to learn about Flowbit and the requirements.",
        "Use get_files_list and get_file to read the Flowbit documentation and examples. Save the complete canonical workflow JSON with save_append_file as workflow.json. Write any open questions and assumptions to open-questions.md.",
        "If workflow.json is already saved, it is the current workflow: change it as requested and keep everything else unchanged.");

    public static string PromptFingerprint { get; } = AuthoringPackageBuilder.Hash(Instructions);

    /// <summary>
    /// The agent over <paramref name="provider"/>. Outermost first: function invocation, the checkpoint layer, per-call history
    /// persistence, compact tool results, then the provider client. History is stored after every model call.
    /// </summary>
    public static ChatClientAgent Create(IChatClient provider, IList<AITool> tools, string modelId, string? reasoningEffort,
        int maxOutputTokens, int maxRequests, Func<IChatClient, IChatClient> checkpoints)
    {
        var chat = new CompactToolResultsChatClient(provider)
            .AsBuilder()
            .UseFunctionInvocation(configure: functions =>
            {
                functions.AllowConcurrentInvocation = false;
                // Tool argument errors go back to the model so it can correct the call; the request cap still bounds the run.
                functions.IncludeDetailedErrors = true;
                functions.MaximumIterationsPerRequest = maxRequests;
            })
            .Use(checkpoints)
            .UsePerServiceCallChatHistoryPersistence()
            .Build();
        return new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Id = AgentId,
            Name = AgentName,
            UseProvidedChatClientAsIs = true,
            RequirePerServiceCallChatHistoryPersistence = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                ModelId = modelId,
                Reasoning = reasoningEffort switch
                {
                    "low" => new ReasoningOptions { Effort = ReasoningEffort.Low },
                    "medium" => new ReasoningOptions { Effort = ReasoningEffort.Medium },
                    "high" => new ReasoningOptions { Effort = ReasoningEffort.High },
                    _ => null
                },
                MaxOutputTokens = maxOutputTokens,
                Tools = tools,
                ToolMode = ChatToolMode.Auto
            }
        });
    }
}
