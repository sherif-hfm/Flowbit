using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

public sealed partial class WorkflowApiClient
{
    public Task<PagedResult<LocalUserDto>> GetUsersAsync(string search = "", int page = 1, string active = "")
        => IdentityReadAsync<PagedResult<LocalUserDto>>($"/api/users?search={Uri.EscapeDataString(search)}&page={page}&pageSize=25{(active == "" ? "" : "&active=" + active)}");
    public Task<LocalUserDto> CreateUserAsync(CreateLocalUserRequest request) => IdentityWriteAsync<LocalUserDto>(HttpMethod.Post, "/api/users", request);
    public Task<LocalUserDto> UpdateUserAsync(long id, UpdateLocalUserRequest request) => IdentityWriteAsync<LocalUserDto>(HttpMethod.Put, $"/api/users/{id}", request);
    public Task<LocalUserDto> SetUserStatusAsync(long id, SetLocalUserStatusRequest request) => IdentityWriteAsync<LocalUserDto>(HttpMethod.Post, $"/api/users/{id}/status", request);
    public Task ResetUserPasswordAsync(long id, ResetLocalPasswordRequest request) => IdentityCommandAsync($"/api/users/{id}/password", request);
    public Task ChangeLocalPasswordAsync(ChangeLocalPasswordRequest request) => IdentityCommandAsync("/api/auth/change-password", request);
    public Task<UserAttributeDto[]> GetUserAttributesAsync() => IdentityReadAsync<UserAttributeDto[]>("/api/user-attributes");
    public Task<UserAttributeDto> SaveUserAttributeAsync(long? id, SaveUserAttributeRequest request)
        => IdentityWriteAsync<UserAttributeDto>(id is null ? HttpMethod.Post : HttpMethod.Put, "/api/user-attributes" + (id is null ? "" : $"/{id}"), request);
    private async Task<T> IdentityReadAsync<T>(string path)
    {
        using var response = await httpClient.GetAsync(path);
        await EnsureSuccessAsync(response, default);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private async Task<T> IdentityWriteAsync<T>(HttpMethod method, string path, object body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, body.GetType()) };
        using var response = await httpClient.SendAsync(request);
        await EnsureSuccessAsync(response, default);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private async Task IdentityCommandAsync(string path, object body)
    {
        using var response = await httpClient.PostAsync(path, JsonContent.Create(body, body.GetType()));
        await EnsureSuccessAsync(response, default);
    }
}
