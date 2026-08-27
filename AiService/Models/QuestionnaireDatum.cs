using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace AiService.Models;

[Keyless]
[Table("QUESTIONNAIRE_DATA")]
public partial class QuestionnaireDatum
{
    [Key]
    [Column("QUESTIONNAIRE_DATA_RECID", TypeName = "NUMBER")]
    public decimal? QuestionnaireDataRecid { get; set; }

    [Column("PERM_ID", TypeName = "NUMBER")]
    public decimal? PermId { get; set; }

    [Column("QUESTIONNAIRE_TYPE_ID", TypeName = "NUMBER")]
    public decimal? QuestionnaireTypeId { get; set; }

    [Column("QUESTIONNAIRE_NO")]
    [StringLength(50)]
    [Unicode(false)]
    public string? QuestionnaireNo { get; set; }

    [Column("QUESTIONNAIRE_DATE", TypeName = "DATE")]
    public DateTime? QuestionnaireDate { get; set; }

    [Column("COMP_CIVILID")]
    [StringLength(200)]
    [Unicode(false)]
    public string? CompCivilid { get; set; }

    [Column("COMP_NAME")]
    [Unicode(false)]
    public string? CompName { get; set; }

    [Column("COMP_CENTRAL_NO")]
    [StringLength(200)]
    [Unicode(false)]
    public string? CompCentralNo { get; set; }

    [Column("LICN_NO")]
    [StringLength(100)]
    [Unicode(false)]
    public string? LicnNo { get; set; }

    [Column("COMM_BOOK_NO")]
    [StringLength(100)]
    [Unicode(false)]
    public string? CommBookNo { get; set; }

    [Column("ADDRESS_AUTO_NO")]
    [StringLength(200)]
    [Unicode(false)]
    public string? AddressAutoNo { get; set; }

    [Column("MAIN_ADDRESS_NO")]
    [Unicode(false)]
    public string? MainAddressNo { get; set; }

    [Column("ISSUE_DATE", TypeName = "DATE")]
    public DateTime? IssueDate { get; set; }

    [Column("EXPIERY_DATE", TypeName = "DATE")]
    public DateTime? ExpieryDate { get; set; }

    [Column("LICN_STATUS")]
    [StringLength(200)]
    [Unicode(false)]
    public string? LicnStatus { get; set; }

    [Column("WORK_START_DATE", TypeName = "DATE")]
    public DateTime? WorkStartDate { get; set; }

    [Column("COMP_PHONE_NO")]
    [StringLength(200)]
    [Unicode(false)]
    public string? CompPhoneNo { get; set; }

    [Column("COMP_EMAIL")]
    [StringLength(500)]
    [Unicode(false)]
    public string? CompEmail { get; set; }

    [Column("KUWAIT_BRANCH_COUNT", TypeName = "NUMBER")]
    public decimal? KuwaitBranchCount { get; set; }

    [Column("OVERSEAS_BRANCH_COUNT", TypeName = "NUMBER")]
    public decimal? OverseasBranchCount { get; set; }

    [Column("OVERSEAS_BRANCH_LOCATIONS", TypeName = "CLOB")]
    public string? OverseasBranchLocations { get; set; }

    [Column("IS_BRANCH_OF_INTERNATIONAL", TypeName = "NUMBER")]
    public decimal? IsBranchOfInternational { get; set; }

    [Column("MAIN_COM_LOCATION")]
    [Unicode(false)]
    public string? MainComLocation { get; set; }

    [Column("YEAR_FROM")]
    [Precision(4)]
    public byte? YearFrom { get; set; }

    [Column("YEAR_TO")]
    [Precision(4)]
    public byte? YearTo { get; set; }

    [Column("TOTAL_TRANS_ON_LAST_YEAR", TypeName = "NUMBER(18,2)")]
    public decimal? TotalTransOnLastYear { get; set; }

    [Column("EMPLOYEE_COUNT", TypeName = "NUMBER")]
    public decimal? EmployeeCount { get; set; }

