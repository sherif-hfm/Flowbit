using System.Net;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;
using static Flowbit.BrowserAcceptanceTests.ExampleApiClient;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// Runs the two showcase definitions end to end through real API and Worker processes,
/// with every actor authenticated through the token screen and every action sent over HTTP.
/// </summary>
[Collection(ExampleAcceptanceCollection.Name)]
public sealed class ExampleWorkflowShowcaseAcceptanceTests(ExampleAcceptanceFixture fixture)
{
    private const string PurchasePath = "showcases/01-purchase-request-approval.json";
    private const string OrderPath = "showcases/02-order-fulfilment.json";
    private static readonly string[] AdminRoles = ["admin"];
    private BrowserStackFixture Stack => fixture.Stack;
    private string Example(string relativePath) => Path.Combine(Stack.RepositoryRoot, "examples", relativePath);

    [Fact]
    public async Task PurchaseRequestRunsRevisionBoardQuorumAndParallelChecks()
    {
        await using var scenario = await Stack.CreateScenario("showcase-purchase-happy-path");
        await scenario.RunAsync("purchase request from submission to purchase order", async () =>
        {
            await using var admin = await CreateAsync(scenario, "showcase-admin", AdminRoles);
            await using var requester = await CreateAsync(scenario, "pr-requester", "Requester");
            await using var manager = await CreateAsync(scenario, "pr-manager", "Manager");
            await using var board1 = await CreateAsync(scenario, "board-one", "BoardMember");
            await using var board2 = await CreateAsync(scenario, "board-two", "BoardMember");
            await using var finance = await CreateAsync(scenario, "pr-finance", "Finance");
            await using var lead = await CreateAsync(scenario, "pr-lead", "ProcurementLead");
            await using var buyer = await CreateAsync(scenario, "pr-buyer", "Buyer");
            var workflow = await admin.Workflows.CreateAndPublishAsync(Example(PurchasePath));
            var requestNumber = $"PR-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

            var start = await requester.Workflows.StartInstanceAsync(workflow.Id, PurchaseInputs(requestNumber, 18000));
            var detail = await admin.Workflows.GetInstanceAsync(start.Id);
            Assert.True(AtNode(2)(detail));
            Assert.Equal(requestNumber, detail.BusinessKey);
            Assert.Equal("pr-requester", Value(detail, "requestedBy").GetString());
            var review = await SoleTaskAsync(manager, start.Id, 2);
            var reviewTask = await manager.Workflows.GetUserTaskAsync(review.UserTaskId);
            Assert.Equal("pr-manager", reviewTask.Assignee);
            Assert.Equal(50, reviewTask.Priority);
            Assert.True(reviewTask.DueAt > reviewTask.CreatedAt.AddHours(24));
            var deadline = Assert.Single(await admin.JobsAsync(start.Id), job => job.NodeId == 3 && job.Status == "queued");
            Assert.True(deadline.DueAt > DateTimeOffset.UtcNow.AddDays(2));
            await requester.ExpectStatusAsync(HttpStatusCode.Conflict, HttpMethod.Post, "/api/instances",
                new { workflowId = workflow.Id, variables = PurchaseInputs(requestNumber, 500) });

            await manager.Workflows.TakeUserTaskFlowAsync(review.UserTaskId, 202,
                Variables(("revisionNote", "Please add the second quote.")));
            var revise = await SoleTaskAsync(requester, start.Id, 6);
            Assert.Equal("pr-requester", (await requester.Workflows.GetUserTaskAsync(revise.UserTaskId)).Assignee);
            Assert.Equal("cancelled", Assert.Single(await admin.JobsAsync(start.Id), job => job.Id == deadline.Id).Status);
            await requester.Workflows.TakeUserTaskFlowAsync(revise.UserTaskId, 601,
                Variables(("amount", 32000), ("changeSummary", "Added a second quote and two docking stations.")));

            var second = await SoleTaskAsync(manager, start.Id, 2);
            Assert.NotEqual(review.UserTaskId, second.UserTaskId);
            Assert.Equal(80, (await manager.Workflows.GetUserTaskAsync(second.UserTaskId)).Priority);
            await manager.Workflows.TakeUserTaskFlowAsync(second.UserTaskId, 201,
                Variables(("managerComment", "Approved with the second quote.")));

            detail = await admin.Workflows.GetInstanceAsync(start.Id);
            Assert.True(AtNode(9)(detail));
            await board1.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(board1, start.Id, 9)).UserTaskId, 901,
                Variables(("voteComment", "Good value.")));
            Assert.True(AtNode(9)(await admin.Workflows.GetInstanceAsync(start.Id)));
            await board2.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(board2, start.Id, 9)).UserTaskId, 901);

            detail = await admin.Workflows.GetInstanceAsync(start.Id);
            Assert.True(AtNode(12)(detail));
            Assert.True(AtNode(13)(detail));
            Assert.Equal(3, Value(detail, "boardVotes").GetArrayLength());
            var votes = (await admin.ExecutionsAsync(start.Id)).Where(visit => visit.NodeId == 9).ToArray();
            Assert.Equal(2, votes.Count(visit => visit.Status == "completed"));
            Assert.Equal(1, votes.Count(visit => visit.Status == "cancelled"));

            var budget = await SoleTaskAsync(finance, start.Id, 12);
            await finance.Workflows.ClaimUserTaskAsync(budget.UserTaskId);
            await finance.Workflows.TakeUserTaskFlowAsync(budget.UserTaskId, 1201, Variables(("budgetCode", "IT-2026")));
            Assert.Empty(await buyer.Workflows.GetInboxAsync(start.Id));
            var supplier = Assert.Single(await lead.ManagedTasksAsync(start.Id), task => task.NodeId == 13);
            await lead.PostAsync<JsonElement>($"/api/user-tasks/{supplier.UserTaskId}/assign",
                new AssignUserTaskRequest("pr-buyer", supplier.UpdatedAt, "Buyer for IT equipment"));
            await buyer.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(buyer, start.Id, 13)).UserTaskId, 1301,
                Variables(("supplierName", "Northwind Supplies"), ("quotedAmount", 31500)));

            var order = await SoleTaskAsync(buyer, start.Id, 15);
            Assert.Equal("pr-buyer", order.Assignee);
            await buyer.Workflows.TakeUserTaskFlowAsync(order.UserTaskId, 1501, Variables(("purchaseOrderNumber", "PO-1001")));
            detail = await admin.Workflows.GetInstanceAsync(start.Id);
            Assert.Equal("completed", detail.Status);
            Assert.Equal(16, detail.Completion!.NodeId);
            Assert.Equal(32000, Value(detail, "amount").GetInt32());
        });
    }

    [Fact]
    public async Task PurchaseRequestStopsOnNoBudgetSendBackLimitWithdrawalAndEscalation()
    {
        await using var scenario = await Stack.CreateScenario("showcase-purchase-other-endings");
        await scenario.RunAsync("purchase request terminate, revision limit, withdrawal and escalation", async () =>
        {
            await using var admin = await CreateAsync(scenario, "showcase-admin", AdminRoles);
            await using var requester = await CreateAsync(scenario, "pr-requester", "Requester");
            await using var manager = await CreateAsync(scenario, "pr-manager", "Manager");
            await using var finance = await CreateAsync(scenario, "pr-finance", "Finance");
            await using var head = await CreateAsync(scenario, "pr-head", "DepartmentHead");
            await using var board1 = await CreateAsync(scenario, "board-one", "BoardMember");
            await using var board2 = await CreateAsync(scenario, "board-two", "BoardMember");
            var workflow = await admin.Workflows.CreateAndPublishAsync(Example(PurchasePath));

            // The manager rejects outright.
            var rejected = await requester.Workflows.StartInstanceAsync(workflow.Id, PurchaseInputs(NewRequestNumber(), 700));
            await manager.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(manager, rejected.Id, 2)).UserTaskId, 203,
                Variables(("rejectionReason", "We already have spare laptops.")));
            Assert.Equal(17, (await admin.Workflows.GetInstanceAsync(rejected.Id)).Completion!.NodeId);

            // Two board rejections decide the vote against the request.
            var boardRejected = await requester.Workflows.StartInstanceAsync(workflow.Id, PurchaseInputs(NewRequestNumber(), 60000));
            await manager.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(manager, boardRejected.Id, 2)).UserTaskId, 201);
            await board1.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(board1, boardRejected.Id, 9)).UserTaskId, 902);
            await board2.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(board2, boardRejected.Id, 9)).UserTaskId, 902,
                Variables(("voteComment", "Not this year.")));
            var boardDetail = await admin.Workflows.GetInstanceAsync(boardRejected.Id);
            Assert.Equal("completed", boardDetail.Status);
            Assert.Equal(22, boardDetail.Completion!.NodeId);

            // No budget: the terminate end cancels the open supplier selection.
            var noBudget = await requester.Workflows.StartInstanceAsync(workflow.Id, PurchaseInputs(NewRequestNumber(), 1200));
            await manager.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(manager, noBudget.Id, 2)).UserTaskId, 201);
            var budget = await SoleTaskAsync(finance, noBudget.Id, 12);
            await finance.Workflows.ClaimUserTaskAsync(budget.UserTaskId);
            await finance.Workflows.TakeUserTaskFlowAsync(budget.UserTaskId, 1202,
                Variables(("budgetNote", "The equipment budget is spent for this year.")));
            var stopped = await admin.Workflows.GetInstanceAsync(noBudget.Id);
            Assert.Equal("completed", stopped.Status);
            Assert.Equal("terminate", stopped.Completion!.Kind);
            Assert.Equal(21, stopped.Completion.NodeId);
            Assert.Equal("cancelled", Assert.Single(await admin.ExecutionsAsync(noBudget.Id), visit => visit.NodeId == 13).Status);

            // The third send-back closes the request instead of returning it.
            var limited = await requester.Workflows.StartInstanceAsync(workflow.Id, PurchaseInputs(NewRequestNumber(), 4000));
            for (var round = 1; round <= 3; round++)
            {
                await manager.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(manager, limited.Id, 2)).UserTaskId, 202,
                    Variables(("revisionNote", $"Round {round}: the quote is missing.")));
                if (round == 3) break;
                await requester.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(requester, limited.Id, 6)).UserTaskId, 601,
                    Variables(("amount", 4000), ("changeSummary", $"Attached quote number {round}.")));
            }
            var closed = await admin.Workflows.GetInstanceAsync(limited.Id);
            Assert.Equal("completed", closed.Status);
            Assert.Equal(19, closed.Completion!.NodeId);
            Assert.Equal(2, (await admin.ExecutionsAsync(limited.Id)).Count(visit => visit.NodeId == 6));

            // The requester withdraws a request that was sent back.
            var withdrawn = await requester.Workflows.StartInstanceAsync(workflow.Id, PurchaseInputs(NewRequestNumber(), 900));
            await manager.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(manager, withdrawn.Id, 2)).UserTaskId, 202,
                Variables(("revisionNote", "Is this still needed?")));
            await requester.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(requester, withdrawn.Id, 6)).UserTaskId, 602);
            Assert.Equal(18, (await admin.Workflows.GetInstanceAsync(withdrawn.Id)).Completion!.NodeId);

            // An administrator fires the three-working-day deadline now instead of waiting for it.
            var overdue = await requester.Workflows.StartInstanceAsync(workflow.Id, PurchaseInputs(NewRequestNumber(), 2500));
            await Stack.StartWorkerAsync();
            var batch = await admin.PostAsync<AdministrativeActionBatchDetailDto>("/api/administrative-action-batches",
                new CreateAdministrativeActionBatchRequest(workflow.Id, 2, "timerBoundary", 301, 3, null,
                    "Showcase: escalate an overdue review", null,
                    new("allMatching", null, new() { WorkflowDefinitionId = workflow.Id, SourceNodeId = 2, InstanceId = overdue.Id }, null),
                    Guid.NewGuid().ToString("N")));
            var ready = await UntilAsync(() => admin.GetAsync<AdministrativeActionBatchDetailDto>($"/api/administrative-action-batches/{batch.Summary.Id}"),
                current => current.Summary.Status == "ready", "escalation batch preparation");
            Assert.Equal(1, ready.Summary.EligibleItemCount);
            await admin.PostAsync<AdministrativeActionBatchDetailDto>($"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                new ConfirmAdministrativeActionBatchRequest(1, ready.Summary.TotalAffectedTaskCount, ready.Summary.UpdatedAt));
            await UntilAsync(() => admin.Workflows.GetInstanceAsync(overdue.Id), AtNode(4), "escalated review");
            var escalated = await SoleTaskAsync(head, overdue.Id, 4);
            Assert.Equal(90, (await head.Workflows.GetUserTaskAsync(escalated.UserTaskId)).Priority);
            await head.Workflows.ClaimUserTaskAsync(escalated.UserTaskId);
            await head.Workflows.TakeUserTaskFlowAsync(escalated.UserTaskId, 402,
                Variables(("rejectionReason", "Duplicates an order placed last month.")));
            Assert.Equal(17, (await admin.Workflows.GetInstanceAsync(overdue.Id)).Completion!.NodeId);
        });
    }

    [Fact]
    public async Task OrderFulfilmentRunsCreditReservationsAndShipmentConfirmation()
    {
        await using var scenario = await Stack.CreateScenario("showcase-order-happy-path");
        await scenario.RunAsync("order from message start to shipment confirmation", async () =>
        {
            await using var admin = await CreateAsync(scenario, "showcase-admin", AdminRoles);
            var (_, workflow) = await PublishOrderShowcaseAsync(admin);
            await Stack.StartWorkerAsync();
            var orderId = NewOrderId();
            var requestKey = $"request-{orderId}";
            var body = OrderMessage(orderId, $"good-{orderId[4..]}", expedited: false,
                new { sku = " kb-100 ", quantity = 2 }, new { sku = "MS-200", quantity = 1 });

            var ack = await admin.SendAsync<MessageStartAckDto>(HttpMethod.Post,
                $"/api/workflows/{workflow.WorkflowKey}/message-start", body, StartHeaders(requestKey));
            Assert.Equal("running", ack.Status);
            await admin.ExpectStatusAsync(HttpStatusCode.Conflict, HttpMethod.Post,
                $"/api/workflows/{workflow.WorkflowKey}/message-start", body, StartHeaders(requestKey));
            await admin.ExpectStatusAsync(HttpStatusCode.Conflict, HttpMethod.Post,
                $"/api/workflows/{workflow.WorkflowKey}/message-start", body, StartHeaders($"{requestKey}-again"));

            var waiting = await UntilAsync(() => admin.Workflows.GetInstanceAsync(ack.InstanceId), AtNode(18), "shipment wait");
            Assert.Equal(orderId, waiting.BusinessKey);
            Assert.Equal("standard", Value(waiting, "shippingService").GetString());
            Assert.Equal("approved", Value(waiting, "creditDecision").GetString());
            Assert.Equal(200, Value(waiting, "creditStatus").GetInt32());
            Assert.Equal($"SHP-{orderId}", Value(waiting, "shipmentReference").GetString());
            var rows = Value(waiting, "reservations").EnumerateArray().ToArray();
            Assert.Equal(new[] { $"RES-{orderId}-KB-100", $"RES-{orderId}-MS-200" },
                rows.Select(row => row.GetProperty("variables").GetProperty("reservationId").GetString()));
            Assert.All(rows, row => Assert.Equal("completed", row.GetProperty("status").GetString()));
            Assert.Single(fixture.Service.ShowcaseCalls, call => call.Route == "credit" && call.OrderId == orderId);

            var message = $"/api/instances/{ack.InstanceId}/message";
            var shipment = new { shipment = new { trackingNumber = "1Z999AA10123456784", carrier = "UPS" } };
            await admin.ExpectStatusAsync(HttpStatusCode.BadRequest, HttpMethod.Post, message, shipment,
                ShipmentHeaders("SHP-ORD-WRONG", $"delivery-{orderId}"));
            await admin.SendAsync<MessageDeliveryAckDto>(HttpMethod.Post, message, shipment,
                ShipmentHeaders($"SHP-{orderId}", $"delivery-{orderId}"));
            var fulfilled = await admin.Workflows.GetInstanceAsync(ack.InstanceId);
            Assert.Equal("completed", fulfilled.Status);
            Assert.Equal(22, fulfilled.Completion!.NodeId);
            Assert.Equal("1Z999AA10123456784", Value(fulfilled, "trackingNumber").GetString());
            await admin.ExpectStatusAsync(HttpStatusCode.Conflict, HttpMethod.Post, message, shipment,
                ShipmentHeaders($"SHP-{orderId}", $"delivery-{orderId}"));
        });
    }

    [Fact]
    public async Task OrderFulfilmentHandlesCreditOverrideStockShortageAndCarrierTimeout()
    {
        await using var scenario = await Stack.CreateScenario("showcase-order-recovery-paths");
        await scenario.RunAsync("credit override call, stock shortage retry, and carrier timeout", async () =>
        {
            await using var admin = await CreateAsync(scenario, "showcase-admin", AdminRoles);
            await using var creditManager = await CreateAsync(scenario, "credit-manager", "Manager");
            await using var warehouse = await CreateAsync(scenario, "warehouse-lead", "WarehouseLead");
            await using var clerk = await CreateAsync(scenario, "shipping-clerk", "ShippingClerk");
            var (child, workflow) = await PublishOrderShowcaseAsync(admin);
            await Stack.StartWorkerAsync();
            var orderId = NewOrderId();
            var ack = await admin.SendAsync<MessageStartAckDto>(HttpMethod.Post,
                $"/api/workflows/{workflow.WorkflowKey}/message-start",
                OrderMessage(orderId, $"review-{orderId[4..]}", expedited: true,
                    new { sku = "KB-100", quantity = 1 }, new { sku = "OUT-300", quantity = 4 }),
                StartHeaders($"request-{orderId}"));

            await UntilAsync(() => admin.Workflows.GetInstanceAsync(ack.InstanceId), AtNode(9), "credit override call");
            var approval = await UntilAsync(() => creditManager.Workflows.GetInboxAsync(),
                items => items.Any(item => item.WorkflowId == child.Id), "manager approval in the called workflow");
            await creditManager.Workflows.TakeUserTaskFlowAsync(
                approval.Single(item => item.WorkflowId == child.Id).UserTaskId, 201);

            await UntilAsync(() => admin.Workflows.GetInstanceAsync(ack.InstanceId), AtNode(15), "stock shortage task");
            var shortage = await admin.Workflows.GetInstanceAsync(ack.InstanceId);
            Assert.True(Value(shortage, "overrideApproved").GetBoolean());
            Assert.Equal("service_task_failed", Value(shortage, "stockFailure").GetString());
            Assert.Equal("express", Value(shortage, "shippingService").GetString());
            var resolve = await SoleTaskAsync(warehouse, ack.InstanceId, 15);
            Assert.Equal(90, (await warehouse.Workflows.GetUserTaskAsync(resolve.UserTaskId)).Priority);
            fixture.Service.Restock(orderId);
            await warehouse.Workflows.TakeUserTaskFlowAsync(resolve.UserTaskId, 1501,
                Variables(("resolutionNote", "Moved stock from the north warehouse.")));

            var waiting = await UntilAsync(() => admin.Workflows.GetInstanceAsync(ack.InstanceId), AtNode(18), "shipment wait");
            Assert.All(Value(waiting, "reservations").EnumerateArray(),
                row => Assert.Equal("completed", row.GetProperty("status").GetString()));
            var timer = Assert.Single(await admin.JobsAsync(ack.InstanceId), job => job.NodeId == 19 && job.Status == "queued");
            Assert.InRange(timer.DueAt - timer.CreatedAt, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(65));

            await UntilAsync(() => admin.Workflows.GetInstanceAsync(ack.InstanceId), AtNode(20), "carrier timeout",
                TimeSpan.FromSeconds(150));
            await admin.ExpectStatusAsync(HttpStatusCode.BadRequest, HttpMethod.Post, $"/api/instances/{ack.InstanceId}/message",
                new { shipment = new { trackingNumber = "LATE00000001", carrier = "DHL" } },
                ShipmentHeaders($"SHP-{orderId}", $"late-{orderId}"));
            var chase = await SoleTaskAsync(clerk, ack.InstanceId, 20);
            await clerk.Workflows.TakeUserTaskFlowAsync(chase.UserTaskId, 2001,
                Variables(("trackingNumber", "JD014600006281234567"), ("carrier", "DHL")));
            var fulfilled = await admin.Workflows.GetInstanceAsync(ack.InstanceId);
            Assert.Equal(22, fulfilled.Completion!.NodeId);
            Assert.Equal("DHL", Value(fulfilled, "carrier").GetString());
        });
    }

    [Fact]
    public async Task OrderFulfilmentEndsOnInvalidLinesRefusalDeclinedOverrideAndRecoversFromCreditOutage()
    {
        await using var scenario = await Stack.CreateScenario("showcase-order-failure-paths");
        await scenario.RunAsync("invalid lines, refused credit, declined override, and a recovered outage", async () =>
        {
            await using var admin = await CreateAsync(scenario, "showcase-admin", AdminRoles);
            await using var creditManager = await CreateAsync(scenario, "credit-manager", "Manager");
            var (child, workflow) = await PublishOrderShowcaseAsync(admin);
            var route = $"/api/workflows/{workflow.WorkflowKey}/message-start";

            var invalidId = NewOrderId();
            var invalid = await admin.SendAsync<MessageStartAckDto>(HttpMethod.Post, route,
                OrderMessage(invalidId, "good-invalid", expedited: false, new { sku = "KB-100", quantity = 0 }),
                StartHeaders($"request-{invalidId}"));
            Assert.Equal("faulted", invalid.Status);
            Assert.Equal("ORDER.INVALID_LINES", invalid.Fault!.Code);
            Assert.Contains("Order line 1", Value(await admin.Workflows.GetInstanceAsync(invalid.InstanceId), "orderProblem").GetString());

            await Stack.StartWorkerAsync();
            var refusedId = NewOrderId();
            var refused = await admin.SendAsync<MessageStartAckDto>(HttpMethod.Post, route,
                OrderMessage(refusedId, "refused-customer", expedited: false, new { sku = "KB-100", quantity = 1 }),
                StartHeaders($"request-{refusedId}"));
            var refusedDetail = await UntilAsync(() => admin.Workflows.GetInstanceAsync(refused.InstanceId),
                detail => detail.Status == "faulted", "refused credit");
            Assert.Equal("ORDER.CREDIT_REFUSED", refusedDetail.Fault!.Code);
            Assert.Equal(403, Value(refusedDetail, "creditStatus").GetInt32());
            Assert.Single(fixture.Service.ShowcaseCalls, call => call.OrderId == refusedId);

            var declinedId = NewOrderId();
            var declined = await admin.SendAsync<MessageStartAckDto>(HttpMethod.Post, route,
                OrderMessage(declinedId, "review-declined", expedited: false, new { sku = "KB-100", quantity = 1 }),
                StartHeaders($"request-{declinedId}"));
            var approval = await UntilAsync(() => creditManager.Workflows.GetInboxAsync(),
                items => items.Any(item => item.WorkflowId == child.Id), "manager approval in the called workflow");
            await creditManager.Workflows.TakeUserTaskFlowAsync(approval.Single(item => item.WorkflowId == child.Id).UserTaskId, 202);
            var declinedDetail = await UntilAsync(() => admin.Workflows.GetInstanceAsync(declined.InstanceId),
                detail => detail.Status == "completed", "declined override");
            Assert.Equal(11, declinedDetail.Completion!.NodeId);
            Assert.StartsWith("call_child_faulted", Value(declinedDetail, "overrideFailure").GetString());

            var outageId = NewOrderId();
            var customer = $"down-{outageId[4..]}";
            var outage = await admin.SendAsync<MessageStartAckDto>(HttpMethod.Post, route,
                OrderMessage(outageId, customer, expedited: false, new { sku = "KB-100", quantity = 1 }),
                StartHeaders($"request-{outageId}"));
            var incident = Assert.Single(await UntilAsync(() => admin.IncidentsAsync(outage.InstanceId),
                rows => rows.Any(row => row.Status == "open"), "credit outage incident"));
            Assert.Equal(5, incident.NodeId);
            Assert.Equal(3, (await admin.AttemptsAsync(incident.JobId)).Count);
            Assert.Equal(3, fixture.Service.ShowcaseCalls.Count(call => call.OrderId == outageId && call.StatusCode == 503));
            fixture.Service.RecoverCreditService(customer);
            await admin.PostAsync<RetryIncidentResultDto>($"/api/incidents/{incident.Id}/retry");
            var recovered = await UntilAsync(() => admin.Workflows.GetInstanceAsync(outage.InstanceId), AtNode(18), "recovered order");
            Assert.Equal("approved", Value(recovered, "creditDecision").GetString());
            await admin.ExpectStatusAsync(HttpStatusCode.NoContent, HttpMethod.Post, $"/api/instances/{outage.InstanceId}/cancel");

            // A 200 with a decision the workflow does not accept matches no boundary either.
            var oddId = NewOrderId();
            var odd = await admin.SendAsync<MessageStartAckDto>(HttpMethod.Post, route,
                OrderMessage(oddId, $"odd-{oddId[4..]}", expedited: false, new { sku = "KB-100", quantity = 1 }),
                StartHeaders($"request-{oddId}"));
            var oddIncident = Assert.Single(await UntilAsync(() => admin.IncidentsAsync(odd.InstanceId),
                rows => rows.Any(row => row.Status == "open"), "unexpected decision incident"));
            Assert.Equal(3, (await admin.AttemptsAsync(oddIncident.JobId)).Count);
            Assert.Equal("pending", Value(await admin.Workflows.GetInstanceAsync(odd.InstanceId), "creditDecision").GetString());
            await admin.ExpectStatusAsync(HttpStatusCode.NoContent, HttpMethod.Post, $"/api/instances/{odd.InstanceId}/cancel");

            // A shortage can also end the order.
            await using var warehouse = await CreateAsync(scenario, "warehouse-lead", "WarehouseLead");
            var shortId = NewOrderId();
            var shortOrder = await admin.SendAsync<MessageStartAckDto>(HttpMethod.Post, route,
                OrderMessage(shortId, $"good-{shortId[4..]}", expedited: false, new { sku = "OUT-900", quantity = 1 }),
                StartHeaders($"request-{shortId}"));
            await UntilAsync(() => admin.Workflows.GetInstanceAsync(shortOrder.InstanceId), AtNode(15), "stock shortage task");
            await warehouse.Workflows.TakeUserTaskFlowAsync((await SoleTaskAsync(warehouse, shortOrder.InstanceId, 15)).UserTaskId, 1502,
                Variables(("cancelReason", "Discontinued item.")));
            var cancelled = await admin.Workflows.GetInstanceAsync(shortOrder.InstanceId);
            Assert.Equal("completed", cancelled.Status);
            Assert.Equal(16, cancelled.Completion!.NodeId);
        });
    }

    private async Task<(WorkflowSummaryDto Child, WorkflowSummaryDto Order)> PublishOrderShowcaseAsync(ExampleApiClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var child = ReadModel("call-activities/01-manager-approval-child.json");
        child.Id = $"{child.Id}-{suffix}";
        var order = ReadModel(OrderPath);
        order.Id = $"{order.Id}-{suffix}";
        order.FlowNodes.Single(node => node.Type == BpmnFlowNodeTypes.CallActivity).CallActivity!.WorkflowKey = child.Id;
        var publishedChild = await admin.Workflows.CreateAndPublishAsync(child);
        return (publishedChild, await admin.Workflows.CreateAndPublishAsync(order));
    }

    private WorkflowModel ReadModel(string path) =>
        JsonSerializer.Deserialize<WorkflowModel>(File.ReadAllText(Example(path)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static Dictionary<string, JsonElement> PurchaseInputs(string requestNumber, decimal amount) => Variables(
        ("requestNumber", requestNumber),
        ("title", "Laptops for the design team"),
        ("category", "equipment"),
        ("amount", amount),
        ("managerUser", "pr-manager"));

    private static string NewRequestNumber() => $"PR-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static string NewOrderId() => $"ORD-{Guid.NewGuid().ToString("N")[..8]}";

    private static object OrderMessage(string orderId, string customerId, bool expedited, params object[] lines) => new
    {
        order = new { id = orderId, total = 1250.5, expedited, lines },
        customer = new { id = customerId },
    };

    private static IReadOnlyDictionary<string, string> StartHeaders(string requestKey) => new Dictionary<string, string>
    {
        ["X-Client-Id"] = ExampleAcceptanceFixture.MessageClientId,
        ["X-Client-Secret"] = ExampleAcceptanceFixture.MessageClientSecret,
        ["X-Flowbit-Correlation"] = "orders:inbound",
        ["Idempotency-Key"] = requestKey,
    };

    private static IReadOnlyDictionary<string, string> ShipmentHeaders(string reference, string deliveryKey) => new Dictionary<string, string>
    {
        ["X-Client-Id"] = ExampleAcceptanceFixture.MessageClientId,
        ["X-Client-Secret"] = ExampleAcceptanceFixture.MessageClientSecret,
        ["X-Shipment-Reference"] = reference,
        ["X-Flowbit-Delivery-Key"] = deliveryKey,
    };

    private static async Task<InboxItemDto> SoleTaskAsync(ExampleApiClient actor, long instanceId, int nodeId) =>
        Assert.Single(await actor.Workflows.GetInboxAsync(instanceId), item => item.CurrentNodeId == nodeId);

    private static Func<InstanceDetailDto, bool> AtNode(int nodeId) => detail =>
        detail.ExecutionPositions.Any(position => position.NodeId == nodeId && position.TokenStatus == "active");

    private static JsonElement Value(InstanceDetailDto detail, string name) =>
        detail.Variables.Where(variable => variable.VariableName == name).OrderByDescending(variable => variable.Id).First().Value;
}
