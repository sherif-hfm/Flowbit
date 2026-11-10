using System.Net;
using Flowbit.Shared.Dtos;
using Flowbit.Ui.Clients;
using Flowbit.Ui.Components.Shared;

namespace Flowbit.Ui.Components.Pages;

public partial class InstanceDetail
{
    private long? diagramPageInstanceId;
    private long? diagramDisplayedWorkflowId;
    private bool diagramHistoryEnabled;
    private InstanceDiagramHistoryDto? diagramHistory;
    private bool diagramHistoryLoading;
    private string? diagramHistoryError;
    private bool diagramHistoryRequiresInstanceRefresh;
    private string instanceUnavailableDescription = "Retry loading this instance.";
    private string? diagramNodeExecutionsError;
    private IReadOnlyList<NodeExecutionSummaryDto>? diagramHistoricalExecutions;
    private long diagramHistoricalTotal;
    private int diagramHistoricalPage = 1;
    private bool diagramHistoricalLoading;
    private string? diagramHistoricalError;
    private CancellationTokenSource? diagramSummaryRequest;
    private CancellationTokenSource? diagramHistoricalRequest;
    private CancellationTokenSource? diagramCurrentRequest;
    private long diagramSummaryGeneration;
    private long diagramHistoricalGeneration;
    private long diagramCurrentGeneration;

    private static void CancelDiagramRequest(ref CancellationTokenSource? request)
    {
        var previous = request;
        request = null;
        if (previous is null) return;
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        previous.Dispose();
    }

    private void ClearDiagramSummary()
    {
        ++diagramSummaryGeneration;
        CancelDiagramRequest(ref diagramSummaryRequest);
        diagramHistory = null;
        diagramHistoryLoading = false;
        diagramHistoryError = null;
        diagramHistoryRequiresInstanceRefresh = false;
    }

    private void MarkDiagramRefreshFailed()
    {
        if (!diagramHistoryEnabled) return;
        ClearDiagramSummary();
        diagramHistoryRequiresInstanceRefresh = true;
        diagramHistoryError = "Recorded history could not be refreshed. Retry to reload the instance and its history.";
    }

    private void ClearUnavailableInstance()
    {
        instance = null;
        ResetInstanceAuxiliaryState();
        ResetDiagramState(resetToggle: true, resetSelection: true);
    }

    private void ResetDiagramExecutionPage()
    {
        ++diagramHistoricalGeneration;
        CancelDiagramRequest(ref diagramHistoricalRequest);
        diagramHistoricalExecutions = null;
        diagramHistoricalTotal = 0;
        diagramHistoricalPage = 1;
        diagramHistoricalLoading = false;
        diagramHistoricalError = null;
    }

    private void ResetDiagramState(bool resetToggle, bool resetSelection)
    {
        ClearDiagramSummary();
        ResetDiagramExecutionPage();
        if (resetToggle) diagramHistoryEnabled = false;
        if (!resetSelection) return;
        ++diagramCurrentGeneration;
        ++diagramSelectionRevision;
        CancelDiagramRequest(ref diagramCurrentRequest);
        selectedDiagramNodeId = null;
        diagramNodeExecutions = null;
        diagramNodeExecutionTotal = 0;
        diagramNodeExecutionsLoading = false;
        diagramNodeExecutionsError = null;
    }

    private bool IsDiagramRequestCurrent(long id, long workflowId, long epoch) =>
        !disposed && instance?.Id == id && instance.Workflow.Id == workflowId
        && epoch == Interlocked.Read(ref identityEpoch);

    private bool IsPageRequestCurrent(long id, long epoch) =>
        !disposed && InstanceId == id && epoch == Interlocked.Read(ref identityEpoch);

    private async Task ApplyInstanceActionAsync(Func<long, Task<InstanceDetailDto?>> action)
    {
        var id = InstanceId;
        var epoch = Interlocked.Read(ref identityEpoch);
        var result = await action(id);
        if (IsPageRequestCurrent(id, epoch)) instance = result;
    }

