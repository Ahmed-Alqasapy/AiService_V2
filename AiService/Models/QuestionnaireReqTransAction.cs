using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace AiService.Models;

[Keyless]
[Table("QUESTIONNAIRE_REQ_TRANS_ACTION")]
public partial class QuestionnaireReqTransAction
{
    [Column("ID", TypeName = "NUMBER")]
    public decimal? Id { get; set; }

    [Column("AR_DESC")]
    [StringLength(300)]
    [Unicode(false)]
    public string? ArDesc { get; set; }

    [Column("EN_DESC")]
    [StringLength(300)]
    [Unicode(false)]
    public string? EnDesc { get; set; }

    [Column("TRANS_TYPE", TypeName = "NUMBER")]
    public decimal? TransType { get; set; }

    [Column("REQ_TYPE", TypeName = "NUMBER")]
    public decimal? ReqType { get; set; }

    [Column("STATUS")]
    [StringLength(5)]
    [Unicode(false)]
    public string? Status { get; set; }

    [Column("CREATED_BY")]
    [StringLength(50)]
    [Unicode(false)]
    public string? CreatedBy { get; set; }

    [Column("CREATED_DT", TypeName = "DATE")]
    public DateTime? CreatedDt { get; set; }

    [Column("ACTION_NOTES")]
    [StringLength(500)]
    [Unicode(false)]
    public string? ActionNotes { get; set; }
}
