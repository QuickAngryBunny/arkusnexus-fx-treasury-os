using Google.Api.Gax.ResourceNames;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Grpc.Core;

namespace FxOs.Platform.Secrets;

/// <summary>
/// Tenant database credentials in Google Secret Manager (hosting on Firebase / Google Cloud). One secret per ref
/// (e.g. <c>tenant-db-acme</c>), automatic replication; a new value is a new version, reads take the latest.
/// Credentials come from Application Default Credentials (the Cloud Run service account, which needs
/// Secret Manager Admin on the project, or Secret Accessor plus Secret Version Adder on <c>tenant-db-*</c>).
/// </summary>
public sealed class GoogleSecretManagerStore(SecretManagerServiceClient client, string projectId) : ISecretStore
{
    public async Task<string?> GetAsync(string secretRef, CancellationToken ct = default)
    {
        try
        {
            var version = new SecretVersionName(projectId, secretRef, "latest");
            var response = await client.AccessSecretVersionAsync(version, ct);
            return response.Payload.Data.ToStringUtf8();
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task SetAsync(string secretRef, string value, CancellationToken ct = default)
    {
        var name = new SecretName(projectId, secretRef);
        try
        {
            await client.CreateSecretAsync(new ProjectName(projectId), secretRef,
                new Secret { Replication = new Replication { Automatic = new Replication.Types.Automatic() } }, ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.AlreadyExists)
        {
        }

        await client.AddSecretVersionAsync(name, new SecretPayload { Data = ByteString.CopyFromUtf8(value) }, ct);
    }

    public async Task DeleteAsync(string secretRef, CancellationToken ct = default)
    {
        try
        {
            await client.DeleteSecretAsync(new SecretName(projectId, secretRef), ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
        }
    }
}
