using System.Text.Json;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowEditorHostAiTests
{
    [Fact]
    public async Task ApplyRechecksSessionAfterStreamAndAuthorizationBeforeMutating()
    {
        using var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("""
            let mutations = 0;
            const document = { body: {}, createElement: () => ({ remove() {} }), head: { append: element => element.onload() } };
            const MutationObserver = class { observe() {} disconnect() {} };
            const TextDecoder = class { decode(value) { return value; } };
            const window = { FlowbitEditor: { mount: () => ({ applyProposal() { mutations++; return {}; }, dispose() {} }) } };
            """);
        engine.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflow-editor-host.js"))
            .Replace("export async function create", "async function create", StringComparison.Ordinal));
        var result = await engine.EvaluateAsync("""
            (async () => {
              let authorized = true, resolveAuthorization = null;
              const dotnet = { invokeMethodAsync: () => resolveAuthorization
                ? new Promise(resolve => resolveAuthorization = resolve) : Promise.resolve(authorized) };
              const host = await create({ isConnected: true }, dotnet, { template: 'template', stylesheet: 'css', script: 'script' });
              const stream = { arrayBuffer: () => Promise.resolve('{}') };

              let release;
              host.beginAuthoringApply('cancelled');
              const cancelled = host.applyProposal({ arrayBuffer: () => new Promise(resolve => release = resolve) }, 'snapshot', 'cancelled')
                .then(() => false, () => true);
              host.invalidateAuthoringApply('cancelled'); release('{}');
              const streamCancelled = await cancelled;

              authorized = false; host.beginAuthoringApply('identity');
              const identityRejected = await host.applyProposal(stream, 'snapshot', 'identity').then(() => false, () => true);

              authorized = true; resolveAuthorization = true; host.beginAuthoringApply('late-cancel');
              const late = host.applyProposal(stream, 'snapshot', 'late-cancel').then(() => false, () => true);
              await Promise.resolve(); await Promise.resolve();
              host.invalidateAuthoringApply('late-cancel'); resolveAuthorization(true);
              const lateCancelled = await late;

              resolveAuthorization = null; host.beginAuthoringApply('valid');
              host.invalidateAuthoringApply('older-panel');
              await host.applyProposal(stream, 'snapshot', 'valid');
              host.beginAuthoringApply('disposed'); host.dispose();
              const disposedRejected = await host.applyProposal(stream, 'snapshot', 'disposed').then(() => false, () => true);
              return JSON.stringify({streamCancelled, identityRejected, lateCancelled, disposedRejected, mutations});
            })()
            """);
        using var json = JsonDocument.Parse(result.AsString());
        foreach (var property in json.RootElement.EnumerateObject().Where(property => property.Name != "mutations"))
            Assert.True(property.Value.GetBoolean(), property.Name);
        Assert.Equal(1, json.RootElement.GetProperty("mutations").GetInt32());
    }
}
