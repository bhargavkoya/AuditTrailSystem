using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Events;
using AuditFlow.Workflow.Api.Domain;

namespace AuditFlow.Workflow.Api.Handlers;

public sealed class EngagementCreatedHandler(ProvisioningService provisioning) : IEventHandler<EngagementCreatedPayload>
{
    public Task HandleAsync(EventEnvelope<EngagementCreatedPayload> envelope, IUnitOfWork uow, CancellationToken cancellationToken)
        => provisioning.ProvisionAsync(envelope, uow, cancellationToken);
}
