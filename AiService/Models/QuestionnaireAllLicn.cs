using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace AiService.Models;

[Keyless]
[Table("QUESTIONNAIRE_ALL_LICNS")]
public partial class QuestionnaireAllLicn
{
    [Column("LICN_ID", TypeName = "NUMBER")]
    public decimal? LicnId { get; set; }

    [Column("QUESTIONNAIRE_DATA_RECID", TypeName = "NUMBER")]
    public decimal? QuestionnaireDataRecid { get; set; }

    [Column("LIC_CIVIL_ID")]
    [StringLength(500)]
    [Unicode(false)]
    public string? LicCivilId { get; set; }

    [Column("LICN_COMM_NO")]
    [StringLength(500)]
    [Unicode(false)]
    public string? LicnCommNo { get; set; }

    [Column("COMPANY_NAME")]
    [Unicode(false)]
    public string? CompanyName { get; set; }

    [Column("LICN_COMM_EDATE", TypeName = "DATE")]
    public DateTime? LicnCommEdate { get; set; }

    [Column("LICN_TYPE_DESC")]
    [StringLength(500)]
    [Unicode(false)]
    public string? LicnTypeDesc { get; set; }

    [Column("ACTIVITY_DESC")]
    [Unicode(false)]
    public string? ActivityDesc { get; set; }

    [Column("CREATED_ON", TypeName = "DATE")]
    public DateTime? CreatedOn { get; set; }

    [Column("CREATED_BY")]
    [StringLength(500)]
    [Unicode(false)]
    public string? CreatedBy { get; set; }
}
