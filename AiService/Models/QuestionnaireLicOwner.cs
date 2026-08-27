using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace AiService.Models;

[Keyless]
[Table("QUESTIONNAIRE_LIC_OWNERS")]
public partial class QuestionnaireLicOwner
{
    [Column("REQ_OWN_ID", TypeName = "NUMBER")]
    public decimal? ReqOwnId { get; set; }

    [Column("QUESTIONNAIRE_DATA_RECID", TypeName = "NUMBER")]
    public decimal? QuestionnaireDataRecid { get; set; }

    [Column("LIC_CIVIL_ID", TypeName = "NUMBER")]
    public decimal? LicCivilId { get; set; }

    [Column("LICN_REC_ID", TypeName = "NUMBER")]
    public decimal? LicnRecId { get; set; }

    [Column("CIVIL_ID")]
    [StringLength(500)]
    [Unicode(false)]
    public string? CivilId { get; set; }

    [Column("OWNER_NAME")]
    [StringLength(500)]
    [Unicode(false)]
    public string? OwnerName { get; set; }

    [Column("OWNER_MEMBER_DESC_ID", TypeName = "NUMBER")]
    public decimal? OwnerMemberDescId { get; set; }

    [Column("OWNER_MEMBER_DESC_NAME")]
    [StringLength(500)]
    [Unicode(false)]
    public string? OwnerMemberDescName { get; set; }

    [Column("CIVIL_TYPE_ID", TypeName = "NUMBER")]
    public decimal? CivilTypeId { get; set; }

    [Column("CIVIL_TYPE_NAME")]
    [StringLength(500)]
    [Unicode(false)]
    public string? CivilTypeName { get; set; }

    [Column("CIVIL_NATION_ID", TypeName = "NUMBER")]
    public decimal? CivilNationId { get; set; }

    [Column("CIVIL_NATION_NAME")]
    [StringLength(500)]
    [Unicode(false)]
    public string? CivilNationName { get; set; }

    [Column("CREATED_ON", TypeName = "DATE")]
    public DateTime? CreatedOn { get; set; }

    [Column("CREATED_BY")]
    [StringLength(500)]
    [Unicode(false)]
    public string? CreatedBy { get; set; }
}