    [Column("REVENUE_VALUE_TOYEAR", TypeName = "NUMBER(18,2)")]
    public decimal? RevenueValueToyear { get; set; }

    [Column("SALES_COUNT_OVER_1M", TypeName = "NUMBER")]
    public decimal? SalesCountOver1m { get; set; }

    [Column("SALES_COUNT_OVER_5M", TypeName = "NUMBER")]
    public decimal? SalesCountOver5m { get; set; }

    [Column("RESTATE_VAL_ON_TOYEAR", TypeName = "NUMBER(18,2)")]
    public decimal? RestateValOnToyear { get; set; }

    [Column("TRANS_COUNT_CASH_DEPOSIT_TOYEAR", TypeName = "NUMBER")]
    public decimal? TransCountCashDepositToyear { get; set; }

    [Column("RECEIVE_CASH_FLAG", TypeName = "NUMBER(1)")]
    public bool? ReceiveCashFlag { get; set; }

    [Column("CASH_VALUE_TOYEAR", TypeName = "NUMBER(18,2)")]
    public decimal? CashValueToyear { get; set; }

    [Column("PERIOD_DIGITAL_CURRENCY_FLAG", TypeName = "NUMBER(1)")]
    public bool? PeriodDigitalCurrencyFlag { get; set; }

    [Column("PERIOD_DIGITAL_CURRENCY_VALUE_KD", TypeName = "NUMBER(18,2)")]
    public decimal? PeriodDigitalCurrencyValueKd { get; set; }

    [Column("PERIOD_ONLINE_TRANS_FLAG", TypeName = "NUMBER(1)")]
    public bool? PeriodOnlineTransFlag { get; set; }

    [Column("PERIOD_ONLINE_TRANS_COUNT", TypeName = "NUMBER")]
    public decimal? PeriodOnlineTransCount { get; set; }

    [Column("PERIOD_FOREIGN_MONEY_FLAG", TypeName = "NUMBER(1)")]
    public bool? PeriodForeignMoneyFlag { get; set; }

    [Column("PERIOD_FOREIGN_MONEY_VALUE_KD", TypeName = "NUMBER(18,2)")]
    public decimal? PeriodForeignMoneyValueKd { get; set; }

    [Column("FOREIGN_TRANS_FLAG", TypeName = "NUMBER(1)")]
    public bool? ForeignTransFlag { get; set; }

    [Column("FOREIGN_TRANS_DETAILS", TypeName = "CLOB")]
    public string? ForeignTransDetails { get; set; }

