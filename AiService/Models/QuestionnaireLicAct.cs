using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace AiService.Models;

[Keyless]
[Table("QUESTIONNAIRE_LIC_ACT")]
public partial class QuestionnaireLicAct
{
    [Column("REQ_ACT_ID", TypeName = "NUMBER")]
    public decimal? ReqActId { get; set; }

    [Column("QUESTIONNAIRE_DATA_RECID", TypeName = "NUMBER")]
    public decimal? QuestionnaireDataRecid { get; set; }

    [Column("LICN_REC_ID", TypeName = "NUMBER")]
    public decimal? LicnRecId { get; set; }

    [Column("LIC_CIVIL_ID", TypeName = "NUMBER")]
    public decimal? LicCivilId { get; set; }

    [Column("ACTIVE_PURPOSE")]
    [StringLength(3000)]
    [Unicode(false)]
    public string? ActivePurpose { get; set; }

    [Column("ACTIVITY_PURPOSE_CODE", TypeName = "NUMBER")]
    public decimal? ActivityPurposeCode { get; set; }

    [Column("ACTIVITY_PURPOSE_DESC")]
    [StringLength(500)]
    [Unicode(false)]
    public string? ActivityPurposeDesc { get; set; }

    [Column("ACT_CODE_INTL")]
    [StringLength(500)]
    [Unicode(false)]
    public string? ActCodeIntl { get; set; }

    [Column("ACT_DESC_INTL")]
    [StringLength(2500)]
    [Unicode(false)]
    public string? ActDescIntl { get; set; }

    [Column("CREATED_ON", TypeName = "DATE")]
    public DateTime? CreatedOn { get; set; }

    [Column("CREATED_BY")]
    [StringLength(500)]
    [Unicode(false)]
    public string? CreatedBy { get; set; }
}
