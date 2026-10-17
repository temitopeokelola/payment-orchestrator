using PaymentOrchestrator.Domain.Common;

namespace PaymentOrchestrator.Domain.Payments;

public sealed record PaymentCreated(Guid PaymentId, string MerchantId, Money Amount, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentAuthorized(Guid PaymentId, string Provider, string ProviderReference, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentCaptured(Guid PaymentId, string MerchantId, string Provider, Money Amount, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentRefunded(Guid PaymentId, string MerchantId, string Provider, Money Amount, Money TotalRefunded, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentFailed(Guid PaymentId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentCancelled(Guid PaymentId, DateTimeOffset OccurredAt) : IDomainEvent;
