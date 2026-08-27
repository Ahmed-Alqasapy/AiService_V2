using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace AiService.Models;

public partial class MyDbContext : DbContext
{
    public MyDbContext()
    {
    }

    public MyDbContext(DbContextOptions<MyDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<QuestionnaireAllLicn> QuestionnaireAllLicns { get; set; }

    public virtual DbSet<QuestionnaireDatum> QuestionnaireData { get; set; }

    public virtual DbSet<QuestionnaireLicAct> QuestionnaireLicActs { get; set; }

    public virtual DbSet<QuestionnaireLicOwner> QuestionnaireLicOwners { get; set; }

    public virtual DbSet<QuestionnaireReqTransAction> QuestionnaireReqTransActions { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
        => optionsBuilder.UseOracle("User Id=APP_AML;Password=BUbu67C4x34gvvmh;Data Source=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=10.21.1.169)(PORT=1521)))(CONNECT_DATA=(SERVICE_NAME=awsdevdb)));");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasDefaultSchema("APP_AML")
            .UseCollation("USING_NLS_COMP");

        modelBuilder.Entity<QuestionnaireAllLicn>(entity =>
        {
            entity.Property(e => e.LicnId).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<QuestionnaireDatum>(entity =>
        {
            entity.Property(e => e.ReqStatus).HasDefaultValueSql("1");
        });

        modelBuilder.Entity<QuestionnaireLicAct>(entity =>
        {
            entity.Property(e => e.ReqActId).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<QuestionnaireLicOwner>(entity =>
        {
            entity.Property(e => e.CivilId).ValueGeneratedOnAdd();
            entity.Property(e => e.CivilNationId).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.CivilNationName).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.CivilTypeId).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.CivilTypeName).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.CreatedBy).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.LicCivilId).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.LicnRecId).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.OwnerMemberDescId).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.OwnerMemberDescName).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.OwnerName).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.QuestionnaireDataRecid).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
            entity.Property(e => e.ReqOwnId).HasDefaultValueSql("AML_LICN_OWNERS_SEQ.NEXTVAL");
        });
        modelBuilder.HasSequence("AEZ_BY_CIVL_ID_WS_LOG_SEQ");
        modelBuilder.HasSequence("AML_ACTIVITY_SEQUENCE");
        modelBuilder.HasSequence("AML_COM_CON_SEQ");
        modelBuilder.HasSequence("AML_EVENT_ATTENDENCE_SEQ");
        modelBuilder.HasSequence("AML_LICN_EXTRA_ACTV_SEQ");
        modelBuilder.HasSequence("AML_LICN_EXTRA_ADRS_SEQ");
        modelBuilder.HasSequence("AML_LICN_OWNERS_SEQ");
        modelBuilder.HasSequence("AML_LICN_REQ_DOCS_SEQ");
        modelBuilder.HasSequence("AML_LICN_REQ_EXTRA_ACTV_SEQ");
        modelBuilder.HasSequence("AML_LICN_REQ_EXTRA_ADRS_SEQ");
        modelBuilder.HasSequence("AML_LICN_REQ_OWNERS_SEQ");
        modelBuilder.HasSequence("AML_LICN_REQ_PARENT_ACTV_SEQ");
        modelBuilder.HasSequence("AML_LICN_REQ_SEQUENCE");
        modelBuilder.HasSequence("AML_LICN_SEQUENCE");
        modelBuilder.HasSequence("AML_NOTIFICATION_SEQ");
        modelBuilder.HasSequence("AUDIT_ATTACH_SEQ");
        modelBuilder.HasSequence("AUDIT_ID_SEQ");
        modelBuilder.HasSequence("AUDIT_LOG_ID_SEQ");
        modelBuilder.HasSequence("COM_ATTACH_ID_SEQ");
        modelBuilder.HasSequence("COMP_REQUEST_ID");
        modelBuilder.HasSequence("COMPLIANCE_ID_SEQ");
        modelBuilder.HasSequence("COMPLIANCE_OFFICER_REQ_LOG_ID");
        modelBuilder.HasSequence("COMPLIANCE_REQ_SEQ");
        modelBuilder.HasSequence("DOC_ID_SEQ");
        modelBuilder.HasSequence("EVAL_POINTS_ID_SEQ");
        modelBuilder.HasSequence("INSPECT_COMMITMENT_SEQ");
        modelBuilder.HasSequence("INSPECT_DATA_SEQ");
        modelBuilder.HasSequence("INSPECT_LIC_ACT_SEQ");
        modelBuilder.HasSequence("INSPECT_LIC_OWNERS_SEQ");
        modelBuilder.HasSequence("INSPECT_LICNS_BENEFICIARIE_SEQ");
        modelBuilder.HasSequence("INSPECT_PENALITIES_SEQ");
        modelBuilder.HasSequence("INSPECT_VIOLATIONS_SEQ");
        modelBuilder.HasSequence("INSPECTIONS_LAWS_DATA_SEQ");
        modelBuilder.HasSequence("INSPECTIONS_LOOKUPS_DATA_SEQ");
        modelBuilder.HasSequence("INSPECTIONS_VIOLATIONS_DAT_SEQ");
        modelBuilder.HasSequence("LEGAL_EVAL_ACTION_ID_SEQ");
        modelBuilder.HasSequence("LEGAL_EVAL_ACTIONS_LOG_ID_SEQ");
        modelBuilder.HasSequence("LEGAL_EVAL_ID_SEQ");
        modelBuilder.HasSequence("LEGAL_LOG_ID_SEQ");
        modelBuilder.HasSequence("NOTE_ID_SEQ");
        modelBuilder.HasSequence("ORD_ID_SEQ");
        modelBuilder.HasSequence("QUESTIONNAIRE_ALL_LICNS_SEQ");
        modelBuilder.HasSequence("QUESTIONNAIRE_LIC_ACT_SEQ");
        modelBuilder.HasSequence("QUESTIONNAIRE_LIC_OWNERS_SEQ");
        modelBuilder.HasSequence("REQ_TRANS_ATTACH_SEQ");
        modelBuilder.HasSequence("REQ_TRANS_SEQ");
        modelBuilder.HasSequence("RETURN_LOG_ID_SEQ");
        modelBuilder.HasSequence("SEQ_INSPECT_PENALITY_ID");
        modelBuilder.HasSequence("SEQ_INSPECT_VIOLATION_ID");
        modelBuilder.HasSequence("UPDATED_ACTION_FINE_VAL_ID_SEQ");
        modelBuilder.HasSequence("WLOG_ID_SEQ");
        modelBuilder.HasSequence("WORKORDERS_LOG_ID_SEQ");

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
