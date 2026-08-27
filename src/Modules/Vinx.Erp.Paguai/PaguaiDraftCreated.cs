namespace Vinx.Erp.Paguai;

/// <summary>Identifica a criação confirmada de um rascunho sem transportar conteúdo financeiro ou pessoal.</summary>
public sealed record PaguaiDraftCreated(Guid CompanyId, Guid DraftId, Guid RequesterUserId);
