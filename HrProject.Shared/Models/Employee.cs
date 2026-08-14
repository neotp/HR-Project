namespace HrProject.Shared.Models;

public sealed class Employee
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ThaiFullName { get; set; } = string.Empty;
    public string EnglishFullName { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public string Company { get; set; } = string.Empty;
    public string Division { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string SupervisorName { get; set; } = string.Empty;
    public string LeaveApproverName { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = string.Empty;
    public string WorkLocation { get; set; } = string.Empty;
    public string EmployeeStatus { get; set; } = string.Empty;
    public string ProfileImageDataUrl { get; set; } = string.Empty;
    public string PersonalMobile { get; set; } = string.Empty;
    public string HomePhone { get; set; } = string.Empty;
    public string Religion { get; set; } = string.Empty;
    public string BloodType { get; set; } = string.Empty;
    public string CurrentAddress { get; set; } = string.Empty;
    public string IdCardAddress { get; set; } = string.Empty;
    public string HouseRegistrationAddress { get; set; } = string.Empty;
    public string ResidenceProvince { get; set; } = string.Empty;
    public string EmergencyContactName { get; set; } = string.Empty;
    public string EmergencyContactPhone { get; set; } = string.Empty;
    public string EmergencyContactAddress { get; set; } = string.Empty;
    public string BuddyName { get; set; } = string.Empty;
    public string InternalExtension { get; set; } = string.Empty;
    public string DirectPhone { get; set; } = string.Empty;
    public string CompanyMobile { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string BusinessUnit { get; set; } = string.Empty;
    public string ProductsResponsible { get; set; } = string.Empty;
    public string FunctionalSupervisorName { get; set; } = string.Empty;
    public string ResponsibilityProvince { get; set; } = string.Empty;
    public string ChecklistType { get; set; } = string.Empty;
    public string WorkSchedule { get; set; } = string.Empty;
    public string JobCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public DateOnly? AppointmentDate { get; set; }
    public DateOnly? ProvidentFundStartDate { get; set; }
    public string WorkExperienceType { get; set; } = string.Empty;
    public bool? HasCompanyParking { get; set; }
    public string PreviousCompany { get; set; } = string.Empty;
    public string PreviousPosition { get; set; } = string.Empty;
    public string PreviousWorkPeriod { get; set; } = string.Empty;
    public string PreviousWorkDetails { get; set; } = string.Empty;
    public List<EmployeeWorkHistoryItem> WorkHistory { get; set; } = [];
    public bool? CanTravelUpcountry { get; set; }
    public string EducationLevel { get; set; } = string.Empty;
    public string EducationInstitution { get; set; } = string.Empty;
    public string EducationMajor { get; set; } = string.Empty;
    public string EducationGraduationYear { get; set; } = string.Empty;
    public List<EmployeeEducationItem> EducationHistory { get; set; } = [];
    public string TrainingCourse { get; set; } = string.Empty;
    public string TrainingOrganizer { get; set; } = string.Empty;
    public DateOnly? TrainingDate { get; set; }
    public string TrainingDetails { get; set; } = string.Empty;
    public List<EmployeeTrainingItem> TrainingHistory { get; set; } = [];
    public string FamilyMemberName { get; set; } = string.Empty;
    public string FamilyRelationship { get; set; } = string.Empty;
    public string FamilyPhone { get; set; } = string.Empty;
    public string FamilyOccupation { get; set; } = string.Empty;
    public string MaritalStatus { get; set; } = string.Empty;
    public bool? IsMarriageRegistered { get; set; }
    public string SpouseTitle { get; set; } = string.Empty;
    public string SpouseName { get; set; } = string.Empty;
    public DateOnly? MarriageDate { get; set; }
    public bool? SpouseHasIncome { get; set; }
    public string SpouseNationalId { get; set; } = string.Empty;
    public string SpousePassportId { get; set; } = string.Empty;
    public string SpousePassportName { get; set; } = string.Empty;
    public string SpousePassportFileName { get; set; } = string.Empty;
    public int UneducatedChildCount { get; set; }
    public int StudyingChildCount { get; set; }
    public decimal LifeInsuranceAmount { get; set; }
    public decimal ParentSupportDeductionAmount { get; set; }
    public decimal SpouseParentSupportDeductionAmount { get; set; }
    public string CurrentAddressMapUrl { get; set; } = string.Empty;

    public string FullName => $"{FirstName} {LastName}";
}

public sealed class EmployeeWorkHistoryItem
{
    public string Period { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
}

public sealed class EmployeeTrainingItem
{
    public string CourseName { get; set; } = string.Empty;
    public string TrainingPeriod { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public decimal Expense { get; set; }
    public string Certificate { get; set; } = string.Empty;
    public decimal ExamFee { get; set; }
}

public sealed class EmployeeEducationItem
{
    public string Level { get; set; } = string.Empty;
    public string Institution { get; set; } = string.Empty;
    public string Major { get; set; } = string.Empty;
    public string GraduationYear { get; set; } = string.Empty;
}
