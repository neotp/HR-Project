namespace HrProject.Shared.Models;

public sealed class EmployeeTaxDeductionDto
{
    public long? Id { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public int TaxYear { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string MaritalStatus { get; set; } = string.Empty;
    public bool? IsMarriageRegistered { get; set; }
    public string SpouseName { get; set; } = string.Empty;
    public bool? SpouseHasIncome { get; set; }
    public string SpouseNationalId { get; set; } = string.Empty;
    public int ChildCount { get; set; }
    public int ChildBornFrom2018Count { get; set; }
    public int DisabledDependentCount { get; set; }
    public bool SupportsFather { get; set; }
    public bool SupportsMother { get; set; }
    public bool SupportsSpouseFather { get; set; }
    public bool SupportsSpouseMother { get; set; }
    public decimal LifeInsuranceAmount { get; set; }
    public decimal HealthInsuranceAmount { get; set; }
    public decimal ParentHealthInsuranceAmount { get; set; }
    public decimal ProvidentFundAmount { get; set; }
    public decimal RetirementFundAmount { get; set; }
    public decimal SocialSecurityAmount { get; set; }
    public decimal InvestmentDeductionAmount { get; set; }
    public decimal DonationAmount { get; set; }
    public string OtherDeductionDescription { get; set; } = string.Empty;
    public decimal OtherDeductionAmount { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed record EmployeeTaxDeductionHistoryDto(
    long Id,
    string Action,
    string Status,
    string ChangedByName,
    DateTimeOffset ChangedAt);