    private async Task OnDiagramHistoryEnabledChangedAsync(bool enabled)
    {
        ResetDiagramState(resetToggle: false, resetSelection: false);
        diagramHistoryEnabled = enabled;
        if (!enabled) return;
        await RetryDiagramHistoryAsync();
    }

    private async Task RetryDiagramHistoryAsync()
    {
        var id = InstanceId;
        var epoch = Interlocked.Read(ref identityEpoch);
        var refreshInstance = diagramHistoryRequiresInstanceRefresh;
        var mismatch = !refreshInstance && await LoadDiagramHistoryAsync(force: true);
        if (!IsPageRequestCurrent(id, epoch) || !diagramHistoryEnabled) return;
        if (refreshInstance || mismatch)
        {
            // A summary can race a committed action. Reconcile once; continuous
            // churn remains a local retry state instead of a refresh loop.
            await refreshGate.WaitAsync(pollCancellation.Token);
            try
            {
                if (IsPageRequestCurrent(id, epoch) && diagramHistoryEnabled)
                {
                    error = null;
                    await RefreshInstanceStateAsync(pollCancellation.Token, allowHistoryReconcile: false);
                }
            }
            catch (OperationCanceledException) when (disposed) { }
            catch (Exception) when (!IsPageRequestCurrent(id, epoch) || !diagramHistoryEnabled) { }
            catch (Exception) { diagramHistoryError = "Recorded history could not be refreshed. Retry to load the latest instance."; }
            finally { refreshGate.Release(); }
        }
        else
        {
            await LoadDiagramHistoricalExecutionsAsync();
            await LoadDiagramNodeExecutionsAsync();
        }
    }

