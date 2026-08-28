using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Persistence.Configurations;

public sealed class AtendimentoCardParticipantConfiguration
    : IEntityTypeConfiguration<AtendimentoCardParticipant>
{
    public void Configure(EntityTypeBuilder<AtendimentoCardParticipant> builder)
    {
        builder.ToTable("atendimento_card_participants", "service_desk");
        builder.HasKey(participant => new
        {
            participant.CompanyId,
            participant.CardId,
            participant.MembershipId,
        });
        builder.Property(participant => participant.CompanyId).HasColumnName("company_id");
        builder.Property(participant => participant.CardId).HasColumnName("card_id");
        builder.Property(participant => participant.MembershipId).HasColumnName("membership_id");
        builder.HasOne(participant => participant.Card)
            .WithMany(card => card.Participants)
            .HasForeignKey(participant => new { participant.CompanyId, participant.CardId })
            .HasPrincipalKey(card => new { card.CompanyId, card.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(participant => new { participant.CompanyId, participant.MembershipId })
            .HasPrincipalKey(membership => new { membership.CompanyId, membership.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
