using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceApiTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task Reads_separate_summary_visibility_from_payload_and_never_offer_human_actions()
    {
        var (executionId, itemId) = await SeedAsync();
        var listPath = $"/api/multi-instance-executions/{executionId}/items";
        using var anonymousList = await fixture.Client.GetAsync(listPath);
        using var anonymousDetail = await fixture.Client.GetAsync($"{listPath}/{itemId}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousDetail.StatusCode);

        using var summary = await GetAsync(listPath + "?page=2&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var page = await summary.Content.ReadFromJsonAsync<PagedResult<AutomaticMultiInstanceItemSummaryDto>>();
        Assert.Equal(2, page!.TotalCount);
        Assert.Equal(1, Assert.Single(page.Items).ItemIndex);
        Assert.False(page.Items[0].CanReadPayload);
        var summaryJson = await summary.Content.ReadAsStringAsync();
        Assert.DoesNotContain("copied-payload", summaryJson);
        Assert.DoesNotContain("staging-secret", summaryJson);

        using var hidden = await GetAsync($"{listPath}/{itemId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var detail = await GetAsync($"{listPath}/{itemId}", "mi-payload-reader");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var payload = await detail.Content.ReadFromJsonAsync<AutomaticMultiInstanceItemDetailDto>();
        Assert.Equal("copied-payload", payload!.Item!.Value.GetString());
        Assert.DoesNotContain("staging-secret", await detail.Content.ReadAsStringAsync());
        using var wrongOwner = await GetAsync($"/api/multi-instance-executions/{executionId + 1_000_000}/items/{itemId}", "mi-payload-reader");
        Assert.Equal(HttpStatusCode.NotFound, wrongOwner.StatusCode);

        foreach (var suffix in new[] { "flows", "roles" })
        {
            using var response = await GetAsync($"/api/multi-instance-executions/{executionId}/{suffix}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var malformed = await GetAsync(listPath + "?page=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var emptyPage = await GetAsync(listPath + "?page=999&pageSize=200");
        var empty = await emptyPage.Content.ReadFromJsonAsync<PagedResult<AutomaticMultiInstanceItemSummaryDto>>();
        Assert.Empty(empty!.Items);
        Assert.Equal(2, empty.TotalCount);
    }

    [Fact]
    public async Task Authoring_capabilities_report_the_api_release_switch_to_workflow_authors()
    {
        using var anonymous = await fixture.Client.GetAsync("/api/workflows/authoring-capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var forbidden = await GetAsync("/api/workflows/authoring-capabilities");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        // The test host is not Development, so the switch stays closed whatever its option says.
        using var closed = await GetAsync("/api/workflows/authoring-capabilities", "admin");
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.False((await closed.Content.ReadFromJsonAsync<WorkflowAuthoringCapabilitiesDto>())!.AutomaticMultiInstance);

        var open = new Flowbit.Service.Services.AutomaticMultiInstanceOptions { AllowInDevelopment = true };
        open.ResolveForEnvironment(isDevelopment: true);
        var result = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<WorkflowAuthoringCapabilitiesDto>>(
            Flowbit.Api.Endpoints.WorkflowDefinitionEndpoints.GetAuthoringCapabilities(open));
        Assert.True(result.Value!.AutomaticMultiInstance);
    }

    private async Task<(long ExecutionId, long ItemId)> SeedAsync()
    {
        // Repository/API contract test only. Browser acceptance must initialize
        // real bodies through adapters rather than manufacturing runtime rows.
        await using var db = fixture.CreateDbContext();
        var key = $"auto-item-api-{Guid.NewGuid():N}";
        var definition = new WorkflowDefinitionEntity
        {
            Name = key, WorkflowKey = key, Version = 1,
            Definition = new WorkflowModel { Id = key, Name = key, TaskAssignmentRoles = ["mi-payload-reader"] }
        };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var instance = new WorkflowInstanceEntity { WorkflowDefinitionId = definition.Id, WorkflowKey = key, Status = "running" };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        var token = new ExecutionTokenEntity { InstanceId = instance.Id, NodeId = 2, NodeName = "Automatic work", NodeType = "serviceTask" };
        db.ExecutionTokens.Add(token);
        await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var repository = new AutomaticMultiInstanceRepository(db);
        var body = await repository.CreateAsync(new AutomaticMultiInstanceCreateRecord
        {
            InstanceId = instance.Id, TokenId = token.Id, WorkflowDefinitionId = definition.Id,
            ActivationId = token.ActivationId, NodeId = 2, NodeName = token.NodeName, ActivityKind = "serviceTask",
            Mode = "parallel", ResultVariable = "results", AggregateScope = "instance", AggregateValueVersion = 0,
            MaxInstances = 1000, EffectiveConcurrency = 2, TotalCount = 2, ResultBytes = 2,
            InputSnapshot = JsonSerializer.SerializeToElement(new { collection = new[] { "copied-payload", "second" } })
        }, default);
        var items = await repository.AdmitAsync(body.Id, token.ActivationId, 2, default);
        await repository.SaveStageAsync(body.Id, items[0].Id, token.ActivationId,
            JsonSerializer.SerializeToElement(new { hidden = "staging-secret" }), default);
        await tx.CommitAsync();
        return (body.Id, items[0].Id);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string role = "worker")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        ApiTestAuth.Authorize(request, "reader", [role]);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return await fixture.Client.SendAsync(request);
    }
}