    // Returns true only for a current response whose revision does not match
    // displayed detail. Superseded/previous-identity responses do nothing.
    private async Task<bool> LoadDiagramHistoryAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (!diagramHistoryEnabled || instance is null || disposed) return false;
        if (!force && diagramHistory is not null && InstanceDiagramOverlay.Matches(instance, diagramHistory)) return false;
        ClearDiagramSummary();
        var generation = diagramSummaryGeneration;
        var id = instance.Id;
        var workflowId = instance.Workflow.Id;
        var epoch = Interlocked.Read(ref identityEpoch);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pollCancellation.Token);
        diagramSummaryRequest = request;
        diagramHistoryLoading = true;
        bool Current() => diagramHistoryEnabled && generation == diagramSummaryGeneration
            && IsDiagramRequestCurrent(id, workflowId, epoch);
        try
        {
            var result = await Api.GetInstanceDiagramHistoryAsync(id, request.Token);
            if (!Current()) return false;
            if (!InstanceDiagramOverlay.Matches(instance!, result))
            {
                diagramHistoryError = "The instance changed while recorded history was loading. Retry to load a matching snapshot.";
                return true;
            }
            diagramHistory = result;
            return false;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (!Current()) return false;
            diagramHistoryError = ex is WorkflowApiException api ? api.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Sign in again to load recorded history.",
                HttpStatusCode.Forbidden => "Recorded history is not available to this identity.",
                HttpStatusCode.NotFound => "Recorded history is unavailable. Refresh the instance to check whether it still exists.",
                _ => "Recorded history could not be loaded. Retry to request a new snapshot."
            } : "Recorded history could not be loaded. Retry to request a new snapshot.";
            if (IsDiagramPermissionFailure(ex)) ClearDiagramExecutionPermission();
            return false;
        }
        finally
        {
            if (Current()) diagramHistoryLoading = false;
            if (ReferenceEquals(diagramSummaryRequest, request)) diagramSummaryRequest = null;
        }
    }

    private async Task OnDiagramHistoryPageChangedAsync(int page)
    {
        if (!diagramHistoryEnabled || page < 1) return;
        diagramHistoricalPage = page;
        await LoadDiagramHistoricalExecutionsAsync();
        // Role-scoped searches can express permission loss as an empty 200.
        // Revalidate the separate latest-position lookup as well; never use
        // a historical page to choose a current execution link.
        await LoadDiagramNodeExecutionsAsync();
    }

    private async Task LoadDiagramHistoricalExecutionsAsync(CancellationToken cancellationToken = default, bool allowPageCorrection = true)
    {
        if (!diagramHistoryEnabled || instance is null || selectedDiagramNodeId is not int nodeId || disposed) return;
        CancelDiagramRequest(ref diagramHistoricalRequest);
        var generation = ++diagramHistoricalGeneration;
        var id = instance.Id;
        var workflowId = instance.Workflow.Id;
        var epoch = Interlocked.Read(ref identityEpoch);
        var pageNumber = diagramHistoricalPage;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pollCancellation.Token);
        diagramHistoricalRequest = request;
        diagramHistoricalExecutions = null;
        diagramHistoricalLoading = true;
        diagramHistoricalError = null;
        bool Current() => diagramHistoryEnabled && generation == diagramHistoricalGeneration
            && selectedDiagramNodeId == nodeId && diagramHistoricalPage == pageNumber
            && IsDiagramRequestCurrent(id, workflowId, epoch);
        try
        {
            var page = await Api.SearchNodeExecutionsAsync(new NodeExecutionSearchBodyRequest
            {
                InstanceId = id, WorkflowId = workflowId, NodeId = nodeId,
                Sort = [new SearchSortDto("id", "desc")], Page = pageNumber, PageSize = 50
            }, request.Token);
            if (!Current()) return;
            diagramHistoricalExecutions = page.Items;
            diagramHistoricalTotal = page.TotalCount;
            // Retention may empty a later page; return to a valid bounded page.
            if (page.Items.Count == 0 && pageNumber > 1)
            {
                var corrected = page.TotalCount == 0 ? 1
                    : (int)Math.Min(int.MaxValue, (page.TotalCount - 1) / 50 + 1);
                if (allowPageCorrection && corrected != pageNumber)
                {
                    diagramHistoricalPage = corrected;
                    await LoadDiagramHistoricalExecutionsAsync(cancellationToken, allowPageCorrection: false);
                }
                else
                {
                    diagramHistoricalExecutions = null;
                    diagramHistoricalError = "Execution records changed while this page was loading. Retry execution details.";
                }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!Current()) return;
            diagramHistoricalExecutions = null;
            diagramHistoricalTotal = 0;
            diagramHistoricalError = ExecutionLookupError(ex);
            if (IsDiagramPermissionFailure(ex)) ClearCurrentExecutionPermission();
        }
        finally
        {
            if (Current()) diagramHistoricalLoading = false;
            if (ReferenceEquals(diagramHistoricalRequest, request)) diagramHistoricalRequest = null;
        }
    }

    private static bool IsDiagramPermissionFailure(Exception exception) => exception is WorkflowApiException
        { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized };

    private static string ExecutionLookupError(Exception exception) => IsDiagramPermissionFailure(exception)
        ? "Execution details are not available to this identity."
        : "Execution details could not be loaded. Retry or refresh the instance.";

    private void ClearCurrentExecutionPermission()
    {
        ++diagramCurrentGeneration;
        CancelDiagramRequest(ref diagramCurrentRequest);
        diagramNodeExecutions = [];
        diagramNodeExecutionTotal = 0;
        diagramNodeExecutionsLoading = false;
        diagramNodeExecutionsError = "Execution details are not available to this identity.";
    }

    private void ClearHistoricalExecutionPermission()
    {
        ++diagramHistoricalGeneration;
        CancelDiagramRequest(ref diagramHistoricalRequest);
        diagramHistoricalExecutions = null;
        diagramHistoricalTotal = 0;
        diagramHistoricalLoading = false;
        diagramHistoricalError = "Execution details are not available to this identity.";
    }

    private void ClearDiagramExecutionPermission()
    {
        ClearCurrentExecutionPermission();
        ClearHistoricalExecutionPermission();
    }
}
