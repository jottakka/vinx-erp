namespace Vinx.Erp.ApiHost.Models;

public sealed record NotificationInboxItemResponse(
    Guid Id,
    Guid CompanyId,
    string Source,
    string? Event,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    NotificationInboxActionResponse? Action
);

public sealed record NotificationInboxActionResponse(string Kind, string Href);

public sealed record NotificationInboxPageResponse(
    IReadOnlyList<NotificationInboxItemResponse> Items,
    string? NextCursor
);

public sealed record NotificationUnreadCountResponse(long UnreadCount);

public sealed record NotificationMarkReadResponse(string Status);