    [Column("CUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? CustomerCountToyear { get; set; }

    [Column("KUWAIT_RESIDENT_COUNT_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? KuwaitResidentCountCountToyear { get; set; }

    [Column("NON_KUWAIT_RESIDENT_COUNT_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? NonKuwaitResidentCountCountToyear { get; set; }

    [Column("KUWAIT_LEGAL_ENTITY_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? KuwaitLegalEntityCountToyear { get; set; }

    [Column("NON_KUWAIT_LEGAL_ENTITY_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? NonKuwaitLegalEntityCountToyear { get; set; }

    [Column("POLITICAL_CUSTOMERS_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? PoliticalCustomersCountToyear { get; set; }

    [Column("RICH_CUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? RichCustomerCountToyear { get; set; }

    [Column("BLACK_LIST_COUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? BlackListCoustomerCountToyear { get; set; }

    [Column("GRAY_LIST_COUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? GrayListCoustomerCountToyear { get; set; }

    [Column("TWENTY_LIST_COUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? TwentyListCoustomerCountToyear { get; set; }

    [Column("HAVE_WRITTERN_POLICY_FLAG", TypeName = "NUMBER(1)")]
    public bool? HaveWritternPolicyFlag { get; set; }

    [Column("HAVE_WRITTEN_POLICY_FILE_NAME")]
    [Unicode(false)]
    public string? HaveWrittenPolicyFileName { get; set; }

    [Column("RISK_ASSIGMENT_FLAG", TypeName = "NUMBER(1)")]
    public bool? RiskAssigmentFlag { get; set; }

    [Column("RISK_ASSIGGMENT_DATE", TypeName = "DATE")]
    public DateTime? RiskAssiggmentDate { get; set; }

    [Column("RISK_ASSIGGMENT_FILE_NAME")]
    [Unicode(false)]
    public string? RiskAssiggmentFileName { get; set; }

    [Column("CUSTOMER_RISK_FILE_NAME")]
    [Unicode(false)]
    public string? CustomerRiskFileName { get; set; }

    [Column("LEGAL_ENTITY_BENEFICARY_STEPS", TypeName = "CLOB")]
    public string? LegalEntityBeneficarySteps { get; set; }

    [Column("RISK_ASSIGGMENT_FILE_KEEP_FLAG", TypeName = "NUMBER(1)")]
    public bool? RiskAssiggmentFileKeepFlag { get; set; }

    [Column("UPDATE_CUSTOMER_RISK_FLAG", TypeName = "NUMBER(1)")]
    public bool? UpdateCustomerRiskFlag { get; set; }

    [Column("POLITICAL_CUSTOMERS_SYSTEM_FLAG", TypeName = "NUMBER(1)")]
    public bool? PoliticalCustomersSystemFlag { get; set; }

    [Column("POLITICAL_CUSTOMERS_SYSTEM_DETAILS", TypeName = "CLOB")]
    public string? PoliticalCustomersSystemDetails { get; set; }

    [Column("AML_BOOK_KEEPING_FLAG", TypeName = "NUMBER(1)")]
    public bool? AmlBookKeepingFlag { get; set; }

    [Column("AML_BOOK_KEEPING_TYPE_ID", TypeName = "NUMBER")]
    public decimal? AmlBookKeepingTypeId { get; set; }

    [Column("AML_BOOK_KEEPING_PERIOD_INYEARS", TypeName = "NUMBER")]
    public decimal? AmlBookKeepingPeriodInyears { get; set; }

    [Column("AML_PROCESS_FLAG", TypeName = "NUMBER(1)")]
    public bool? AmlProcessFlag { get; set; }

    [Column("AML_PROCESS_TYPE_ID", TypeName = "NUMBER")]
    public decimal? AmlProcessTypeId { get; set; }

    [Column("AML_PROCESS_DETAILS", TypeName = "CLOB")]
    public string? AmlProcessDetails { get; set; }

    [Column("COMPLIENCE_OFFICER_FLAG", TypeName = "NUMBER(1)")]
    public bool? ComplienceOfficerFlag { get; set; }

    [Column("AML_EMP_TRAINING_FLAG", TypeName = "NUMBER(1)")]
    public bool? AmlEmpTrainingFlag { get; set; }

    [Column("AML_EMP_TRAINING_TYPE_ID", TypeName = "NUMBER")]
    public decimal? AmlEmpTrainingTypeId { get; set; }

    [Column("AML_EMP_TRAINING_COUNT", TypeName = "NUMBER")]
    public decimal? AmlEmpTrainingCount { get; set; }

    [Column("AML_EMP_TRAINING_SUBJECTS", TypeName = "CLOB")]
    public string? AmlEmpTrainingSubjects { get; set; }

    [Column("AML_TRANS_REPORT_FLAG", TypeName = "NUMBER(1)")]
    public bool? AmlTransReportFlag { get; set; }

    [Column("AML_TRANS_REPORT_COUNT", TypeName = "NUMBER")]
    public decimal? AmlTransReportCount { get; set; }

    [Column("COMPLIENCE_MANAGER_ID")]
    [StringLength(200)]
    [Unicode(false)]
    public string? ComplienceManagerId { get; set; }

    [Column("COMPLIENCE_MANAGER_NAME")]
    [Unicode(false)]
    public string? ComplienceManagerName { get; set; }

    [Column("COMPLIENCE_OFFICER_ID")]
    [StringLength(200)]
    [Unicode(false)]
    public string? ComplienceOfficerId { get; set; }

    [Column("COMPLIENCE_OFFICER_NAME")]
    [Unicode(false)]
    public string? ComplienceOfficerName { get; set; }

    [Column("SELL_GOLD_BARS_FLAG", TypeName = "NUMBER(1)")]
    public bool? SellGoldBarsFlag { get; set; }

    [Column("SELL_EXPENSIVE_WATCHES_FLAG", TypeName = "NUMBER(1)")]
    public bool? SellExpensiveWatchesFlag { get; set; }

    [Column("KNET_DEVICES_FLAG", TypeName = "NUMBER(1)")]
    public bool? KnetDevicesFlag { get; set; }

    [Column("KNET_DEVICES_TYPES")]
    [StringLength(2000)]
    [Unicode(false)]
    public string? KnetDevicesTypes { get; set; }

    [Column("ALL_TRANS_VALUE_KD_TOYEAR", TypeName = "NUMBER(18,2)")]
    public decimal? AllTransValueKdToyear { get; set; }

    [Column("PUR_TRANS_VALUE_KD_TOYEAR", TypeName = "NUMBER(18,2)")]
    public decimal? PurTransValueKdToyear { get; set; }

    [Column("CASH_SELL_TRANS_PRC_TOYEAR", TypeName = "NUMBER(18,2)")]
    public decimal? CashSellTransPrcToyear { get; set; }

    [Column("SELL_TRANS_VALUE_TOYEAR", TypeName = "NUMBER(18,2)")]
    public decimal? SellTransValueToyear { get; set; }

    [Column("SELL_TRANS_COUNT_3000_TOYEAR", TypeName = "NUMBER")]
    public decimal? SellTransCount3000Toyear { get; set; }

    [Column("SELL_TRANS_COUNT_5000_TOYEAR", TypeName = "NUMBER")]
    public decimal? SellTransCount5000Toyear { get; set; }

    [Column("ACCEPT_PREPAIND_FLAG", TypeName = "NUMBER(1)")]
    public bool? AcceptPrepaindFlag { get; set; }

    [Column("PERIOD_PREPAID_VALUE", TypeName = "NUMBER(18,2)")]
    public decimal? PeriodPrepaidValue { get; set; }

    [Column("THIRD_PARTY_3000_FLAG", TypeName = "NUMBER(1)")]
    public bool? ThirdParty3000Flag { get; set; }

    [Column("THIRD_PARTY_3000_VALUE_KD", TypeName = "NUMBER(18,2)")]
    public decimal? ThirdParty3000ValueKd { get; set; }

    [Column("IMPORT_FLAG", TypeName = "NUMBER(1)")]
    public bool? ImportFlag { get; set; }

    [Column("IMPORT_COUNTRIES")]
    [Unicode(false)]
    public string? ImportCountries { get; set; }

    [Column("USED_GOODS_FLAG", TypeName = "NUMBER(1)")]
    public bool? UsedGoodsFlag { get; set; }

    [Column("USED_GOODS_PAYMENT_TYPE_ID", TypeName = "NUMBER")]
    public decimal? UsedGoodsPaymentTypeId { get; set; }

    [Column("RESIDENAT_CUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? ResidenatCustomerCountToyear { get; set; }

    [Column("NON_RESIDANT_CUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? NonResidantCustomerCountToyear { get; set; }

    [Column("KUWAIT_COMP_CUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? KuwaitCompCustomerCountToyear { get; set; }

    [Column("FORIGN_COMP_CUSTOMER_COUNT_TOYEAR", TypeName = "NUMBER")]
    public decimal? ForignCompCustomerCountToyear { get; set; }

    [Column("MULTI_CASH_TRANS_SYSTEM_FLAG", TypeName = "NUMBER(1)")]
    public bool? MultiCashTransSystemFlag { get; set; }

    [Column("MULTI_CASH_TRANS_SYSTEM_TYPE_ID", TypeName = "NUMBER")]
    public decimal? MultiCashTransSystemTypeId { get; set; }

    [Column("MULTI_CASH_TRANS_SYSTEM_TYPE_DETAILS")]
    [Unicode(false)]
    public string? MultiCashTransSystemTypeDetails { get; set; }

    [Column("CONCENT_1_FLAG", TypeName = "NUMBER(1)")]
    public bool? Concent1Flag { get; set; }

    [Column("CONCENT_2_FLAG", TypeName = "NUMBER(1)")]
    public bool? Concent2Flag { get; set; }

    [Column("MANAGER_CIVIL_ID")]
    [StringLength(200)]
    [Unicode(false)]
    public string? ManagerCivilId { get; set; }

    [Column("MANAGER_NAME")]
    [Unicode(false)]
    public string? ManagerName { get; set; }

    [Column("MANAGER_SIGNATURE", TypeName = "CLOB")]
    public string? ManagerSignature { get; set; }

    [Column("OFFICER_SIGNATURE", TypeName = "CLOB")]
    public string? OfficerSignature { get; set; }

    [Column("CREATED_ON", TypeName = "DATE")]
    public DateTime? CreatedOn { get; set; }

    [Column("CREATED_BY")]
    [StringLength(200)]
    [Unicode(false)]
    public string? CreatedBy { get; set; }

    [Column("UPDATED_ON", TypeName = "DATE")]
    public DateTime? UpdatedOn { get; set; }

    [Column("UPDATED_BY")]
    [StringLength(200)]
    [Unicode(false)]
    public string? UpdatedBy { get; set; }

    [Column("IS_ACTIVE", TypeName = "NUMBER(1)")]
    public bool? IsActive { get; set; }

    [Column("INACTIVE_ON", TypeName = "DATE")]
    public DateTime? InactiveOn { get; set; }

    [Column("INACTIVE_USER")]
    [StringLength(200)]
    [Unicode(false)]
    public string? InactiveUser { get; set; }

    [Column("COMPLIENCE_OFFICER_HIREDATE", TypeName = "DATE")]
    public DateTime? ComplienceOfficerHiredate { get; set; }

    [Column("THIRD_PARTY_FLAG", TypeName = "NUMBER")]
    public decimal? ThirdPartyFlag { get; set; }

    [Column("THIRD_PARTY_VALUE", TypeName = "NUMBER")]
    public decimal? ThirdPartyValue { get; set; }

    [Column("IS_CONDUCT_CUSTOMER_ARTICLE5_FLAG", TypeName = "NUMBER")]
    public decimal? IsConductCustomerArticle5Flag { get; set; }

    [Column("POLITICAL_CUSTOMERS_SYSTEM_TYPE", TypeName = "NUMBER")]
    public decimal? PoliticalCustomersSystemType { get; set; }

    [Column("LICN_TYPE")]
    [StringLength(2000)]
    [Unicode(false)]
    public string? LicnType { get; set; }

    [Column("SELL_TRANS_5000_TOYEAR_CASH_PERCENT", TypeName = "NUMBER")]
    public decimal? SellTrans5000ToyearCashPercent { get; set; }

    [Column("PERIOD_FOREIGN_MONEY_COUNTRY_DTLS")]
    [Unicode(false)]
    public string? PeriodForeignMoneyCountryDtls { get; set; }

    [Column("REQ_STATUS", TypeName = "NUMBER")]
    public decimal? ReqStatus { get; set; }

    [Column("QUESTIONNAIRE_RESULT", TypeName = "NUMBER")]
    public decimal? QuestionnaireResult { get; set; }

    [Column("ASSIGNED_EMP")]
    [StringLength(100)]
    [Unicode(false)]
    public string? AssignedEmp { get; set; }

    [Column("ASSIGNED_BY")]
    [StringLength(100)]
    [Unicode(false)]
    public string? AssignedBy { get; set; }

    [Column("ASSIGNED_ON", TypeName = "DATE")]
    public DateTime? AssignedOn { get; set; }
}
